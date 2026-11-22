#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
Analysis and visualization of SU2 convergence histories (history.csv).

Supports three modes:
  Mode A: Summary table across all variants in a directory (default without --var and --compare).
  Mode B: Detailed 3-panel convergence plot for a single variant (saved next to the CSV).
  Mode C: Overlay comparison plot of multiple variants (--compare).
"""

import sys
import os
import argparse
import json
from pathlib import Path

# Activate headless backend 'Agg' unless --show is passed
if "--show" not in sys.argv:
    import matplotlib
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import pandas as pd
import numpy as np


# Standard prefixes for aerodynamic coefficients in SU2
KNOWN_AERO_PREFIXES = ["CD", "CL", "CSF", "CMX", "CMY", "CMZ", "CEFF"]


def parse_export_formats(args):
    """Determine export image formats from --formats and --vector."""
    if getattr(args, "vector", False):
        return ["png", "pdf", "svg"]
    if getattr(args, "formats", None):
        formats = [f.strip().lower().lstrip(".") for f in args.formats.split(",") if f.strip()]
        return formats if formats else ["png"]
    return ["png"]


def save_figure_multi_formats(fig, base_path, formats, dpi=150):
    """Save a Matplotlib figure in multiple formats."""
    base = Path(base_path)
    if base.suffix.lower() in [".png", ".pdf", ".svg", ".jpg", ".jpeg"]:
        base = base.with_suffix("")
    saved = []
    for fmt in formats:
        out_file = base.with_suffix(f".{fmt}")
        fig.savefig(out_file, dpi=dpi, bbox_inches="tight")
        saved.append(out_file)
        print(f"Saved plot: {out_file}")
    return saved


def parse_su2_cfg(cfg_path):
    """Read ITER, CONV_CAUCHY_ELEMS, and CONV_CAUCHY_EPS from an SU2 .cfg file."""
    params = {}
    if not os.path.isfile(cfg_path):
        return params

    try:
        with open(cfg_path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                clean_line = line.split("%")[0].strip()
                if not clean_line or "=" not in clean_line:
                    continue
                key, val = clean_line.split("=", 1)
                key = key.strip().upper()
                val = val.strip().strip("\"'").strip()
                if key == "ITER":
                    try:
                        params["max_iter"] = int(val)
                    except ValueError:
                        pass
                elif key == "CONV_CAUCHY_ELEMS":
                    try:
                        params["cauchy_elems"] = int(val)
                    except ValueError:
                        pass
                elif key == "CONV_CAUCHY_EPS":
                    try:
                        params["cauchy_eps"] = float(val)
                    except ValueError:
                        pass
    except Exception:
        pass
    return params


def parse_effective_config_json(json_path):
    """Recursively search for Su2MaxIterations, CauchyElements, and CauchyTolerance in effective-config.json."""
    params = {}
    if not os.path.isfile(json_path):
        return params

    try:
        with open(json_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        def walk(node):
            if isinstance(node, dict):
                for k, v in node.items():
                    k_lower = k.lower()
                    if k_lower in ("su2maxiterations", "iter", "maxiterations") and "max_iter" not in params:
                        try:
                            params["max_iter"] = int(v)
                        except (ValueError, TypeError):
                            pass
                    elif k_lower in ("cauchyelements", "conv_cauchy_elems") and "cauchy_elems" not in params:
                        try:
                            params["cauchy_elems"] = int(v)
                        except (ValueError, TypeError):
                            pass
                    elif k_lower in ("cauchytolerance", "conv_cauchy_eps") and "cauchy_eps" not in params:
                        try:
                            params["cauchy_eps"] = float(v)
                        except (ValueError, TypeError):
                            pass
                    walk(v)
            elif isinstance(node, list):
                for item in node:
                    walk(item)

        walk(data)
    except Exception:
        pass
    return params


def resolve_run_config(csv_path_or_dir):
    """Resolve ITER, CONV_CAUCHY_ELEMS, and CONV_CAUCHY_EPS for the given file or folder."""
    target = Path(csv_path_or_dir)
    folder = target if target.is_dir() else target.parent

    # 1. Specific config next to CSV (e.g., Config_Gen1_Var2.cfg for History_Gen1_Var2.csv)
    if target.is_file():
        stem = target.stem
        cfg_name = stem.replace("History_", "Config_") + ".cfg"
        specific_cfg = folder / cfg_name
        if specific_cfg.is_file():
            params = parse_su2_cfg(specific_cfg)
            if params:
                return params

    # 2. Any Config_*.cfg in the same folder
    for cfg in sorted(folder.glob("Config_*.cfg")):
        params = parse_su2_cfg(cfg)
        if params:
            return params

    # 3. effective-config.json in the folder or parent directories
    curr = folder.resolve()
    for _ in range(4):
        eff = curr / "effective-config.json"
        if eff.is_file():
            params = parse_effective_config_json(eff)
            if params:
                return params
        if curr.parent == curr:
            break
        curr = curr.parent

    return {}


def read_history_csv(csv_path):
    """Read an SU2 history CSV, clean column names, and coerce values to numeric."""
    df = pd.read_csv(csv_path, skipinitialspace=True)
    df.columns = [str(c).strip().strip("\"'").strip() for c in df.columns]
    df = df.loc[:, ~df.columns.str.match(r"^Unnamed")]
    df = df.dropna(how="all")
    for col in df.columns:
        df[col] = pd.to_numeric(df[col], errors="coerce")
    return df


def find_iter_column(df):
    """Return the iteration column name (Inner_Iter, ITER, Iter, Iteration)."""
    for candidate in ["Inner_Iter", "ITER", "Iter", "Iteration"]:
        for col in df.columns:
            if col.lower() == candidate.lower():
                return col
    return None


def resolve_x_axis(df):
    """
    Determine x-axis values and label:
    If URANS was run (varying Cur_Time or Time_Iter), use physical time or time step;
    otherwise use iteration index.
    """
    # SU2 7.5.1 also writes a constant Time_Iter=0 for steady runs, so only treat it as
    # unsteady when the column actually varies.
    def varies(col):
        return df[col].dropna().nunique() > 1

    for col in df.columns:
        if col.lower() in ("cur_time", "curtime", "time", "physical_time") and varies(col):
            return df[col], "Physical Time [s]"
    for col in df.columns:
        if col.lower() in ("time_iter", "timeiter", "time_iteration") and varies(col):
            return df[col], "Time Step (Time_Iter)"
    iter_col = find_iter_column(df)
    if iter_col and iter_col in df.columns:
        return df[iter_col], "Iteration"
    return pd.Series(range(len(df))), "Iteration"


def get_iterations_count(df, iter_col):
    """Determine number of iterations executed (SU2 uses 0-based indexing)."""
    if iter_col and iter_col in df.columns:
        series = df[iter_col].dropna()
        if len(series) > 0:
            last_val = series.iloc[-1]
            try:
                val_int = int(last_val)
                if val_int >= 0:
                    return val_int + 1
            except (ValueError, TypeError):
                pass
    return len(df)


def find_rms_columns(df):
    """Return all columns starting with 'rms['."""
    return [c for c in df.columns if c.lower().startswith("rms[")]


def compute_residual_drop(df, rms_cols):
    """Compute residual drop in decades (first row minus last row; SU2 RMS is log10)."""
    if not rms_cols:
        return float("nan")
    first_col = rms_cols[0]
    series = df[first_col].dropna()
    if len(series) < 2:
        return float("nan")
    return float(series.iloc[0] - series.iloc[-1])


def detect_metrics(df, requested_metrics=None):
    """
    Identify relevant metrics. Uses requested_metrics if provided; otherwise
    auto-detects known aerodynamic coefficients (CD, CL, CSF, CMx, CMy, CMz, CEff).
    """
    cols = list(df.columns)
    if requested_metrics:
        matched = []
        for req in requested_metrics:
            req_clean = req.strip()
            found = None
            for c in cols:
                if c.lower() == req_clean.lower():
                    found = c
                    break
            if not found:
                for c in cols:
                    if c.lower().startswith(req_clean.lower()):
                        found = c
                        break
            if found and found not in matched:
                matched.append(found)
        if matched:
            return matched

    found = []
    for prefix in KNOWN_AERO_PREFIXES:
        for c in cols:
            c_upper = c.upper()
            if c_upper == prefix or c_upper.startswith(prefix + "_") or c_upper.startswith(prefix + "-") or c_upper.startswith(prefix + "["):
                if c not in found:
                    found.append(c)

    if found:
        return found

    iter_col = find_iter_column(df)
    fallback = []
    for c in cols:
        if c == iter_col or c.lower().startswith("rms["):
            continue
        fallback.append(c)

    return fallback[:2] if fallback else (["CD"] if "CD" in cols else cols[:1])


def compute_metric_stats(series, cauchy_elems):
    """
    Compute final value, min, max, mean, and relative oscillation (max - min) / |mean| in %
    over the Cauchy window (the last cauchy_elems rows).
    """
    series = series.dropna()
    if len(series) == 0:
        return None
    window_len = min(cauchy_elems, len(series))
    window = series.tail(window_len)
    final_val = float(series.iloc[-1])
    w_min = float(window.min())
    w_max = float(window.max())
    w_mean = float(window.mean())
    if abs(w_mean) < 1e-7:
        osc_pct = 0.0
    else:
        osc_pct = ((w_max - w_min) / abs(w_mean)) * 100.0

    return {
        "final_val": final_val,
        "min": w_min,
        "max": w_max,
        "mean": w_mean,
        "osc_pct": osc_pct,
        "window_len": window_len,
    }


def extract_variant_name(file_path):
    """Extract variant name from filename (e.g., History_Gen2_Var3.csv -> Gen2_Var3)."""
    stem = Path(file_path).stem
    if stem.lower().startswith("history_"):
        return stem[8:]
    return stem


def find_history_files(target_path):
    """
    Find all history CSV files:
    - direct file path
    - inside directory (checking analysis/histories/ and histories/, with fallback to
      legacy Analyseergebnisse/Verlaeufe/ and Verlaeufe/)
    """
    p = Path(target_path)
    if p.is_file():
        return [p]
    if not p.is_dir():
        return []

    candidates = []
    for sub in [
        p / "analysis" / "histories",
        p / "histories",
        p / "Analyseergebnisse" / "Verlaeufe",
        p / "Verlaeufe",
    ]:
        if sub.is_dir():
            candidates.extend(sorted(sub.glob("History_*.csv")))
            candidates.extend(sorted(sub.glob("history*.csv")))

    candidates.extend(sorted(p.glob("History_*.csv")))
    candidates.extend(sorted(p.glob("history*.csv")))

    seen = set()
    unique = []
    for f in candidates:
        abs_p = f.resolve()
        if abs_p not in seen and f.is_file():
            seen.add(abs_p)
            unique.append(f)
    return unique


def plot_single_variant(csv_path, args, cfg_defaults):
    """
    Mode B: Generate a 3-panel convergence plot for a single variant.
    Subplot 1: Coefficients over iterations / time
    Subplot 2: Residuals (logarithmic)
    Subplot 3: Cauchy convergence window with tolerance band and shading
    """
    csv_file = Path(csv_path)
    variant_name = extract_variant_name(csv_file)
    df = read_history_csv(csv_file)

    if df.empty:
        print(f"[WARNING] File {csv_file.name} is empty. Skipping plot.")
        return None

    cauchy_elems = args.cauchy_elems if args.cauchy_elems is not None else cfg_defaults.get("cauchy_elems", 150)
    cauchy_eps = args.cauchy_eps if args.cauchy_eps is not None else cfg_defaults.get("cauchy_eps", 1e-4)

    x, x_label = resolve_x_axis(df)

    metrics = detect_metrics(df, args.metrics)
    rms_cols = find_rms_columns(df)

    fig, (ax1, ax2, ax3) = plt.subplots(3, 1, figsize=(11, 9), sharex=True)
    fig.suptitle(f"Convergence History: {variant_name}", fontsize=13, fontweight="bold")

    # -------------------------------------------------------------
    # 1. Coefficients over iteration / time
    # -------------------------------------------------------------
    ax1.set_title(f"Metrics vs. {x_label}", fontsize=10, fontweight="bold")
    if len(metrics) == 1:
        m = metrics[0]
        ax1.plot(x, df[m], label=m, color="#1f77b4", linewidth=1.5)
        ax1.set_ylabel(m, fontsize=10)
    elif len(metrics) == 2:
        m1, m2 = metrics[0], metrics[1]
        c1, c2 = "#1f77b4", "#ff7f0e"
        line1 = ax1.plot(x, df[m1], label=m1, color=c1, linewidth=1.5)
        ax1.set_ylabel(m1, color=c1, fontsize=10)
        ax1.tick_params(axis="y", labelcolor=c1)

        ax1_twin = ax1.twinx()
        line2 = ax1_twin.plot(x, df[m2], label=m2, color=c2, linewidth=1.5)
        ax1_twin.set_ylabel(m2, color=c2, fontsize=10)
        ax1_twin.tick_params(axis="y", labelcolor=c2)
    else:
        colors = plt.cm.tab10(np.linspace(0, 1, max(len(metrics), 3)))
        for idx, m in enumerate(metrics):
            ax1.plot(x, df[m], label=m, color=colors[idx % len(colors)], linewidth=1.5)
        ax1.set_ylabel("Coefficient", fontsize=10)

    if len(x) >= 2 and cauchy_elems is not None and cauchy_elems > 0:
        w_len = min(cauchy_elems, len(x))
        ax1.axvspan(x.iloc[-w_len], x.iloc[-1], color="#2ca02c", alpha=0.08, label=f"Cauchy window ({w_len} iter.)")

    primary_m = metrics[0] if metrics else None
    if primary_m and primary_m in df.columns and cauchy_eps is not None and cauchy_eps > 0:
        ser = df[primary_m].dropna()
        if len(ser) > 0:
            f_val = float(ser.iloc[-1])
            ax1.axhspan(f_val - cauchy_eps, f_val + cauchy_eps, color="#2ca02c", alpha=0.10, linestyle=":", label=f"±ε band ({primary_m})")

    if len(metrics) == 2:
        lines = line1 + line2
        labels = [l.get_label() for l in lines]
        h, l = ax1.get_legend_handles_labels()
        for handle, label in zip(h, l):
            if label not in labels:
                lines.append(handle)
                labels.append(label)
        ax1.legend(lines, labels, loc="best")
    else:
        ax1.legend(loc="best")

    ax1.grid(True, linestyle="--", alpha=0.5)

    # -------------------------------------------------------------
    # 2. Residuals (logarithmic)
    # -------------------------------------------------------------
    ax2.set_title("Residuals (logarithmic)", fontsize=10, fontweight="bold")
    if rms_cols:
        colors = plt.cm.tab10(np.linspace(0, 1, max(len(rms_cols), 3)))
        for idx, col in enumerate(rms_cols):
            ax2.plot(x, df[col], label=col, color=colors[idx % len(colors)], linewidth=1.2)
        ax2.legend(loc="best")
    else:
        ax2.text(0.5, 0.5, "No RMS residual columns available", transform=ax2.transAxes, ha="center", va="center", color="gray")

    ax2.set_ylabel("log10(RMS Residual)", fontsize=10)
    ax2.grid(True, linestyle="--", alpha=0.5)

    # -------------------------------------------------------------
    # 3. Cauchy convergence window
    # -------------------------------------------------------------
    primary_metric = metrics[0] if metrics else "Value"
    ax3.set_title(f"Cauchy Convergence Window – {primary_metric}", fontsize=10, fontweight="bold")

    if primary_metric in df.columns:
        series = df[primary_metric]
        stats = compute_metric_stats(series, cauchy_elems)
        ax3.plot(x, series, label=f"{primary_metric} history", color="#1f77b4", linewidth=1.5)

        if stats:
            final_val = stats["final_val"]
            window_len = stats["window_len"]

            ax3.axhline(final_val, color="#2ca02c", linestyle="--", linewidth=1.3, label=f"Final value ({final_val:.4f})")
            ax3.axhspan(final_val - cauchy_eps, final_val + cauchy_eps, color="#2ca02c", alpha=0.2, label=f"Tolerance band (±{cauchy_eps:.1e})")

            if len(x) >= window_len:
                x_start = x.iloc[-window_len]
                x_end = x.iloc[-1]
                ax3.axvspan(x_start, x_end, color="#7f7f7f", alpha=0.18, hatch="//", label=f"Cauchy window ({window_len} iter.)")

            span = max(stats["max"] - stats["min"], 2.0 * cauchy_eps, 1e-4)
            margin = 0.6 * span
            y_lower = min(stats["min"], final_val - cauchy_eps) - margin
            y_upper = max(stats["max"], final_val + cauchy_eps) + margin
            ax3.set_ylim(y_lower, y_upper)

    ax3.set_xlabel(x_label, fontsize=10)
    ax3.set_ylabel(f"{primary_metric}", fontsize=10)
    ax3.grid(True, linestyle="--", alpha=0.5)
    ax3.legend(loc="best")

    plt.tight_layout()

    formats = parse_export_formats(args)
    saved_files = save_figure_multi_formats(fig, csv_file, formats, dpi=150)
    out_png = saved_files[0] if saved_files else csv_file.with_suffix(".png")

    if args.show:
        plt.show()
    else:
        plt.close(fig)

    return out_png


def plot_comparison(history_files, target_dir, args, cfg_defaults):
    """
    Mode C: Overlay metric and residual histories of multiple variants in one plot.
    Saves History_Compare.png in target_dir.
    """
    if not history_files:
        print("[ERROR] No history files found for comparison.")
        return None

    first_df = read_history_csv(history_files[0])
    metrics = detect_metrics(first_df, args.metrics)
    primary_metric = metrics[0] if metrics else "CD"

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(11, 8), sharex=True)
    fig.suptitle(f"Variant Comparison ({len(history_files)} evaluated)", fontsize=13, fontweight="bold")

    colors = plt.cm.tab10(np.linspace(0, 1, max(len(history_files), 3)))
    x_label = "Iteration"

    for idx, f in enumerate(history_files):
        var_name = extract_variant_name(f)
        df = read_history_csv(f)
        if df.empty:
            continue

        x, x_label = resolve_x_axis(df)
        color = colors[idx % len(colors)]

        if primary_metric in df.columns:
            ax1.plot(x, df[primary_metric], label=var_name, color=color, linewidth=1.5)

        rms_cols = find_rms_columns(df)
        if rms_cols and rms_cols[0] in df.columns:
            ax2.plot(x, df[rms_cols[0]], label=var_name, color=color, linewidth=1.2)

    cauchy_elems = args.cauchy_elems if args.cauchy_elems is not None else cfg_defaults.get("cauchy_elems", None)
    if cauchy_elems and cauchy_elems > 0:
        all_x_max = 0
        for f in history_files:
            try:
                tdf = read_history_csv(f)
                tx, _ = resolve_x_axis(tdf)
                if len(tx) > 0:
                    all_x_max = max(all_x_max, float(tx.iloc[-1]))
            except Exception:
                pass
        if all_x_max > cauchy_elems:
            ax1.axvspan(all_x_max - cauchy_elems, all_x_max, color="#2ca02c", alpha=0.10, hatch="//", label=f"Cauchy window ({cauchy_elems} iter.)")

    ax1.set_title(f"Metric: {primary_metric}", fontsize=10, fontweight="bold")
    ax1.set_ylabel(primary_metric, fontsize=10)
    ax1.grid(True, linestyle="--", alpha=0.5)
    ax1.legend(loc="best")

    ax2.set_title("Residuals (logarithmic)", fontsize=10, fontweight="bold")
    ax2.set_xlabel(x_label, fontsize=10)
    ax2.set_ylabel("log10(RMS Residual)", fontsize=10)
    ax2.grid(True, linestyle="--", alpha=0.5)
    ax2.legend(loc="best")

    plt.tight_layout()

    formats = parse_export_formats(args)
    out_base = Path(target_dir) / "History_Compare"
    saved_files = save_figure_multi_formats(fig, out_base, formats, dpi=150)
    out_png = saved_files[0] if saved_files else out_base.with_suffix(".png")

    if args.show:
        plt.show()
    else:
        plt.close(fig)

    return out_png


def print_overview_table(history_files, args, folder_cfg):
    """
    Mode A: Print a formatted summary table of all discovered variants.
    Columns: Variant | Iterations | Converged | Residual Drop | Metrics (Final / Osc)
    """
    rows = []
    detected_metric_names = []

    for f in history_files:
        var_name = extract_variant_name(f)
        df = read_history_csv(f)
        if df.empty:
            continue

        file_cfg = resolve_run_config(f)
        max_iter = args.max_iter if args.max_iter is not None else file_cfg.get("max_iter", folder_cfg.get("max_iter", 500))
        cauchy_elems = args.cauchy_elems if args.cauchy_elems is not None else file_cfg.get("cauchy_elems", folder_cfg.get("cauchy_elems", 150))

        iter_col = find_iter_column(df)
        iterations = get_iterations_count(df, iter_col)
        converged = (max_iter > 0) and (iterations < max_iter)

        rms_cols = find_rms_columns(df)
        res_drop = compute_residual_drop(df, rms_cols)

        metrics = detect_metrics(df, args.metrics)
        for m in metrics:
            if m not in detected_metric_names:
                detected_metric_names.append(m)

        metric_data = {}
        for m in metrics:
            if m in df.columns:
                stats = compute_metric_stats(df[m], cauchy_elems)
                if stats:
                    metric_data[m] = f"{stats['final_val']:.4f} ({stats['osc_pct']:.2f}%)"
                else:
                    metric_data[m] = "-"
            else:
                metric_data[m] = "-"

        rows.append({
            "variant": var_name,
            "iterations": f"{iterations} / {max_iter}",
            "converged": "yes" if converged else "no",
            "is_conv": converged,
            "res_drop": f"{res_drop:.2f} dec." if not np.isnan(res_drop) else "-",
            "metrics": metric_data,
        })

    if not rows:
        print("[INFO] No evaluable data found in the history files.")
        return

    header_var = "Variant"
    header_iter = "Iterations"
    header_conv = "Converged"
    header_res = "Residual Drop"

    col_widths = {
        "variant": max(len(header_var), max(len(r["variant"]) for r in rows)),
        "iterations": max(len(header_iter), max(len(r["iterations"]) for r in rows)),
        "converged": max(len(header_conv), max(len(r["converged"]) for r in rows)),
        "res_drop": max(len(header_res), max(len(r["res_drop"]) for r in rows)),
    }

    for m in detected_metric_names:
        m_title = f"{m} (Final / Osc)"
        max_val_len = max(len(r["metrics"].get(m, "-")) for r in rows)
        col_widths[m] = max(len(m_title), max_val_len)

    col_order = ["variant", "iterations", "converged", "res_drop"] + detected_metric_names
    headers_map = {
        "variant": header_var,
        "iterations": header_iter,
        "converged": header_conv,
        "res_drop": header_res,
    }
    for m in detected_metric_names:
        headers_map[m] = f"{m} (Final / Osc)"

    def format_row(data_map):
        parts = [f" {str(data_map.get(c, '-')).ljust(col_widths[c])} " for c in col_order]
        return "|" + "|".join(parts) + "|"

    def make_divider(char="-"):
        parts = [char * (col_widths[c] + 2) for c in col_order]
        return "+" + "+".join(parts) + "+"

    divider = make_divider("-")
    header_line = format_row(headers_map)

    print("\nSU2 Convergence Histories Summary:")
    print(divider)
    print(header_line)
    print(make_divider("="))
    for r in rows:
        row_dict = {
            "variant": r["variant"],
            "iterations": r["iterations"],
            "converged": r["converged"],
            "res_drop": r["res_drop"],
        }
        for m in detected_metric_names:
            row_dict[m] = r["metrics"].get(m, "-")
        print(format_row(row_dict))
    print(divider)

    n_conv = sum(1 for r in rows if r["is_conv"])
    n_total = len(rows)
    print(f"Found: {n_total} variant(s) ({n_conv} converged, {n_total - n_conv} not converged / stopped at ITER).\n")


def filter_by_variants(history_files, var_filter):
    """Filter a list of file paths by variant names in var_filter."""
    if not var_filter:
        return history_files

    filtered = []
    patterns = [v.strip().lower() for v in var_filter if v.strip()]
    for f in history_files:
        v_name = extract_variant_name(f).lower()
        stem = f.stem.lower()
        f_name = f.name.lower()
        for p in patterns:
            if v_name == p or stem == p or f_name == p or f"history_{p}.csv" == f_name:
                if f not in filtered:
                    filtered.append(f)
                break
    return filtered


def main():
    parser = argparse.ArgumentParser(
        description="Analyze and plot SU2 convergence histories (history.csv).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Examples:
  # Show summary table of all variants in a results folder:
  python scripts/plot_history.py server-results/MantaAuv

  # Plot a single variant (PNG saved next to the CSV):
  python scripts/plot_history.py server-results/MantaAuv --var Gen2_Var3
  python scripts/plot_history.py server-results/MantaAuv/analysis/histories/History_Gen2_Var3.csv

  # Compare multiple variants:
  python scripts/plot_history.py server-results/MantaAuv --compare
  python scripts/plot_history.py server-results/MantaAuv --compare --var Gen1_Var1 Gen2_Var3

  # Display interactively:
  python scripts/plot_history.py server-results/MantaAuv --var Gen2_Var3 --show
""",
    )

    parser.add_argument("path", help="Path to a history CSV file or a directory containing histories")
    parser.add_argument("--var", nargs="*", default=None, help="Variant name(s) to evaluate, e.g. Gen2_Var3")
    parser.add_argument("--metrics", nargs="*", default=None, help="Metrics to plot (e.g. CD CL; default: auto-detect)")
    parser.add_argument("--compare", action="store_true", help="Generate a comparison plot across multiple variants (History_Compare.png)")
    parser.add_argument("--cauchy-elems", type=int, default=None, dest="cauchy_elems", help="Cauchy convergence window size (default: from config or 150)")
    parser.add_argument("--cauchy-eps", type=float, default=None, dest="cauchy_eps", help="Cauchy convergence tolerance (default: from config or 1e-4)")
    parser.add_argument("--max-iter", type=int, default=None, dest="max_iter", help="Maximum iteration count ITER (default: from config or 500)")
    parser.add_argument("--formats", default=None, help="Comma-separated list of image formats (e.g. png,pdf,svg; default: png)")
    parser.add_argument("--vector", action="store_true", help="Also generate publication vector graphics (.pdf and .svg)")
    parser.add_argument("--show", action="store_true", help="Open interactive Matplotlib windows")

    args = parser.parse_args()

    target = Path(args.path)
    if not target.exists():
        print(f"[ERROR] Path does not exist: {target}")
        sys.exit(1)

    cfg_defaults = resolve_run_config(target)

    if target.is_file():
        plot_single_variant(target, args, cfg_defaults)
        sys.exit(0)

    history_files = find_history_files(target)
    if not history_files:
        print(f"[ERROR] No History_*.csv or history.csv found in directory: {target}")
        sys.exit(1)

    if args.var:
        matching_files = filter_by_variants(history_files, args.var)
        if not matching_files:
            print(f"[ERROR] No file found matching variants {args.var}.")
            sys.exit(1)
    else:
        matching_files = history_files

    if args.compare:
        out_dir = target if target.is_dir() else target.parent
        for sub in [
            target / "analysis" / "histories",
            target / "histories",
            target / "Analyseergebnisse" / "Verlaeufe",
            target / "Verlaeufe",
        ]:
            if sub.is_dir():
                out_dir = sub
                break
        plot_comparison(matching_files, out_dir, args, cfg_defaults)
        sys.exit(0)

    if args.var:
        for f in matching_files:
            plot_single_variant(f, args, cfg_defaults)
        sys.exit(0)

    print_overview_table(matching_files, args, cfg_defaults)


if __name__ == "__main__":
    main()
