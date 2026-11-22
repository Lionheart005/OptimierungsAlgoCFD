#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
plot_calibration.py
Universal analysis of calibration.csv and latest_calibration.json.

Visualizes mesh convergence studies (Grid Convergence Index / GCI, Richardson
extrapolation, wall resolution y+, and runtime scaling):
  01_grid_convergence: 4-panel convergence plot (metrics vs. mesh cell count,
                       Richardson extrapolation, deviation from reference,
                       wall resolution y+ and runtime per level).
"""

import sys
import os
import argparse
import json
from pathlib import Path

# Use headless backend 'Agg' unless --show is passed
if "--show" not in sys.argv:
    import matplotlib
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import pandas as pd
import numpy as np


def parse_export_formats(args):
    """Determine export image formats."""
    if getattr(args, "vector", False):
        return ["png", "pdf", "svg"]
    if getattr(args, "formats", None):
        formats = [f.strip().lower().lstrip(".") for f in args.formats.split(",") if f.strip()]
        return formats if formats else ["png"]
    return ["png"]


def save_figure(fig, base_path, formats, dpi=300):
    """Save figure in all requested formats."""
    base = Path(base_path)
    if base.suffix.lower() in [".png", ".pdf", ".svg", ".jpg", ".jpeg"]:
        base = base.with_suffix("")
    saved = []
    for fmt in formats:
        out_file = base.with_suffix(f".{fmt}")
        fig.savefig(out_file, dpi=dpi, bbox_inches="tight")
        saved.append(out_file)
        print(f"  [Plot] {out_file.name}")
    return saved


def find_calibration_files(path_arg):
    """Locate calibration CSV and JSON files."""
    p = Path(path_arg)
    csv_file = None
    json_file = None

    if p.is_file():
        csv_file = p
        folder = p.parent
    else:
        folder = p
        # 1. Direct lookup in folder
        for name in ["latest_calibration.csv", "calibration.csv"]:
            cand = folder / name
            if cand.is_file():
                csv_file = cand
                break
        # 2. Lookup in calibration/ (or legacy Kalibrierung/)
        if not csv_file:
            for sub_name in ["calibration", "Kalibrierung"]:
                if (folder / sub_name).is_dir():
                    for name in ["latest_calibration.csv", "calibration.csv"]:
                        cand = folder / sub_name / name
                        if cand.is_file():
                            csv_file = cand
                            folder = folder / sub_name
                            break
                if csv_file:
                    break
        # 3. Recursive fallback for calibration*.csv
        if not csv_file:
            matches = sorted(folder.glob("**/calibration*.csv"))
            if matches:
                csv_file = matches[-1]
                folder = csv_file.parent

    if csv_file:
        cand_json = folder / "latest_calibration.json"
        if cand_json.is_file():
            json_file = cand_json
        else:
            for jf in folder.glob("*.json"):
                if "calibration" in jf.name.lower():
                    json_file = jf
                    break

    return csv_file, json_file


def parse_calibration_json(json_path):
    """Read metadata (tolerance, selected factor, GCI) from latest_calibration.json."""
    info = {
        "tolerance": 0.02,
        "selected_factor": None,
        "selected_level": None,
        "gci_order_p": None,
        "gci_fine_pct": None,
        "gci_selected_pct": None,
        "summary": None,
    }
    if not json_path or not os.path.isfile(json_path):
        return info

    try:
        with open(json_path, "r", encoding="utf-8-sig", errors="replace") as f:
            data = json.load(f)

        if "Tolerance" in data:
            info["tolerance"] = float(data["Tolerance"])
        if "SelectedFactor" in data and data["SelectedFactor"] is not None:
            info["selected_factor"] = float(data["SelectedFactor"])
        if "SelectedLevelIndex" in data and data["SelectedLevelIndex"] is not None:
            info["selected_level"] = int(data["SelectedLevelIndex"])
        if "Summary" in data:
            info["summary"] = data["Summary"]

        gci = data.get("Gci")
        if isinstance(gci, dict):
            if "ObservedOrderP" in gci and gci["ObservedOrderP"] is not None:
                info["gci_order_p"] = float(gci["ObservedOrderP"])
            if "GciFinePercent" in gci and gci["GciFinePercent"] is not None:
                info["gci_fine_pct"] = float(gci["GciFinePercent"])
            if "GciSelectedPercent" in gci and gci["GciSelectedPercent"] is not None:
                info["gci_selected_pct"] = float(gci["GciSelectedPercent"])
    except Exception as e:
        print(f"[WARNING] Could not parse {json_path} completely: {e}")

    return info


def read_calibration_csv(csv_path):
    """
    Read calibration.csv, normalize legacy German column names to English
    canonical names, strip '%' from deviation columns, and convert to float.
    """
    df = pd.read_csv(csv_path, sep=";", decimal=".", skipinitialspace=True)
    df.columns = [str(c).strip().strip("\"'").strip() for c in df.columns]
    df = df.loc[:, ~df.columns.str.match(r"^Unnamed")]

    # Map legacy German column names to canonical English names
    legacy_map = {
        "Stufe": "Level",
        "Faktor": "ScaleFactor",
        "Dauer_s": "Duration_s",
        "AbweichungRef": "DeviationFromRef",
        "Konvergiert": "Converged",
        "NormiertRef": "NormalizedToRef",
    }
    rename_dict = {}
    for old_name, new_name in legacy_map.items():
        if old_name in df.columns and new_name not in df.columns:
            rename_dict[old_name] = new_name
    if rename_dict:
        df = df.rename(columns=rename_dict)

    # Strip percentage signs from deviation columns
    for c in df.columns:
        if c.lower() in ("deviationfromref", "abweichungref", "abweichung", "deviation"):
            df[c] = df[c].astype(str).str.replace("%", "").str.strip()
            df[c] = pd.to_numeric(df[c], errors="coerce")

    for c in df.columns:
        df[c] = pd.to_numeric(df[c], errors="coerce")

    return df


def detect_calibration_columns(df):
    """
    Identify standard calibration columns (Level, ScaleFactor, MeshCells, Duration_s,
    DeviationFromRef, Converged, YPlus_P95, YPlus_Ok, NormalizedToRef, and legacy German
    equivalents) and extract the metric columns (e.g., Drag, Lift).
    """
    known_system = {
        "level", "scalefactor", "meshcells", "duration_s", "duration",
        "deviationfromref", "deviation", "converged", "yplus_p95", "yplus_ok",
        "normalizedtoref",
        # Legacy German column names
        "stufe", "faktor", "dauer_s", "dauer", "abweichungref", "abweichung",
        "konvergiert", "normiertref",
        # Cell class / blockage / pressure-viscous breakdown columns if present
        "tri", "quad", "prism", "hexa", "tet", "pyramid", "pyramiden",
        "blockage_%", "cd_p", "cd_v",
    }
    cols = list(df.columns)
    metrics = []

    for c in cols:
        if c.lower() not in known_system:
            metrics.append(c)

    if not metrics:
        for c in ["Drag", "Lift"]:
            if c in cols:
                metrics.append(c)

    return metrics


def calculate_richardson_extrapolation(factors, values):
    """
    Perform Richardson extrapolation for the finest 3 or 2 grids:
      factors: array sorted from finest to coarsest (h1 < h2 < h3)
      values: corresponding metric values (f1, f2, f3)
    Returns: (f_extrap, order_p, gci_pct)
    """
    if len(values) < 2:
        return None, None, None

    f1 = values[0]
    f2 = values[1]
    h1 = factors[0]
    h2 = factors[1]
    r = h2 / h1 if h1 > 0 else 1.414

    if len(values) >= 3 and abs(f2 - f1) > 1e-8:
        f3 = values[2]
        diff12 = f2 - f1
        diff23 = f3 - f2
        if diff12 * diff23 > 0:
            ratio = abs(diff23 / diff12)
            try:
                order_p = np.log(ratio) / np.log(r)
                if order_p < 0.5 or order_p > 4.0:
                    order_p = 2.0
            except Exception:
                order_p = 2.0
        else:
            order_p = 2.0
    else:
        order_p = 2.0

    denom = (r ** order_p) - 1.0
    if abs(denom) > 1e-6:
        f_extrap = f1 + (f1 - f2) / denom
        rel_diff = abs((f1 - f2) / f1) if abs(f1) > 1e-8 else 0.0
        gci_pct = (1.25 * rel_diff / denom) * 100.0
    else:
        f_extrap = f1
        gci_pct = 0.0

    return f_extrap, order_p, gci_pct


def determine_selected_row(df, json_info):
    """
    Determine the selected operating mesh level:
    1. From JSON (SelectedFactor)
    2. By calibration rule: largest ScaleFactor with Converged==1, YPlus_Ok==1, and deviation <= tolerance
    3. Fallback: finest level
    """
    selected_factor = json_info.get("selected_factor")
    if selected_factor is not None:
        match = df[np.isclose(df["ScaleFactor"], selected_factor, atol=0.05)]
        if not match.empty:
            return match.iloc[0]

    tol_pct = json_info.get("tolerance", 0.02) * 100.0
    valid_mask = pd.Series(True, index=df.index)
    if "Converged" in df.columns:
        valid_mask = valid_mask & (df["Converged"] == 1)
    if "YPlus_Ok" in df.columns:
        valid_mask = valid_mask & (df["YPlus_Ok"] == 1)

    if "NormalizedToRef" in df.columns and df["NormalizedToRef"].notna().any():
        valid_mask = valid_mask & (df["NormalizedToRef"] <= 1.0)
    elif "DeviationFromRef" in df.columns:
        valid_mask = valid_mask & (df["DeviationFromRef"] <= tol_pct)

    candidates = df[valid_mask]
    if not candidates.empty:
        return candidates.sort_values(by="ScaleFactor", ascending=False).iloc[0]

    return df.sort_values(by="ScaleFactor", ascending=True).iloc[0]


def plot_grid_convergence(df, metrics, json_info, out_dir, formats, show=False):
    """
    Generate the 4-panel grid convergence plot (01_grid_convergence):
    - Top left: Primary metric (e.g., Drag) vs. MeshCells with Richardson extrapolation & GCI
    - Top right: Secondary metric (e.g., Lift) or runtime scaling vs. MeshCells
    - Bottom left: Percentage deviation / NormalizedToRef per refinement level
    - Bottom right: Wall resolution y+ (P95) and runtime per level
    """
    fig, ((ax1, ax2), (ax3, ax4)) = plt.subplots(2, 2, figsize=(13, 9.5))
    fig.suptitle("Mesh Independence & Grid Convergence Study", fontsize=14, fontweight="bold")

    df_sorted_fine = df.sort_values(by="ScaleFactor", ascending=True).reset_index(drop=True)
    df_by_stage = df.sort_values(by="Level", ascending=True).reset_index(drop=True)

    selected_row = determine_selected_row(df, json_info)
    selected_factor = selected_row["ScaleFactor"]
    selected_cells = selected_row["MeshCells"]
    tol_pct = json_info.get("tolerance", 0.02) * 100.0

    primary_metric = metrics[0] if metrics else "Drag"
    secondary_metric = metrics[1] if len(metrics) > 1 else None

    # -------------------------------------------------------------
    # PANEL 1: Primary metric vs. MeshCells + Richardson extrapolation
    # -------------------------------------------------------------
    if primary_metric in df.columns:
        vals = df_sorted_fine[primary_metric].values
        factors = df_sorted_fine["ScaleFactor"].values
        cells = df_sorted_fine["MeshCells"].values

        ax1.plot(cells, vals, marker="o", color="#1f77b4", linewidth=1.8, label=f"{primary_metric} (Computed)", zorder=3)

        for c, v, h in zip(cells, vals, factors):
            ax1.annotate(f"h={h:.2f}", (c, v), textcoords="offset points", xytext=(0, 7),
                         ha="center", fontsize=8, color="#333333")

        f_ref = vals[0]
        ax1.axhline(f_ref, color="#7f7f7f", linestyle=":", label=f"Reference h={factors[0]:.2f} ({f_ref:.4f})")

        band_lower = f_ref * (1.0 - tol_pct / 100.0)
        band_upper = f_ref * (1.0 + tol_pct / 100.0)
        ax1.axhspan(band_lower, band_upper, color="#2ca02c", alpha=0.12,
                    label=f"Tolerance band (±{tol_pct:.1f}%)")

        f_extrap, order_p, gci = calculate_richardson_extrapolation(factors, vals)
        if json_info.get("gci_fine_pct") is not None:
            gci = json_info["gci_fine_pct"]
        if json_info.get("gci_order_p") is not None:
            order_p = json_info["gci_order_p"]

        if f_extrap is not None:
            gci_abs = abs(f_ref * (gci / 100.0)) if gci else 0.0
            ax1.errorbar(
                [cells[0] * 1.15],
                [f_extrap],
                yerr=[gci_abs],
                fmt="D",
                color="#d62728",
                markersize=6,
                capsize=5,
                elinewidth=1.5,
                label=f"Extrapolation (GCI: ±{gci:.1f}%, p={order_p:.2f})",
                zorder=5
            )

        sel_val = selected_row[primary_metric]
        ax1.scatter([selected_cells], [sel_val], color="#2ca02c", marker="*", s=220,
                    edgecolor="black", label=f"Selected: h={selected_factor:.2f}", zorder=6)

        ax1.set_title(f"{primary_metric} Convergence vs. Cell Count", fontsize=11, fontweight="bold")
        ax1.set_xlabel("Cell Count (MeshCells)", fontsize=10)
        ax1.set_ylabel(primary_metric, fontsize=10)
        ax1.grid(True, linestyle="--", alpha=0.5)
        ax1.legend(loc="best", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 2: Secondary metric (Lift) or runtime scaling
    # -------------------------------------------------------------
    if secondary_metric and secondary_metric in df.columns:
        vals2 = df_sorted_fine[secondary_metric].values
        factors2 = df_sorted_fine["ScaleFactor"].values
        cells2 = df_sorted_fine["MeshCells"].values

        ax2.plot(cells2, vals2, marker="s", color="#ff7f0e", linewidth=1.8, label=f"{secondary_metric} (Computed)", zorder=3)
        for c, v, h in zip(cells2, vals2, factors2):
            ax2.annotate(f"h={h:.2f}", (c, v), textcoords="offset points", xytext=(0, 7),
                         ha="center", fontsize=8, color="#333333")

        f_ref2 = vals2[0]
        ax2.axhline(f_ref2, color="#7f7f7f", linestyle=":", label=f"Reference h={factors2[0]:.2f} ({f_ref2:.4f})")
        half_band2 = max(abs(f_ref2) * (tol_pct / 100.0), 0.005 if secondary_metric.lower() == "lift" else 0.001)
        ax2.axhspan(f_ref2 - half_band2, f_ref2 + half_band2,
                    color="#2ca02c", alpha=0.12,
                    label=f"Tolerance band (±{half_band2:.4f})" if abs(f_ref2) < 0.05 else f"Tolerance band (±{tol_pct:.1f}%)")

        sel_val2 = selected_row[secondary_metric]
        ax2.scatter([selected_cells], [sel_val2], color="#2ca02c", marker="*", s=220,
                    edgecolor="black", label=f"Selected: h={selected_factor:.2f}", zorder=6)

        ax2.set_title(f"{secondary_metric} Convergence vs. Cell Count", fontsize=11, fontweight="bold")
        ax2.set_xlabel("Cell Count (MeshCells)", fontsize=10)
        ax2.set_ylabel(secondary_metric, fontsize=10)
        ax2.grid(True, linestyle="--", alpha=0.5)
        ax2.legend(loc="best", fontsize=8.5)
    else:
        if "Duration_s" in df.columns:
            dur = df_sorted_fine["Duration_s"].values
            cells_d = df_sorted_fine["MeshCells"].values
            ax2.loglog(cells_d, dur, marker="o", color="#9467bd", linewidth=1.8, label="Duration t [s]")
            for c, t, h in zip(cells_d, dur, df_sorted_fine["ScaleFactor"].values):
                ax2.annotate(f"h={h:.2f}\n{t:.1f}s", (c, t), textcoords="offset points", xytext=(0, 8),
                             ha="center", fontsize=8)

            sel_t = selected_row["Duration_s"]
            ax2.scatter([selected_cells], [sel_t], color="#2ca02c", marker="*", s=220,
                        edgecolor="black", label=f"Selected: {sel_t:.1f} s", zorder=6)

            ax2.set_title("Runtime Scaling (log-log)", fontsize=11, fontweight="bold")
            ax2.set_xlabel("Cell Count (MeshCells)", fontsize=10)
            ax2.set_ylabel("Runtime [s]", fontsize=10)
            ax2.grid(True, which="both", linestyle="--", alpha=0.5)
            ax2.legend(loc="best", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 3: Deviations across refinement levels
    # -------------------------------------------------------------
    stages = df_by_stage["Level"].values
    factors_by_st = df_by_stage["ScaleFactor"].values
    stage_labels = [f"Level {int(s)}\n(h={h:.2f})" for s, h in zip(stages, factors_by_st)]

    if "DeviationFromRef" in df_by_stage.columns:
        abw = df_by_stage["DeviationFromRef"].values.copy()
        abw_label = "Deviation [%]"
        if primary_metric in df_by_stage.columns and "NormalizedToRef" in df_by_stage.columns:
            norm_vals = df_by_stage["NormalizedToRef"].values
            if np.nanmax(abw) > 100.0 and np.nanmax(norm_vals) <= 2.0:
                ref_p = df_sorted_fine[primary_metric].iloc[0]
                if abs(ref_p) > 1e-8:
                    abw = np.abs(df_by_stage[primary_metric].values - ref_p) / abs(ref_p) * 100.0
                    abw_label = f"{primary_metric} Deviation [%]"

        bars = ax3.bar(stages, abw, color="#3182bd", alpha=0.75, width=0.55, edgecolor="black", label=abw_label)
        ax3.axhline(tol_pct, color="#d62728", linestyle="--", linewidth=1.5, label=f"Tolerance limit ({tol_pct:.1f}%)")
        ax3.axhspan(0, tol_pct, color="#2ca02c", alpha=0.12, label="Acceptable range")

        y_offset = max(np.nanmax(abw), tol_pct) * 0.02
        for b, v in zip(bars, abw):
            ax3.text(b.get_x() + b.get_width() / 2, v + y_offset, f"{v:.1f}%", ha="center", va="bottom", fontsize=8.5)

        sel_stage = selected_row["Level"]
        sel_idx = int(np.where(stages == sel_stage)[0][0]) if sel_stage in stages else 0
        ax3.scatter([sel_stage], [abw[sel_idx]], color="#2ca02c", marker="*",
                    s=220, edgecolor="black", label=f"Selected (Level {int(sel_stage)})", zorder=6)

        if "NormalizedToRef" in df_by_stage.columns and df_by_stage["NormalizedToRef"].notna().any():
            ax3_twin = ax3.twinx()
            ax3_twin.plot(stages, df_by_stage["NormalizedToRef"], color="#ff7f0e", marker="D",
                          linewidth=1.5, label="NormalizedToRef (<= 1.0)")
            ax3_twin.set_ylabel("Normalized Deviation [-]", color="#ff7f0e", fontsize=10)
            ax3_twin.tick_params(axis="y", labelcolor="#ff7f0e")

        ax3.set_title("Deviation from Finest Reference Level", fontsize=11, fontweight="bold")
        ax3.set_xticks(stages)
        ax3.set_xticklabels(stage_labels, fontsize=8.5)
        ax3.set_ylabel("Deviation [%]", fontsize=10)
        ax3.grid(True, linestyle="--", alpha=0.5)
        ax3.legend(loc="upper right", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 4: Wall resolution y+ (P95) and runtime
    # -------------------------------------------------------------
    if "YPlus_P95" in df_by_stage.columns:
        yplus = df_by_stage["YPlus_P95"].values
        color_yp = "#2b5c8f"
        ax4.plot(stages, yplus, marker="o", color=color_yp, linewidth=2.0, label="y+ (95th percentile)")
        ax4.set_ylabel("Wall Resolution y+ [-]", color=color_yp, fontsize=10)
        ax4.tick_params(axis="y", labelcolor=color_yp)

        ax4.axhline(1.0, color="#2ca02c", linestyle=":", linewidth=1.2, label="y+ <= 1.0 (Low-Re)")
        ax4.axhline(2.0, color="#e6ab02", linestyle=":", linewidth=1.2, label="y+ <= 2.0 (Boundary)")

        if "Duration_s" in df_by_stage.columns:
            ax4_twin = ax4.twinx()
            durations = df_by_stage["Duration_s"].values
            bars_dur = ax4_twin.bar(stages, durations, color="#7570b3", alpha=0.35, width=0.45, label="Runtime [s]")
            ax4_twin.set_ylabel("Duration [s]", color="#7570b3", fontsize=10)
            ax4_twin.tick_params(axis="y", labelcolor="#7570b3")
            for b, d in zip(bars_dur, durations):
                ax4_twin.text(b.get_x() + b.get_width() / 2, d * 0.5, f"{d:.0f}s", ha="center", va="center",
                              fontsize=8, color="#333333")

        ax4.scatter([selected_row["Level"]], [selected_row["YPlus_P95"]], color="#2ca02c", marker="*",
                    s=220, edgecolor="black", label="Selected level", zorder=6)

        ax4.set_title("Wall Resolution y+ and Runtime per Level", fontsize=11, fontweight="bold")
        ax4.set_xticks(stages)
        ax4.set_xticklabels(stage_labels, fontsize=8.5)
        ax4.grid(True, linestyle="--", alpha=0.5)
        ax4.legend(loc="upper left", fontsize=8.5)

    plt.tight_layout()
    out_base = out_dir / "01_grid_convergence"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


def main():
    parser = argparse.ArgumentParser(
        description="Universal analysis of calibration.csv and latest_calibration.json.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("path", help="Path to calibration.csv or directory containing calibration data")
    parser.add_argument("--out", default=None, help="Output directory for plots (default: <csv-dir>/Plots_Calibration/)")
    parser.add_argument("--formats", default="png", help="Comma-separated list of image formats (e.g. png,pdf,svg; default: png)")
    parser.add_argument("--vector", action="store_true", help="Also generate publication vector graphics (.pdf and .svg)")
    parser.add_argument("--show", action="store_true", help="Open interactive Matplotlib windows")

    args = parser.parse_args()

    csv_file, json_file = find_calibration_files(args.path)
    if not csv_file or not csv_file.is_file():
        print(f"[ERROR] No calibration.csv found under: {args.path}")
        sys.exit(1)

    print(f"Reading calibration CSV: {csv_file}")
    df = read_calibration_csv(csv_file)
    if df.empty:
        print("[ERROR] Calibration CSV is empty.")
        sys.exit(1)

    json_info = parse_calibration_json(json_file)
    if json_file:
        print(f"Reading calibration JSON: {json_file}")

    metrics = detect_calibration_columns(df)
    print(f"Detected calibration metrics: {metrics}")

    if args.out:
        out_dir = Path(args.out)
    else:
        out_dir = csv_file.parent / "Plots_Calibration"
    out_dir.mkdir(parents=True, exist_ok=True)

    formats = parse_export_formats(args)
    print(f"Generating calibration plots in: {out_dir} (formats: {', '.join(formats)})")

    plot_grid_convergence(df, metrics, json_info, out_dir, formats, show=args.show)

    print("Calibration analysis completed successfully.")


if __name__ == "__main__":
    main()
