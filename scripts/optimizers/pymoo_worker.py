#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
FluidForge external pymoo (NSGA-II / NSGA-III) evolutionary optimizer worker.

Supports:
  --check
  --request <path> --response <path>
  or JSON over STDIN -> STDOUT

Protocol v1.0:
  - Parameters scaled internally to [0, 1]^d.
  - Objectives minimized internally (Maximize objectives negated).
  - ConstraintViolations follow <= 0 feasible, > 0 violated (matches pymoo G <= 0).
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
import pymoo
from pymoo.algorithms.moo.nsga2 import NSGA2
from pymoo.algorithms.moo.nsga3 import NSGA3
from pymoo.core.population import Population
from pymoo.core.problem import Problem
from pymoo.operators.crossover.sbx import SBX
from pymoo.operators.mutation.pm import PM
from pymoo.operators.sampling.lhs import LHS
from pymoo.util.nds.non_dominated_sorting import NonDominatedSorting
from pymoo.util.ref_dirs import get_reference_directions


def _get(obj: Dict[str, Any], camel: str, pascal: str, default: Any = None) -> Any:
    if camel in obj:
        return obj[camel]
    if pascal in obj:
        return obj[pascal]
    return default


def _set_seeds(seed: int) -> None:
    s = int(seed) if seed and seed > 0 else 42
    random.seed(s)
    np.random.seed(s % (2**32 - 1))


def _to_unit(params_dict: Dict[str, float], param_specs: List[Dict[str, Any]]) -> Optional[List[float]]:
    vec: List[float] = []
    for p in param_specs:
        name = _get(p, "name", "Name", "")
        lo = float(_get(p, "min", "Min", 0.0))
        hi = float(_get(p, "max", "Max", 1.0))
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
        name = _get(p, "name", "Name", "")
        lo = float(_get(p, "min", "Min", 0.0))
        hi = float(_get(p, "max", "Max", 1.0))
        u = max(0.0, min(1.0, float(unit_vec[i])))
        out[name] = max(lo, min(hi, lo + u * (hi - lo)))
    return out


def _compute_pareto_and_hv(
    history: List[Dict[str, Any]],
    obj_specs: List[Dict[str, Any]],
) -> Tuple[List[bool], Optional[float]]:
    n = len(history)
    m = len(obj_specs)
    pareto_mask = [False] * n
    if n == 0 or m == 0:
        return pareto_mask, None

    eligible: List[int] = []
    rows: List[List[float]] = []

    for idx, obs in enumerate(history):
        if str(_get(obs, "status", "Status", "Feasible")) != "Feasible":
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
            row.append(-val if direction == "maximize" else val)
        if valid:
            eligible.append(idx)
            rows.append(row)

    if not eligible:
        return pareto_mask, None

    F = np.asarray(rows, dtype=np.float64)
    fronts = NonDominatedSorting().do(F)
    nd_idx = fronts[0] if len(fronts) > 0 else np.array([], dtype=int)
    for k in nd_idx:
        pareto_mask[eligible[int(k)]] = True

    if m < 2:
        return pareto_mask, None

    ref_vals: List[float] = []
    for k, spec in enumerate(obj_specs):
        direction = str(_get(spec, "direction", "Direction", "Minimize")).lower()
        ref_raw = _get(spec, "referencePoint", "ReferencePoint", None)
        if ref_raw is not None and math.isfinite(float(ref_raw)):
            rv = float(ref_raw)
            ref_vals.append(-rv if direction == "maximize" else rv)
        else:
            col = F[:, k]
            span = float(col.max() - col.min())
            ref_vals.append(float(col.max()) + 0.1 * max(span, 1e-3))

    ref = np.asarray(ref_vals, dtype=np.float64)
    try:
        from pymoo.indicators.hv import HV
        hv_val = float(HV(ref_point=ref)(F[nd_idx]))
    except Exception:
        # Exact 2D fallback
        if m == 2:
            pts = sorted([(float(p[0]), float(p[1])) for p in F[nd_idx] if p[0] < ref[0] and p[1] < ref[1]])
            hv_val = 0.0
            best_y = float(ref[1])
            for x, y in pts:
                if y < best_y:
                    hv_val += (float(ref[0]) - x) * (best_y - y)
                    best_y = y
        else:
            hv_val = 0.0

    return pareto_mask, hv_val


