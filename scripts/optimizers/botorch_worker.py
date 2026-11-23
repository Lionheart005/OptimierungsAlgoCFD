#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
FluidForge external BoTorch optimizer worker.

Supports:
  --check
  --request <path> --response <path>
  or JSON over STDIN -> STDOUT

Protocol v1.0:
  - Input parameters are scaled internally to [0, 1]^d (torch.float64).
  - Objectives in History carry their physical values; Minimize objectives are
    negated internally because BoTorch maximizes objectives.
  - ConstraintViolations follow <= 0 feasible, > 0 violated (matches BoTorch).
"""

import warnings
warnings.filterwarnings("ignore")

import argparse
import json
import math
import random
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
import torch
import botorch
from botorch.acquisition import MCAcquisitionFunction
from botorch.acquisition.logei import qLogExpectedImprovement, qLogNoisyExpectedImprovement
from botorch.acquisition.multi_objective import (
    qLogNoisyExpectedHypervolumeImprovement,
    qNoisyExpectedHypervolumeImprovement,
)
from botorch.acquisition.multi_objective.objective import IdentityMCMultiOutputObjective
from botorch.acquisition.objective import GenericMCObjective
from botorch.fit import fit_gpytorch_mll
from botorch.models import ModelListGP, SingleTaskGP
from botorch.models.gp_regression_fidelity import SingleTaskMultiFidelityGP
from botorch.models.transforms import Standardize
from botorch.optim import optimize_acqf
from botorch.sampling.normal import SobolQMCNormalSampler
from botorch.utils.multi_objective.hypervolume import Hypervolume
from botorch.utils.multi_objective.pareto import is_non_dominated
from botorch.utils.transforms import concatenate_pending_points, t_batch_mode_transform
from gpytorch.mlls import ExactMarginalLogLikelihood, SumMarginalLogLikelihood

torch.set_default_dtype(torch.float64)


class FeasibilityProbabilityACQ(MCAcquisitionFunction):
    """MC acquisition maximizing log smooth probability of feasibility when no feasible points exist yet."""

    def __init__(self, model, sampler, constraint_indices: List[int], eta: float = 1e-2) -> None:
        super().__init__(model=model, sampler=sampler)
        self.constraint_indices = constraint_indices
        self.eta = eta

    @concatenate_pending_points
    @t_batch_mode_transform()
    def forward(self, X: torch.Tensor) -> torch.Tensor:
        posterior = self.model.posterior(X)
        samples = self.get_posterior_samples(posterior)  # (S, b, q, m)
        log_prob = torch.zeros(samples.shape[:-1], dtype=samples.dtype, device=samples.device)
        for idx in self.constraint_indices:
            # Feasible when g <= 0 -> -g / eta >= 0
            log_prob = log_prob + torch.nn.functional.logsigmoid(-samples[..., idx] / self.eta)
        # Log-sum-exp over MC samples, mean/max over q
        log_mean_prob = torch.logsumexp(log_prob, dim=0) - math.log(samples.shape[0])
        return log_mean_prob.mean(dim=-1)


def _set_seeds(seed: int) -> None:
    s = int(seed) if seed and seed > 0 else 42
    random.seed(s)
    np.random.seed(s % (2**32 - 1))
    torch.manual_seed(s)


def _to_unit(params_dict: Dict[str, float], param_specs: List[Dict[str, Any]]) -> Optional[List[float]]:
    vec: List[float] = []
    for p in param_specs:
        name = p["name"] if "name" in p else p["Name"]
        lo = float(p.get("min", p.get("Min", 0.0)))
        hi = float(p.get("max", p.get("Max", 1.0)))
        if name not in params_dict:
            return None
        val = float(params_dict[name])
        if not math.isfinite(val):
            return None
        span = hi - lo
        u = (val - lo) / span if span > 0 else 0.5
        vec.append(max(0.0, min(1.0, u)))
    return vec


def _from_unit(unit_vec: List[float], param_specs: List[Dict[str, Any]]) -> Dict[str, float]:
    out: Dict[str, float] = {}
    for i, p in enumerate(param_specs):
        name = p["name"] if "name" in p else p["Name"]
        lo = float(p.get("min", p.get("Min", 0.0)))
        hi = float(p.get("max", p.get("Max", 1.0)))
        u = max(0.0, min(1.0, float(unit_vec[i])))
        out[name] = max(lo, min(hi, lo + u * (hi - lo)))
    return out


def _get(obj: Dict[str, Any], camel: str, pascal: str, default: Any = None) -> Any:
    if camel in obj:
        return obj[camel]
    if pascal in obj:
        return obj[pascal]
    return default


def _fit_gp(train_X: torch.Tensor, train_Y: torch.Tensor) -> SingleTaskGP:
    gp = SingleTaskGP(train_X, train_Y, outcome_transform=Standardize(m=1))
    mll = ExactMarginalLogLikelihood(gp.likelihood, gp)
    fit_gpytorch_mll(mll)
    return gp


def _extract_ard_importances(gps: List[SingleTaskGP], param_names: List[str]) -> Dict[str, float]:
    d = len(param_names)
    if d == 0 or not gps:
        return {}

    accum = np.zeros(d, dtype=np.float64)
    count = 0
    for gp in gps:
        ls = None
        cov = getattr(gp, "covar_module", None)
        if cov is not None:
            if hasattr(cov, "base_kernel") and hasattr(cov.base_kernel, "lengthscale"):
                ls = cov.base_kernel.lengthscale
            elif hasattr(cov, "lengthscale"):
                ls = cov.lengthscale
        if ls is None:
            continue
        arr = ls.detach().cpu().numpy().reshape(-1)
        if arr.size >= d:
            # Multi-fidelity models have d+1 lengthscales; take first d design dims.
            inv = 1.0 / np.maximum(arr[:d], 1e-9)
            accum += inv
            count += 1

    if count == 0 or float(accum.sum()) <= 0.0:
        return {name: 1.0 / d for name in param_names}

    norm = accum / float(accum.sum())
    return {param_names[i]: float(norm[i]) for i in range(d)}


def _sobol_candidates(d: int, batch_size: int, seed: int, offset: int = 0) -> torch.Tensor:
    engine = torch.quasirandom.SobolEngine(dimension=d, scramble=True, seed=int(seed) if seed > 0 else 42)
    if offset > 0:
        engine.fast_forward(offset)
    return engine.draw(batch_size, dtype=torch.float64).clamp(0.0, 1.0)


def _compute_pareto_and_hv(
    history: List[Dict[str, Any]],
    obj_specs: List[Dict[str, Any]],
) -> Tuple[List[bool], Optional[float], Optional[torch.Tensor], Optional[torch.Tensor]]:
    m = len(obj_specs)
    n = len(history)
    pareto_mask = [False] * n
    if n == 0 or m == 0:
        return pareto_mask, None, None, None

    eligible_indices: List[int] = []
    max_rows: List[List[float]] = []

    for idx, obs in enumerate(history):
        status = str(_get(obs, "status", "Status", "Feasible"))
        if status != "Feasible":
            continue
        objs = _get(obs, "objectives", "Objectives", {}) or {}
        row: List[float] = []
        valid = True
        for spec in obj_specs:
            oname = _get(spec, "name", "Name", "")
            direction = str(_get(spec, "direction", "Direction", "Minimize")).lower()
            if oname not in objs:
                valid = False
                break
            val = float(objs[oname])
            if not math.isfinite(val):
                valid = False
                break
            row.append(-val if direction == "minimize" else val)
        if valid:
            eligible_indices.append(idx)
            max_rows.append(row)

    if not eligible_indices:
        return pareto_mask, None, None, None

    Y_feas = torch.tensor(max_rows, dtype=torch.float64)
    nd_mask = is_non_dominated(Y_feas)
    for k, orig_idx in enumerate(eligible_indices):
        pareto_mask[orig_idx] = bool(nd_mask[k].item())

    if m < 2:
        return pareto_mask, None, Y_feas, None

    # Reference point in maximization space
    ref_list: List[float] = []
    for k, spec in enumerate(obj_specs):
        direction = str(_get(spec, "direction", "Direction", "Minimize")).lower()
        ref_raw = _get(spec, "referencePoint", "ReferencePoint", None)
        if ref_raw is not None and math.isfinite(float(ref_raw)):
            r_val = float(ref_raw)
            ref_list.append(-r_val if direction == "minimize" else r_val)
        else:
            col = Y_feas[:, k]
            span = float((col.max() - col.min()).item())
            ref_list.append(float(col.min().item()) - 0.1 * max(span, 1e-3))

    ref_point = torch.tensor(ref_list, dtype=torch.float64)
    hv_calc = Hypervolume(ref_point=ref_point)
    pareto_Y = Y_feas[nd_mask]
    # Filter points strictly dominating reference point for Hypervolume
    valid_hv = (pareto_Y > ref_point).all(dim=-1)
    hv_val = float(hv_calc.compute(pareto_Y[valid_hv])) if valid_hv.any() else 0.0
    return pareto_mask, hv_val, Y_feas, ref_point


def solve_request(req: Dict[str, Any]) -> Dict[str, Any]:
    seed = int(_get(req, "seed", "Seed", 42))
    _set_seeds(seed)

    param_specs = _get(req, "parameters", "Parameters", []) or []
    obj_specs = _get(req, "objectives", "Objectives", []) or []
    con_specs = _get(req, "constraints", "Constraints", []) or []
    history = _get(req, "history", "History", []) or []
    batch_size = max(1, int(_get(req, "batchSize", "BatchSize", 1)))
    acquisition = str(_get(req, "acquisition", "Acquisition", "Auto") or "Auto").strip()
    multi_fidelity = bool(_get(req, "multiFidelity", "MultiFidelity", False))
    avail_fids = [float(f) for f in (_get(req, "availableFidelities", "AvailableFidelities", [1.0]) or [1.0])]
    target_fidelity = max(avail_fids) if avail_fids else 1.0

    d = len(param_specs)
    if d == 0:
        return {
            "protocolVersion": "1.0",
            "worker": "botorch_worker",
            "backendVersion": f"botorch={botorch.__version__}, torch={torch.__version__}",
            "candidates": [],
            "error": "No free parameters provided in request.",
        }

    param_names = [_get(p, "name", "Name", f"x{i}") for i, p in enumerate(param_specs)]
    obj_names = [_get(o, "name", "Name", f"f{i}") for i, o in enumerate(obj_specs)]
    con_names = [_get(c, "name", "Name", f"c{i}") for i, c in enumerate(con_specs)]
    M = len(obj_specs)
    K = len(con_specs)

    pareto_mask, hv_val, Y_feas, ref_point = _compute_pareto_and_hv(history, obj_specs)

    # Extract training data from History
    obj_X_list: List[List[float]] = []
    obj_fid_list: List[float] = []
    obj_Y_list: List[List[float]] = []
    obj_con_list: List[List[float]] = []
    obj_feasible_flags: List[bool] = []

    con_X_by_idx: List[List[List[float]]] = [[] for _ in range(K)]
    con_Y_by_idx: List[List[float]] = [[] for _ in range(K)]

    for obs in history:
        status = str(_get(obs, "status", "Status", "Feasible"))
        if status in ("Failed", "Disqualified"):
            continue
        p_dict = _get(obs, "parameters", "Parameters", {}) or {}
        u = _to_unit(p_dict, param_specs)
        if u is None:
            continue

        fid = float(_get(obs, "fidelity", "Fidelity", 1.0))
        cons = _get(obs, "constraintViolations", "ConstraintViolations", {}) or {}

        # Constraint GPs learn on ALL non-crashed points with this constraint (including InfeasiblePreSolve!)
        for k_idx, cname in enumerate(con_names):
            if cname in cons:
                cval = float(cons[cname])
                if math.isfinite(cval):
                    con_X_by_idx[k_idx].append(u)
                    con_Y_by_idx[k_idx].append(cval)

        if status == "InfeasiblePreSolve":
            continue

        objs = _get(obs, "objectives", "Objectives", {}) or {}
        if M == 0:
            fit = _get(obs, "fitness", "Fitness", None)
            if fit is None or not math.isfinite(float(fit)):
                continue
            y_vec = [float(fit)]
        else:
            y_vec = []
            ok = True
            for spec in obj_specs:
                oname = _get(spec, "name", "Name", "")
                direction = str(_get(spec, "direction", "Direction", "Minimize")).lower()
                if oname not in objs:
                    ok = False
                    break
                val = float(objs[oname])
                if not math.isfinite(val):
                    ok = False
                    break
                y_vec.append(-val if direction == "minimize" else val)
            if not ok:
                continue

        # Collect constraint values aligned with objective rows (default 0.0 if missing)
        c_row: List[float] = []
        is_feas = status == "Feasible"
        for cname in con_names:
            cv = float(cons.get(cname, 0.0)) if cname in cons else (0.0 if is_feas else 1.0)
            if not math.isfinite(cv):
                cv = 1.0
            c_row.append(cv)
            if cv > 0.0:
                is_feas = False

        obj_X_list.append(u)
        obj_fid_list.append(fid)
        obj_Y_list.append(y_vec)
        obj_con_list.append(c_row)
        obj_feasible_flags.append(is_feas)

    eff_M = max(1, M)
    min_init = max(2 * d, 4)
    ard_importances: Dict[str, float] = {}
    diagnostics: Dict[str, str] = {
        "valid_objective_points": str(len(obj_X_list)),
        "feasible_points": str(sum(1 for f in obj_feasible_flags if f)),
    }

    bounds = torch.stack([torch.zeros(d, dtype=torch.float64), torch.ones(d, dtype=torch.float64)])
    cand_fidelities = [target_fidelity] * batch_size
    pred_objs_list: List[Dict[str, float]] = [{} for _ in range(batch_size)]
    pred_stds_list: List[Dict[str, float]] = [{} for _ in range(batch_size)]
    pred_feas_list: List[Optional[float]] = [None] * batch_size

    # Cold start -> deterministic Sobol DoE
    if len(obj_X_list) < min_init:
        diagnostics["strategy"] = "Sobol_ColdStart"
        X_cand = _sobol_candidates(d, batch_size, seed=seed, offset=len(history))
    else:
        train_X = torch.tensor(obj_X_list, dtype=torch.float64)
        train_Y = torch.tensor(obj_Y_list, dtype=torch.float64)

        distinct_fids = len(set(round(f, 6) for f in obj_fid_list))
        use_mf = multi_fidelity and distinct_fids >= 2 and len(obj_X_list) >= min_init + 2

        # Fit objective GPs
        obj_gps: List[SingleTaskGP] = []
        if use_mf and eff_M == 1:
            train_X_mf = torch.cat(
                [train_X, torch.tensor(obj_fid_list, dtype=torch.float64).unsqueeze(-1)],
                dim=-1,
            )
            try:
                mf_gp = SingleTaskMultiFidelityGP(
                    train_X_mf,
                    train_Y[:, :1],
                    data_fidelities=[-1],
                    outcome_transform=Standardize(m=1),
                )
                fit_gpytorch_mll(ExactMarginalLogLikelihood(mf_gp.likelihood, mf_gp))
                obj_gps.append(mf_gp)
                diagnostics["multi_fidelity_gp"] = "active"
            except Exception:
                obj_gps.append(_fit_gp(train_X, train_Y[:, :1]))
                use_mf = False
        else:
            use_mf = False
            for m_idx in range(eff_M):
                obj_gps.append(_fit_gp(train_X, train_Y[:, m_idx : m_idx + 1]))

        ard_importances = _extract_ard_importances(obj_gps, param_names)

        # Fit constraint GPs (using all non-crashed constraint observations)
        con_gps: List[SingleTaskGP] = []
        active_con_indices: List[int] = []
        for k_idx in range(K):
            if len(con_X_by_idx[k_idx]) >= max(d + 1, 3):
                cX = torch.tensor(con_X_by_idx[k_idx], dtype=torch.float64)
                cY = torch.tensor(con_Y_by_idx[k_idx], dtype=torch.float64).unsqueeze(-1)
                con_gps.append(_fit_gp(cX, cY))
                active_con_indices.append(k_idx)

        sampler = SobolQMCNormalSampler(sample_shape=torch.Size([128]), seed=seed)
        num_restarts = 8
        raw_samples = 128

        has_feasible = any(obj_feasible_flags)
        if con_gps and not has_feasible:
            # Pure feasibility search when 0 feasible CFD evaluations exist yet
            diagnostics["strategy"] = "FeasibilitySearch"
            feas_model = ModelListGP(*con_gps) if len(con_gps) > 1 else con_gps[0]
            acq = FeasibilityProbabilityACQ(
                model=feas_model,
                sampler=sampler,
                constraint_indices=list(range(len(con_gps))),
            )
            X_cand, _ = optimize_acqf(
                acq_function=acq,
                bounds=bounds,
                q=batch_size,
                num_restarts=num_restarts,
                raw_samples=raw_samples,
                sequential=True,
            )
        elif eff_M == 1:
            # Single-objective constrained/unconstrained BO
            if con_gps:
                model = ModelListGP(obj_gps[0], *con_gps)
                constraints = [lambda Z, idx=1 + i: Z[..., idx] for i in range(len(con_gps))]
                objective = GenericMCObjective(lambda Z, X=None: Z[..., 0])
            else:
                model = obj_gps[0]
                constraints = None
                objective = None

            use_qlogei = acquisition.lower() == "qlogei" and not con_gps and not use_mf
            if use_qlogei:
                diagnostics["strategy"] = "qLogEI"
                best_f = train_Y[:, 0].max()
                acq = qLogExpectedImprovement(model=model, best_f=best_f)
                X_cand, _ = optimize_acqf(
                    acq_function=acq,
                    bounds=bounds,
                    q=batch_size,
                    num_restarts=num_restarts,
                    raw_samples=raw_samples,
                    sequential=True,
                )
            elif use_mf:
                diagnostics["strategy"] = "qLogNEI_MultiFidelity"
                acq = qLogNoisyExpectedImprovement(
                    model=obj_gps[0],
                    X_baseline=torch.cat(
                        [train_X, torch.tensor(obj_fid_list, dtype=torch.float64).unsqueeze(-1)],
                        dim=-1,
                    ),
                    sampler=sampler,
                    prune_baseline=True,
                )
                mf_bounds = torch.cat(
                    [
                        bounds,
                        torch.tensor([[min(avail_fids)], [max(avail_fids)]], dtype=torch.float64),
                    ],
                    dim=1,
                )
                X_mf, _ = optimize_acqf(
                    acq_function=acq,
                    bounds=mf_bounds,
                    q=batch_size,
                    num_restarts=num_restarts,
                    raw_samples=raw_samples,
                    sequential=True,
                )
                X_cand = X_mf[:, :d]
                cand_fidelities = [
                    min(avail_fids, key=lambda af: abs(af - float(f_raw)))
                    for f_raw in X_mf[:, d].tolist()
                ]
            else:
                diagnostics["strategy"] = "qLogNEI_Constrained" if constraints else "qLogNEI"
                acq = qLogNoisyExpectedImprovement(
                    model=model,
                    X_baseline=train_X,
                    sampler=sampler,
                    objective=objective,
                    constraints=constraints,
                    prune_baseline=True,
                )
                X_cand, _ = optimize_acqf(
                    acq_function=acq,
                    bounds=bounds,
                    q=batch_size,
                    num_restarts=num_restarts,
                    raw_samples=raw_samples,
                    sequential=True,
                )
        else:
            # Multi-objective constrained/unconstrained BO (qLogNEHVI)
            if con_gps:
                model = ModelListGP(*obj_gps, *con_gps)
                constraints = [lambda Z, idx=eff_M + i: Z[..., idx] for i in range(len(con_gps))]
                objective = IdentityMCMultiOutputObjective(outcomes=list(range(eff_M)))
            else:
                model = ModelListGP(*obj_gps)
                constraints = None
                objective = IdentityMCMultiOutputObjective(outcomes=list(range(eff_M)))

            if ref_point is None:
                col_min = train_Y.min(dim=0).values
                col_span = train_Y.max(dim=0).values - col_min
                ref_point = col_min - 0.1 * torch.clamp(col_span, min=1e-3)

            diagnostics["strategy"] = "qLogNEHVI_Constrained" if constraints else "qLogNEHVI"
            try:
                acq = qLogNoisyExpectedHypervolumeImprovement(
                    model=model,
                    ref_point=ref_point.tolist(),
                    X_baseline=train_X,
                    sampler=sampler,
                    objective=objective,
                    constraints=constraints,
                    prune_baseline=True,
                )
            except Exception:
                diagnostics["strategy"] = "qNEHVI_Fallback"
                acq = qNoisyExpectedHypervolumeImprovement(
                    model=model,
                    ref_point=ref_point.tolist(),
                    X_baseline=train_X,
                    sampler=sampler,
                    objective=objective,
                    constraints=constraints,
                    prune_baseline=True,
                )

            X_cand, _ = optimize_acqf(
                acq_function=acq,
                bounds=bounds,
                q=batch_size,
                num_restarts=num_restarts,
                raw_samples=raw_samples,
                sequential=True,
            )

        # Posterior predictions at candidate locations
        with torch.no_grad():
            X_eval = (
                torch.cat(
                    [X_cand, torch.tensor(cand_fidelities, dtype=torch.float64).unsqueeze(-1)],
                    dim=-1,
                )
                if use_mf
                else X_cand
            )
            for m_idx, gp in enumerate(obj_gps):
                post = gp.posterior(X_eval)
                mean = post.mean.squeeze(-1).cpu().tolist()
                std = post.variance.clamp_min(0.0).sqrt().squeeze(-1).cpu().tolist()
                if not isinstance(mean, list):
                    mean = [mean]
                if not isinstance(std, list):
                    std = [std]
                oname = obj_names[m_idx] if m_idx < len(obj_names) else "Fitness"
                direction = (
                    str(_get(obj_specs[m_idx], "direction", "Direction", "Maximize")).lower()
                    if m_idx < len(obj_specs)
                    else "maximize"
                )
                sign = -1.0 if direction == "minimize" else 1.0
                for b_idx in range(batch_size):
                    pred_objs_list[b_idx][oname] = sign * float(mean[b_idx])
                    pred_stds_list[b_idx][oname] = float(std[b_idx])

            if con_gps:
                normal = torch.distributions.Normal(
                    torch.tensor(0.0, dtype=torch.float64),
                    torch.tensor(1.0, dtype=torch.float64),
                )
                probs = torch.ones(batch_size, dtype=torch.float64)
                for cgp in con_gps:
                    cpost = cgp.posterior(X_cand)
                    cmean = cpost.mean.squeeze(-1)
                    cstd = cpost.variance.clamp_min(1e-12).sqrt().squeeze(-1)
                    probs = probs * normal.cdf(-cmean / cstd)
                for b_idx in range(batch_size):
                    pred_feas_list[b_idx] = float(probs[b_idx].item())

    candidates: List[Dict[str, Any]] = []
    X_np = X_cand.detach().cpu().numpy()
    for i in range(batch_size):
        candidates.append(
            {
                "parameters": _from_unit(X_np[i].tolist(), param_specs),
                "fidelity": float(cand_fidelities[i]),
                "predictedObjectives": pred_objs_list[i],
                "predictedStd": pred_stds_list[i],
                "predictedFeasibilityProb": pred_feas_list[i],
            }
        )

    return {
        "protocolVersion": "1.0",
        "worker": "botorch_worker",
        "backendVersion": f"botorch={botorch.__version__}, torch={torch.__version__}",
        "candidates": candidates,
        "ardImportances": ard_importances,
        "paretoMask": pareto_mask,
        "hypervolume": hv_val,
        "diagnostics": diagnostics,
        "error": None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="FluidForge BoTorch Optimizer Worker")
    parser.add_argument("--check", action="store_true", help="Verify environment & package availability")
    parser.add_argument("--request", type=str, default=None, help="Path to request JSON file")
    parser.add_argument("--response", type=str, default=None, help="Path to response JSON file")
    args = parser.parse_args()

    if args.check:
        info = {
            "status": "ok",
            "worker": "botorch_worker",
            "torch": torch.__version__,
            "botorch": botorch.__version__,
        }
        print(json.dumps(info))
        return 0

    try:
        if args.request:
            req = json.loads(Path(args.request).read_text(encoding="utf-8"))
        else:
            req = json.loads(sys.stdin.read())

        resp = solve_request(req)
        payload = json.dumps(resp, indent=2)

        if args.response:
            out_path = Path(args.response)
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_text(payload, encoding="utf-8")
        else:
            print(payload)
        return 0 if not resp.get("error") else 1
    except Exception as exc:
        err_resp = {
            "protocolVersion": "1.0",
            "worker": "botorch_worker",
            "backendVersion": f"botorch={botorch.__version__}, torch={torch.__version__}",
            "candidates": [],
            "error": f"{type(exc).__name__}: {exc}",
        }
        payload = json.dumps(err_resp, indent=2)
        if args.response:
            out_path = Path(args.response)
            out_path.parent.mkdir(parents=True, exist_ok=True)
            out_path.write_text(payload, encoding="utf-8")
        else:
            print(payload)
        return 1


if __name__ == "__main__":
    sys.exit(main())