class _SurrogateHistoryProblem(Problem):
    def __init__(self, n_var: int, n_obj: int, n_ieq_constr: int) -> None:
        super().__init__(
            n_var=n_var,
            n_obj=n_obj,
            n_ieq_constr=n_ieq_constr,
            xl=np.zeros(n_var, dtype=np.float64),
            xu=np.ones(n_var, dtype=np.float64),
        )

    def _evaluate(self, X, out, *args, **kwargs):
        # Not called directly; historical observations are fed via Population.new(...)
        out["F"] = np.zeros((len(X), self.n_obj), dtype=np.float64)
        if self.n_ieq_constr > 0:
            out["G"] = np.zeros((len(X), self.n_ieq_constr), dtype=np.float64)


def solve_request(req: Dict[str, Any]) -> Dict[str, Any]:
    seed = int(_get(req, "seed", "Seed", 42))
    _set_seeds(seed)

    param_specs = _get(req, "parameters", "Parameters", []) or []
    obj_specs = _get(req, "objectives", "Objectives", []) or []
    con_specs = _get(req, "constraints", "Constraints", []) or []
    history = _get(req, "history", "History", []) or []
    batch_size = max(1, int(_get(req, "batchSize", "BatchSize", 1)))
    acquisition = str(_get(req, "acquisition", "Acquisition", "Auto") or "Auto").strip()

    d = len(param_specs)
    if d == 0:
        return {
            "protocolVersion": "1.0",
            "worker": "pymoo_worker",
            "backendVersion": f"pymoo={pymoo.__version__}",
            "candidates": [],
            "error": "No free parameters provided in request.",
        }

    M = max(1, len(obj_specs))
    K = len(con_specs)
    con_names = [_get(c, "name", "Name", f"c{i}") for i, c in enumerate(con_specs)]

    pareto_mask, hv_val = _compute_pareto_and_hv(history, obj_specs)

    X_rows: List[List[float]] = []
    F_rows: List[List[float]] = []
    G_rows: List[List[float]] = []

    for obs in history:
        status = str(_get(obs, "status", "Status", "Feasible"))
        p_dict = _get(obs, "parameters", "Parameters", {}) or {}
        u = _to_unit(p_dict, param_specs)
        if u is None:
            continue

        objs = _get(obs, "objectives", "Objectives", {}) or {}
        cons = _get(obs, "constraintViolations", "ConstraintViolations", {}) or {}

        # Extract objectives (minimization space)
        if len(obj_specs) == 0:
            fit = _get(obs, "fitness", "Fitness", None)
            if fit is None or not math.isfinite(float(fit)):
                f_row = [1e6]
            else:
                f_row = [-float(fit)]
        else:
            f_row = []
            valid = True
            for spec in obj_specs:
                oname = _get(spec, "name", "Name", "")
                direction = str(_get(spec, "direction", "Direction", "Minimize")).lower()
                if oname not in objs or not math.isfinite(float(objs[oname])):
                    valid = False
                    break
                val = float(objs[oname])
                f_row.append(-val if direction == "maximize" else val)
            if not valid:
                # Penalty objective for failed / pre-constraint infeasible entries
                f_row = [1e6] * M

        # Constraint violations <= 0 feasible, > 0 violated
        g_row: List[float] = []
        for cname in con_names:
            if cname in cons and math.isfinite(float(cons[cname])):
                g_row.append(float(cons[cname]))
            else:
                g_row.append(0.0 if status == "Feasible" else 1.0)

        if status in ("Failed", "Disqualified") and K > 0 and all(gv <= 0.0 for gv in g_row):
            g_row[0] = 1.0

        X_rows.append(u)
        F_rows.append(f_row)
        if K > 0:
            G_rows.append(g_row)

    use_nsga3 = acquisition.upper() == "NSGA3" or (acquisition.upper() == "AUTO" and M >= 3)
    diagnostics: Dict[str, str] = {
        "history_points": str(len(X_rows)),
        "operator": "NSGA3" if use_nsga3 else "NSGA2",
    }

    problem = _SurrogateHistoryProblem(n_var=d, n_obj=M, n_ieq_constr=K)

    if len(X_rows) < max(batch_size, 4):
        # Cold start via pymoo LHS sampling
        diagnostics["strategy"] = "LHS_ColdStart"
        X_cand = LHS()._do(problem, batch_size, random_state=np.random.RandomState(seed))
    else:
        pop_size = max(batch_size, min(len(X_rows), max(12, batch_size * 2)))
        if use_nsga3:
            n_partitions = max(2, min(12, pop_size))
            ref_dirs = get_reference_directions("das-dennis", M, n_partitions=n_partitions)
            algo = NSGA3(
                ref_dirs=ref_dirs,
                pop_size=max(pop_size, len(ref_dirs)),
                n_offsprings=batch_size,
                sampling=LHS(),
                crossover=SBX(prob=0.9, eta=15),
                mutation=PM(eta=20),
                eliminate_duplicates=True,
            )
        else:
            algo = NSGA2(
                pop_size=pop_size,
                n_offsprings=batch_size,
                sampling=LHS(),
                crossover=SBX(prob=0.9, eta=15),
                mutation=PM(eta=20),
                eliminate_duplicates=True,
            )

        algo.setup(problem, seed=seed, verbose=False)
        X_arr = np.asarray(X_rows, dtype=np.float64)
        F_arr = np.asarray(F_rows, dtype=np.float64)
        if K > 0:
            G_arr = np.asarray(G_rows, dtype=np.float64)
            CV = np.sum(np.maximum(G_arr, 0.0), axis=1, keepdims=True)
            pop = Population.new(X=X_arr, F=F_arr, G=G_arr, CV=CV)
        else:
            pop = Population.new(X=X_arr, F=F_arr)

        # Feed current evaluated history in chunks of pop_size so survival + mating produce real offspring
        # First tell initializes algo.pop; subsequent tell advances generation cleanly.
        init_pop = algo.ask()
        if len(init_pop) > 0 and len(pop) >= len(init_pop):
            # Replace initial population directly with evaluated history
            algo.pop = pop
            if algo.n_gen is None or algo.n_gen == 0:
                algo.n_gen = 1
            algo.advance(infills=pop)
        else:
            algo.tell(infills=pop[: len(init_pop)])

        offspring = algo.ask()
        X_cand = offspring.get("X")
        if X_cand is None or len(X_cand) == 0:
            X_cand = LHS()._do(problem, batch_size, random_state=np.random.RandomState(seed))
        elif len(X_cand) < batch_size:
            extra = LHS()._do(problem, batch_size - len(X_cand), random_state=np.random.RandomState(seed + 1))
            X_cand = np.vstack([X_cand, extra])
        X_cand = np.clip(X_cand[:batch_size], 0.0, 1.0)
        diagnostics["strategy"] = diagnostics["operator"]

    candidates: List[Dict[str, Any]] = []
    for i in range(batch_size):
        candidates.append(
            {
                "parameters": _from_unit(X_cand[i].tolist(), param_specs),
                "fidelity": 1.0,
                "predictedObjectives": {},
                "predictedStd": {},
                "predictedFeasibilityProb": None,
            }
        )

    return {
        "protocolVersion": "1.0",
        "worker": "pymoo_worker",
        "backendVersion": f"pymoo={pymoo.__version__}",
        "candidates": candidates,
        "ardImportances": {},
        "paretoMask": pareto_mask,
        "hypervolume": hv_val,
        "diagnostics": diagnostics,
        "error": None,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="FluidForge pymoo (NSGA-II / NSGA-III) Optimizer Worker")
    parser.add_argument("--check", action="store_true", help="Verify environment & package availability")
    parser.add_argument("--request", type=str, default=None, help="Path to request JSON file")
    parser.add_argument("--response", type=str, default=None, help="Path to response JSON file")
    args = parser.parse_args()

    if args.check:
        info = {
            "status": "ok",
            "worker": "pymoo_worker",
            "pymoo": pymoo.__version__,
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
            "worker": "pymoo_worker",
            "backendVersion": f"pymoo={pymoo.__version__}",
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
