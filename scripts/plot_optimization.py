#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
plot_optimization.py
Universelle, projektunabhängige Auswertung von Simulation_Results.csv (Roadmap AP 1.1).

Analysiert GA-Optimierungsläufe mit automatischer Erkennung aktiver Entwurfsparameter (Gene)
und erzeugt publikationsreife Diagramme (PNG 300 DPI, optional PDF/SVG):
  00_dashboard_optimierung: 2x2 Übersichts-Dashboard
  01_fitness_verlauf: Fitness-Evolution, Best-so-Far und Gesamtsieger
  02_parameter_evolution: Evolution aller aktiven Entwurfsparameter
  03_beiwerte_verlauf: Verlauf aerodynamischer/physikalischer Kennwerte
  04_pareto_front: Pareto-Front (z. B. Drag vs. Lift oder Drag vs. Volumen)
  05_champion_vergleich: Relativer Vergleich Champion vs. Basisentwurf mit Tabelle
"""

import sys
import os
import argparse
from pathlib import Path

# Falls --show nicht übergeben wurde: Headless Matplotlib Backend 'Agg'
if "--show" not in sys.argv:
    import matplotlib
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import matplotlib.ticker as ticker
import pandas as pd
import numpy as np


# Bekannte System- und passive Spalten (case-insensitive Vergleich)
KNOWN_ID_COLS = ["iteration", "variant"]
KNOWN_END_COLS = ["fitness", "stlpath", "meshpath", "simulationfailed"]

KNOWN_PASSIVE_COLS = [
    "area", "perimeter", "chord", "thickness", "frontalarea", "volume",
    "surfacearea", "length", "width", "height", "scalefactor",
    "meshrefinementfactor", "meshcells", "reynoldsnumber", "sensordistance",
    "su2_iterations", "su2_converged", "su2_osc_drag", "su2_osc_lift",
    "su2_itererr_drag", "su2_itererr_lift", "drag", "lift",
    "yplus_median", "yplus_p95", "yplus_max", "glideratio",
    "fineverified", "finedeviation", "boundarylayerhexshare"
]


def parse_export_formats(args):
    """Ermittelt die Exportformate (z. B. ['png', 'pdf', 'svg'])."""
    if getattr(args, "vector", False):
        return ["png", "pdf", "svg"]
    if getattr(args, "formats", None):
        formats = [f.strip().lower().lstrip(".") for f in args.formats.split(",") if f.strip()]
        return formats if formats else ["png"]
    return ["png"]


def save_figure(fig, base_path, formats, dpi=300):
    """Speichert eine Figur in allen angegebenen Formaten."""
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


def find_results_csv(path_arg):
    """Findet die Simulation_Results.csv im übergebenen Pfad oder Ordner."""
    p = Path(path_arg)
    if p.is_file():
        return p
    if p.is_dir():
        # Direkte Suche
        direct = p / "Simulation_Results.csv"
        if direct.is_file():
            return direct
        # Suche in Unterordnern
        matches = list(p.glob("**/Simulation_Results.csv"))
        if matches:
            return matches[0]
        # Fallback: irgendeine CSV mit 'simulation_results'
        for f in p.rglob("*.csv"):
            if "simulation_results" in f.name.lower():
                return f
    return None


def read_results_csv(csv_path):
    """Liest die CSV-Datei ein (Trennzeichen ';', Dezimalpunkt '.')."""
    df = pd.read_csv(csv_path, sep=";", decimal=".", skipinitialspace=True)
    df.columns = [str(c).strip().strip("\"'").strip() for c in df.columns]
    df = df.loc[:, ~df.columns.str.match(r"^Unnamed")]
    return df


def detect_columns(df):
    """
    Erkennt automatisch:
    - ID-Spalten (Iteration, Variant)
    - Aktive Entwurfsparameter (Gene)
    - Hauptmetriken (Fitness, Drag, Lift, GlideRatio, Volume, Area etc.)
    """
    cols = list(df.columns)
    cols_lower = [c.lower() for c in cols]

    iter_col = cols[cols_lower.index("iteration")] if "iteration" in cols_lower else None
    var_col = cols[cols_lower.index("variant")] if "variant" in cols_lower else None
    fit_col = cols[cols_lower.index("fitness")] if "fitness" in cols_lower else None
    failed_col = cols[cols_lower.index("simulationfailed")] if "simulationfailed" in cols_lower else None

    # Alle bekannten System-/Passiv-Spalten lower
    all_system_lower = set(KNOWN_ID_COLS + KNOWN_END_COLS + KNOWN_PASSIVE_COLS)

    # Identifikation der aktiven Parameter (Gene):
    # 1. Versuch: Spalten zwischen 'Variant' und der ersten bekannten passiven Spalte
    active_params = []
    if var_col:
        var_idx = cols.index(var_col)
        candidate_cols = cols[var_idx + 1:]
        for c in candidate_cols:
            c_low = c.lower()
            if c_low in KNOWN_END_COLS or c_low in KNOWN_PASSIVE_COLS:
                break
            # Prüfen ob numerisch
            try:
                pd.to_numeric(df[c].dropna().head(10))
                active_params.append(c)
            except (ValueError, TypeError):
                pass

    # Fallback, falls kein Parameter gefunden wurde: alle numerischen Spalten, die nicht in all_system_lower sind
    if not active_params:
        for c in cols:
            c_low = c.lower()
            if c_low not in all_system_lower:
                try:
                    pd.to_numeric(df[c].dropna().head(10))
                    active_params.append(c)
                except (ValueError, TypeError):
                    pass

    # Physik-Spalten erkennen
    drag_col = next((c for c in cols if c.lower() == "drag"), None)
    lift_col = next((c for c in cols if c.lower() == "lift"), None)
    glide_col = next((c for c in cols if c.lower() in ("glideratio", "glide_ratio", "ceff")), None)
    vol_col = next((c for c in cols if c.lower() in ("volume", "volumen")), None)
    area_col = next((c for c in cols if c.lower() in ("area", "frontalarea", "flaeche")), None)

    return {
        "iter_col": iter_col,
        "var_col": var_col,
        "fit_col": fit_col,
        "failed_col": failed_col,
        "active_params": active_params,
        "drag_col": drag_col,
        "lift_col": lift_col,
        "glide_col": glide_col,
        "vol_col": vol_col,
        "area_col": area_col,
    }


def clean_and_prepare_data(df, col_info):
    """
    Wandelt Spalten in numerische Typen um, filtert Ausreißer/Fehler
    und trennt reguläre Varianten von Validierungsvarianten (z. B. Var 99).
    """
    iter_col = col_info["iter_col"]
    var_col = col_info["var_col"]
    fit_col = col_info["fit_col"]
    failed_col = col_info["failed_col"]

    df = df.copy()
    # In numerische Typen umwandeln
    for c in df.columns:
        if c.lower() not in ["stlpath", "meshpath"]:
            df[c] = pd.to_numeric(df[c], errors="coerce")

    # Flag für fehlgeschlagene Simulation
    is_failed = pd.Series(False, index=df.index)
    if failed_col and failed_col in df.columns:
        is_failed = is_failed | (df[failed_col] == 1)

    if fit_col and fit_col in df.columns:
        # Disqualifizierte oder fehlgeschlagene Varianten (Fitness <= 0.001 oder NaN)
        is_failed = is_failed | df[fit_col].isna() | (df[fit_col] <= 0.001)

    df["IsFailed"] = is_failed

    # Flag für Validierungs-/Feinprüfungs-Varianten (Variant 99 oder Iteration 99)
    is_validation = pd.Series(False, index=df.index)
    if var_col and var_col in df.columns:
        is_validation = is_validation | (df[var_col] == 99)
    if iter_col and iter_col in df.columns:
        is_validation = is_validation | (df[iter_col] == 99)
    df["IsValidation"] = is_validation

    # Berechne GlideRatio falls nicht vorhanden, aber Lift und Drag existieren
    drag_col = col_info["drag_col"]
    lift_col = col_info["lift_col"]
    glide_col = col_info["glide_col"]
    if not glide_col and drag_col and lift_col:
        valid_drag = df[drag_col].replace(0, np.nan)
        df["GlideRatio_Calc"] = df[lift_col] / valid_drag
        col_info["glide_col"] = "GlideRatio_Calc"

    return df


def calculate_pareto_front(df_valid, x_col, y_col, maximize_y=True):
    """
    Berechnet die 2D-Pareto-Front:
    x_col wird minimiert (z. B. Drag).
    y_col wird standardmäßig maximiert (z. B. Lift, GlideRatio, Volume).
    """
    sub = df_valid[[x_col, y_col]].dropna()
    if sub.empty:
        return pd.DataFrame()

    points = sub.values
    is_pareto = np.ones(len(points), dtype=bool)

    for i, p in enumerate(points):
        if not is_pareto[i]:
            continue
        for j, q in enumerate(points):
            if i == j:
                continue
            # q dominiert p wenn q_x <= p_x und q_y >= p_y (bei maximize_y)
            if maximize_y:
                if q[0] <= p[0] and q[1] >= p[1] and (q[0] < p[0] or q[1] > p[1]):
                    is_pareto[i] = False
                    break
            else:
                if q[0] <= p[0] and q[1] <= p[1] and (q[0] < p[0] or q[1] < p[1]):
                    is_pareto[i] = False
                    break

    pareto_df = sub[is_pareto].sort_values(by=x_col)
    return pareto_df


# -----------------------------------------------------------------------------
# PLOT 1: Fitness-Verlauf
# -----------------------------------------------------------------------------
def plot_fitness_evolution(df, col_info, out_dir, formats, show=False):
    iter_col = col_info["iter_col"]
    fit_col = col_info["fit_col"]

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy()
    if df_valid.empty or not fit_col:
        return None

    fig, ax = plt.subplots(figsize=(10, 6))

    # Alle Varianten als Streudiagramm
    ax.scatter(
        df_valid[iter_col],
        df_valid[fit_col],
        color="#1f77b4",
        alpha=0.45,
        s=40,
        edgecolors="none",
        label="Varianten (gültig)",
        zorder=3
    )

    # Fehlgeschlagene Varianten am unteren Rand andeuten
    df_failed = df[df["IsFailed"] & ~df["IsValidation"]]
    if not df_failed.empty:
        min_fit = df_valid[fit_col].min()
        dummy_y = min_fit * 0.95 if min_fit > 0 else 0.0
        ax.scatter(
            df_failed[iter_col],
            [dummy_y] * len(df_failed),
            color="#d62728",
            marker="x",
            s=35,
            alpha=0.6,
            label=f"Fehlgeschlagen ({len(df_failed)})",
            zorder=3
        )

    # Generations-Champions und Best-so-Far
    gen_groups = df_valid.groupby(iter_col)[fit_col]
    gen_champs = gen_groups.max().reset_index()

    # Best-so-far
    gens = gen_champs[iter_col].values
    best_vals = np.maximum.accumulate(gen_champs[fit_col].values)

    ax.step(
        gens,
        best_vals,
        where="post",
        color="#2ca02c",
        linewidth=2.2,
        label="Best-so-Far",
        zorder=4
    )

    ax.plot(
        gen_champs[iter_col],
        gen_champs[fit_col],
        color="#ff7f0e",
        marker="o",
        markersize=6,
        linestyle="--",
        linewidth=1.2,
        label="Generations-Champion",
        zorder=5
    )

    # Gesamtsieger hervorheben
    best_idx = df_valid[fit_col].idxmax()
    champ_row = df_valid.loc[best_idx]
    champ_iter = champ_row[iter_col]
    champ_fit = champ_row[fit_col]

    ax.scatter(
        [champ_iter],
        [champ_fit],
        color="#d62728",
        marker="*",
        s=250,
        edgecolors="black",
        linewidth=1.2,
        label=f"Gesamtsieger (Gen {int(champ_iter)}: {champ_fit:.2f})",
        zorder=6
    )

    # Basis-Entwurf (Gen 1, Var 1)
    base_rows = df_valid[(df_valid[iter_col] == 1) & (df_valid[col_info["var_col"]] == 1)]
    if not base_rows.empty:
        base_fit = base_rows.iloc[0][fit_col]
        ax.scatter(
            [1],
            [base_fit],
            color="#7f7f7f",
            marker="s",
            s=80,
            edgecolors="black",
            label=f"Basisentwurf ({base_fit:.2f})",
            zorder=5
        )

    ax.set_title("Fitness-Entwicklung über die Generationen", fontsize=13, fontweight="bold")
    ax.set_xlabel("Generation (Iteration)", fontsize=11)
    ax.set_ylabel("Fitness [-]", fontsize=11)
    ax.xaxis.set_major_locator(ticker.MaxNLocator(integer=True))
    ax.grid(True, linestyle="--", alpha=0.5)
    ax.legend(loc="best", framealpha=0.9)

    plt.tight_layout()
    out_base = out_dir / "01_fitness_verlauf"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


# -----------------------------------------------------------------------------
# PLOT 2: Parameter-Evolution (Gene)
# -----------------------------------------------------------------------------
def plot_parameter_evolution(df, col_info, out_dir, formats, show=False):
    active_params = col_info["active_params"]
    iter_col = col_info["iter_col"]
    fit_col = col_info["fit_col"]

    if not active_params or not iter_col:
        return None

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy()
    if df_valid.empty:
        return None

    num_params = len(active_params)
    ncols = min(3, num_params)
    nrows = (num_params + ncols - 1) // ncols

    fig, axes = plt.subplots(nrows, ncols, figsize=(5.2 * ncols, 3.8 * nrows), squeeze=False)
    fig.suptitle("Evolution der aktiven Entwurfsparameter", fontsize=14, fontweight="bold", y=1.00)

    # Generations-Champions identifizieren
    gen_champ_indices = df_valid.groupby(iter_col)[fit_col].idxmax() if fit_col else []
    champ_rows = df_valid.loc[gen_champ_indices]

    unique_gens = sorted(df_valid[iter_col].unique())

    for idx, param in enumerate(active_params):
        r, c = divmod(idx, ncols)
        ax = axes[r][c]

        # Daten pro Generation sammeln
        data_per_gen = [df_valid[df_valid[iter_col] == g][param].dropna().values for g in unique_gens]

        # Boxplots je Generation
        box_pos = list(range(len(unique_gens)))
        bp = ax.boxplot(
            data_per_gen,
            positions=box_pos,
            widths=0.45,
            patch_artist=True,
            boxprops=dict(facecolor="#c6dbef", color="#3182bd", alpha=0.6),
            medianprops=dict(color="#08519c", linewidth=1.5),
            whiskerprops=dict(color="#3182bd"),
            capprops=dict(color="#3182bd"),
            flierprops=dict(marker="o", markerfacecolor="gray", markersize=3, alpha=0.4),
        )

        # Einzelne Punkte mit leichtem Jitter überlagern
        for i, (g, vals) in enumerate(zip(unique_gens, data_per_gen)):
            jitter = np.random.normal(0, 0.04, size=len(vals))
            ax.scatter(
                np.full_like(vals, i) + jitter,
                vals,
                color="#3182bd",
                alpha=0.35,
                s=18,
                zorder=3
            )

        # Generations-Champion als rote Linie/Sterne
        champ_vals = [champ_rows[champ_rows[iter_col] == g][param].values[0]
                      for g in unique_gens if len(champ_rows[champ_rows[iter_col] == g]) > 0]
        ax.plot(
            box_pos,
            champ_vals,
            color="#d62728",
            marker="*",
            markersize=9,
            linewidth=1.2,
            label="Gen-Champion",
            zorder=5
        )

        ax.set_title(param, fontsize=11, fontweight="bold")
        ax.set_xticks(box_pos)
        ax.set_xticklabels([f"Gen {int(g)}" for g in unique_gens], rotation=35, ha="right", fontsize=9)
        ax.set_ylabel(param, fontsize=10)
        ax.grid(True, linestyle="--", alpha=0.4)
        if idx == 0:
            ax.legend(loc="best", fontsize=9)

    # Nicht genutzte Achsen ausblenden
    for idx in range(num_params, nrows * ncols):
        r, c = divmod(idx, ncols)
        fig.delaxes(axes[r][c])

    plt.tight_layout()
    out_base = out_dir / "02_parameter_evolution"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


# -----------------------------------------------------------------------------
# PLOT 3: Physik-/Beiwerte-Verlauf über den fortlaufenden Variantenindex
# -----------------------------------------------------------------------------
def plot_coefficients_history(df, col_info, out_dir, formats, show=False):
    drag_col = col_info["drag_col"]
    lift_col = col_info["lift_col"]
    glide_col = col_info["glide_col"]
    iter_col = col_info["iter_col"]

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy().reset_index(drop=True)
    if df_valid.empty or not drag_col:
        return None

    # Metriken sammeln
    plot_metrics = []
    if drag_col and drag_col in df_valid.columns:
        plot_metrics.append((drag_col, "Widerstandsbeiwert Cd", "#1f77b4"))
    if lift_col and lift_col in df_valid.columns and df_valid[lift_col].abs().max() > 1e-6:
        plot_metrics.append((lift_col, "Auftriebsbeiwert Cl", "#2ca02c"))
    if glide_col and glide_col in df_valid.columns:
        plot_metrics.append((glide_col, "Gleitzahl Cl / Cd", "#ff7f0e"))

    if not plot_metrics:
        # Fallback auf Geometriegrößen
        for alt_c in ["Volume", "Area", "FrontalArea"]:
            if alt_c in df_valid.columns:
                plot_metrics.append((alt_c, alt_c, "#9467bd"))
                break

    n_plots = len(plot_metrics)
    fig, axes = plt.subplots(n_plots, 1, figsize=(11, 3.2 * n_plots), sharex=True)
    if n_plots == 1:
        axes = [axes]

    fig.suptitle("Entwicklung der physikalischen Kenngrößen", fontsize=13, fontweight="bold")

    # Generationswechsel-Indizes für vertikale Linien
    gen_changes = []
    current_gen = None
    for idx, row in df_valid.iterrows():
        g = row[iter_col]
        if g != current_gen:
            gen_changes.append((idx, int(g)))
            current_gen = g

    x_idx = df_valid.index.values

    for ax_idx, (col_name, label_name, color) in enumerate(plot_metrics):
        ax = axes[ax_idx]
        ax.plot(x_idx, df_valid[col_name], color=color, linewidth=1.5, marker="o", markersize=3, label=label_name)
        ax.set_ylabel(label_name, fontsize=10, fontweight="bold")
        ax.grid(True, linestyle="--", alpha=0.5)

        # Generations-Trennlinien
        for split_idx, gen_num in gen_changes:
            ax.axvline(split_idx, color="gray", linestyle=":", alpha=0.7)
            if ax_idx == 0:
                ax.text(split_idx + 0.3, ax.get_ylim()[1], f"Gen {gen_num}",
                        fontsize=8, color="#555555", verticalalignment="top", rotation=90)

        ax.legend(loc="best")

    axes[-1].set_xlabel("Fortlaufender Varianten-Index (gültige Simulationen)", fontsize=11)

    plt.tight_layout()
    out_base = out_dir / "03_beiwerte_verlauf"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


# -----------------------------------------------------------------------------
# PLOT 4: Pareto-Front
# -----------------------------------------------------------------------------
def plot_pareto_front(df, col_info, out_dir, formats, show=False):
    drag_col = col_info["drag_col"]
    lift_col = col_info["lift_col"]
    fit_col = col_info["fit_col"]
    iter_col = col_info["iter_col"]
    var_col = col_info["var_col"]

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy()
    if df_valid.empty or not drag_col:
        return None

    # Entscheiden, welche Y-Achse genutzt wird
    use_lift = lift_col and lift_col in df_valid.columns and df_valid[lift_col].abs().max() > 1e-4
    if use_lift:
        x_col = drag_col
        y_col = lift_col
        x_label = f"Widerstandsbeiwert Cd ({drag_col}) [Minimierung]"
        y_label = f"Auftriebsbeiwert Cl ({lift_col}) [Maximierung]"
        maximize_y = True
    else:
        # Fallback: Drag vs. Volume / Area
        alt_y = next((c for c in ["Volume", "FrontalArea", "Area"] if c in df_valid.columns), None)
        if not alt_y:
            return None
        x_col = drag_col
        y_col = alt_y
        x_label = f"Widerstand ({drag_col}) [Minimierung]"
        y_label = f"Geometrie ({alt_y}) [Maximierung]"
        maximize_y = True

    fig, ax = plt.subplots(figsize=(10, 6.5))

    # Streudiagramm farbkodiert nach Generation
    gens = df_valid[iter_col].values
    scatter = ax.scatter(
        df_valid[x_col],
        df_valid[y_col],
        c=gens,
        cmap="viridis",
        s=55,
        alpha=0.75,
        edgecolors="none",
        zorder=3
    )

    cbar = fig.colorbar(scatter, ax=ax)
    cbar.set_label("Generation (Iteration)", fontsize=10)
    cbar.locator = ticker.MaxNLocator(integer=True)
    cbar.update_ticks()

    # Pareto-Front berechnen und einzeichnen
    pareto_df = calculate_pareto_front(df_valid, x_col, y_col, maximize_y=maximize_y)
    if not pareto_df.empty:
        ax.plot(
            pareto_df[x_col],
            pareto_df[y_col],
            color="#d62728",
            linestyle="--",
            linewidth=2.0,
            marker="o",
            markersize=5,
            label="Pareto-Front",
            zorder=4
        )

    # Basis-Entwurf (Gen 1, Var 1)
    base_rows = df_valid[(df_valid[iter_col] == 1) & (df_valid[var_col] == 1)]
    if not base_rows.empty:
        bx = base_rows.iloc[0][x_col]
        by = base_rows.iloc[0][y_col]
        ax.scatter([bx], [by], color="black", marker="s", s=110, label="Basisentwurf (Gen 1, Var 1)", zorder=5)

    # Gesamtsieger hervorheben
    if fit_col and fit_col in df_valid.columns:
        best_row = df_valid.loc[df_valid[fit_col].idxmax()]
        ax.scatter(
            [best_row[x_col]],
            [best_row[y_col]],
            color="#e377c2",
            marker="*",
            s=280,
            edgecolors="black",
            linewidth=1.2,
            label=f"Gesamtsieger (* Gen {int(best_row[iter_col])})",
            zorder=6
        )

    ax.set_title(f"Pareto-Diagramm: {y_col} über {x_col}", fontsize=13, fontweight="bold")
    ax.set_xlabel(x_label, fontsize=11)
    ax.set_ylabel(y_label, fontsize=11)
    ax.grid(True, linestyle="--", alpha=0.5)
    ax.legend(loc="best", framealpha=0.9)

    plt.tight_layout()
    out_base = out_dir / "04_pareto_front"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


# -----------------------------------------------------------------------------
# PLOT 5: Champion-Vergleich (Relativ gegenüber Basisentwurf + Kennwert-Tabelle)
# -----------------------------------------------------------------------------
def plot_champion_comparison(df, col_info, out_dir, formats, show=False):
    iter_col = col_info["iter_col"]
    var_col = col_info["var_col"]
    fit_col = col_info["fit_col"]
    active_params = col_info["active_params"]

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy()
    if df_valid.empty or not fit_col:
        return None

    # Basisentwurf finden
    base_rows = df_valid[(df_valid[iter_col] == 1) & (df_valid[var_col] == 1)]
    if base_rows.empty:
        base_row = df_valid.iloc[0]
    else:
        base_row = base_rows.iloc[0]

    # Gesamtsieger finden
    best_row = df_valid.loc[df_valid[fit_col].idxmax()]

    # Vergleichsmetriken zusammenstellen
    compare_metrics = list(active_params)
    for phys_col in [col_info["drag_col"], col_info["lift_col"], col_info["glide_col"], fit_col]:
        if phys_col and phys_col in df_valid.columns and phys_col not in compare_metrics:
            compare_metrics.append(phys_col)

    table_data = []
    param_names = []
    pct_changes = []

    for p in compare_metrics:
        b_val = base_row.get(p, np.nan)
        c_val = best_row.get(p, np.nan)
        if pd.isna(b_val) or pd.isna(c_val):
            continue

        try:
            b_float = float(b_val)
            c_float = float(c_val)
        except (ValueError, TypeError):
            continue

        if abs(b_float) > 1e-7:
            delta_pct = ((c_float - b_float) / abs(b_float)) * 100.0
        else:
            delta_pct = 0.0

        param_names.append(p)
        pct_changes.append(delta_pct)
        table_data.append([
            p,
            f"{b_float:.4g}",
            f"{c_float:.4g}",
            f"{delta_pct:+.1f} %"
        ])

    if not param_names:
        return None

    fig, (ax_bar, ax_tab) = plt.subplots(2, 1, figsize=(10, 8), gridspec_kw={"height_ratios": [1.4, 1.0]})
    fig.suptitle(f"Gesamtsieger (Gen {int(best_row[iter_col])}, Var {int(best_row[var_col])}) im Vergleich zum Basisentwurf",
                 fontsize=13, fontweight="bold")

    # Balkendiagramm der relativen Änderungen
    y_pos = np.arange(len(param_names))
    colors = ["#2ca02c" if v >= 0 else "#1f77b4" for v in pct_changes]

    # Bei Widerstand (Drag) ist negativ positiv (Verbesserung)
    for idx, p in enumerate(param_names):
        if "drag" in p.lower():
            colors[idx] = "#2ca02c" if pct_changes[idx] <= 0 else "#d62728"

    bars = ax_bar.barh(y_pos, pct_changes, color=colors, height=0.6, edgecolor="black", alpha=0.85)
    ax_bar.set_yticks(y_pos)
    ax_bar.set_yticklabels(param_names, fontsize=10, fontweight="bold")
    ax_bar.axvline(0, color="black", linewidth=1.0)
    ax_bar.set_xlabel("Relative Änderung gegenüber Basisentwurf [%]", fontsize=11)
    ax_bar.grid(True, linestyle="--", alpha=0.4, axis="x")

    # Prozentlabels an den Balken
    for bar, pct in zip(bars, pct_changes):
        x_val = bar.get_width()
        ha = "left" if x_val >= 0 else "right"
        offset = 1.0 if x_val >= 0 else -1.0
        ax_bar.text(x_val + offset, bar.get_y() + bar.get_height() / 2, f"{pct:+.1f} %",
                    va="center", ha=ha, fontsize=9, fontweight="bold")

    # Tabelle
    ax_tab.axis("off")
    col_labels = ["Parameter / Metrik", "Basisentwurf", "Champion", "Differenz (%)"]
    tab = ax_tab.table(
        cellText=table_data,
        colLabels=col_labels,
        loc="center",
        cellLoc="center"
    )
    tab.auto_set_font_size(False)
    tab.set_fontsize(9.5)
    tab.scale(1.0, 1.4)

    # Header-Styling
    for c_idx in range(len(col_labels)):
        cell = tab[0, c_idx]
        cell.set_facecolor("#2b5c8f")
        cell.set_text_props(color="white", fontweight="bold")

    plt.tight_layout()
    out_base = out_dir / "05_champion_vergleich"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


# -----------------------------------------------------------------------------
# PLOT 0: Dashboard (2x2)
# -----------------------------------------------------------------------------
def plot_dashboard(df, col_info, out_dir, formats, show=False):
    iter_col = col_info["iter_col"]
    var_col = col_info["var_col"]
    fit_col = col_info["fit_col"]
    drag_col = col_info["drag_col"]
    lift_col = col_info["lift_col"]

    df_valid = df[~df["IsFailed"] & ~df["IsValidation"]].copy()
    if df_valid.empty or not fit_col:
        return None

    fig, ((ax1, ax2), (ax3, ax4)) = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle("Optimierungs-Dashboard (Gesamtübersicht)", fontsize=15, fontweight="bold")

    # 1. Panel oben links: Fitness-Verlauf
    ax1.scatter(df_valid[iter_col], df_valid[fit_col], color="#1f77b4", alpha=0.4, s=35, label="Varianten")
    gen_champs = df_valid.groupby(iter_col)[fit_col].max().reset_index()
    best_vals = np.maximum.accumulate(gen_champs[fit_col].values)
    ax1.step(gen_champs[iter_col], best_vals, where="post", color="#2ca02c", linewidth=2.0, label="Best-so-Far")
    champ_row = df_valid.loc[df_valid[fit_col].idxmax()]
    ax1.scatter([champ_row[iter_col]], [champ_row[fit_col]], color="#d62728", marker="*", s=180, label="Champion *", zorder=6)
    ax1.set_title("Fitness-Entwicklung", fontsize=11, fontweight="bold")
    ax1.set_xlabel("Generation", fontsize=10)
    ax1.set_ylabel("Fitness", fontsize=10)
    ax1.grid(True, linestyle="--", alpha=0.5)
    ax1.legend(loc="best", fontsize=8.5)

    # 2. Panel oben rechts: Pareto-Front
    if drag_col and drag_col in df_valid.columns:
        use_lift = lift_col and lift_col in df_valid.columns and df_valid[lift_col].abs().max() > 1e-4
        y_name = lift_col if use_lift else next((c for c in ["Volume", "Area"] if c in df_valid.columns), drag_col)
        scatter = ax2.scatter(df_valid[drag_col], df_valid[y_name], c=df_valid[iter_col], cmap="viridis", s=40, alpha=0.7)
        pareto_df = calculate_pareto_front(df_valid, drag_col, y_name, maximize_y=True)
        if not pareto_df.empty:
            ax2.plot(pareto_df[drag_col], pareto_df[y_name], color="#d62728", linestyle="--", linewidth=1.8, label="Pareto-Front")
        ax2.scatter([champ_row[drag_col]], [champ_row[y_name]], color="#e377c2", marker="*", s=200, label="Champion *", zorder=6)
        ax2.set_title(f"Pareto-Verteilung ({y_name} vs. {drag_col})", fontsize=11, fontweight="bold")
        ax2.set_xlabel(drag_col, fontsize=10)
        ax2.set_ylabel(y_name, fontsize=10)
        ax2.grid(True, linestyle="--", alpha=0.5)
        ax2.legend(loc="best", fontsize=8.5)
    else:
        ax2.text(0.5, 0.5, "Keine Widerstandsspalte vorhanden", ha="center", va="center")

    # 3. Panel unten links: Physik-Beiwerte
    if drag_col and drag_col in df_valid.columns:
        sub_df = df_valid.reset_index(drop=True)
        ax3.plot(sub_df.index, sub_df[drag_col], color="#1f77b4", label=f"Widerstand ({drag_col})", linewidth=1.3)
        ax3.set_ylabel(drag_col, color="#1f77b4", fontsize=10)
        ax3.tick_params(axis="y", labelcolor="#1f77b4")
        if lift_col and lift_col in sub_df.columns and sub_df[lift_col].abs().max() > 1e-4:
            ax3_twin = ax3.twinx()
            ax3_twin.plot(sub_df.index, sub_df[lift_col], color="#2ca02c", label=f"Auftrieb ({lift_col})", linewidth=1.3)
            ax3_twin.set_ylabel(lift_col, color="#2ca02c", fontsize=10)
            ax3_twin.tick_params(axis="y", labelcolor="#2ca02c")
        ax3.set_title("Physikalischer Verlauf über Varianten", fontsize=11, fontweight="bold")
        ax3.set_xlabel("Varianten-Index", fontsize=10)
        ax3.grid(True, linestyle="--", alpha=0.5)
    else:
        ax3.text(0.5, 0.5, "Keine Physik-Spalten vorhanden", ha="center", va="center")

    # 4. Panel unten rechts: Champion-Vergleich
    base_rows = df_valid[(df_valid[iter_col] == 1) & (df_valid[var_col] == 1)]
    base_row = base_rows.iloc[0] if not base_rows.empty else df_valid.iloc[0]

    compare_keys = list(col_info["active_params"])
    if drag_col and drag_col in df_valid.columns:
        compare_keys.append(drag_col)
    if lift_col and lift_col in df_valid.columns and df_valid[lift_col].abs().max() > 1e-4:
        compare_keys.append(lift_col)
    compare_keys.append(fit_col)

    names = []
    deltas = []
    for k in compare_keys[:8]:  # max 8 im Dashboard
        b = base_row.get(k, np.nan)
        c = champ_row.get(k, np.nan)
        if pd.notna(b) and pd.notna(c) and abs(float(b)) > 1e-7:
            d = ((float(c) - float(b)) / abs(float(b))) * 100.0
            names.append(k)
            deltas.append(d)

    if names:
        y_pos = np.arange(len(names))
        bar_colors = ["#2ca02c" if v >= 0 else "#1f77b4" for v in deltas]
        for idx, nm in enumerate(names):
            if "drag" in nm.lower():
                bar_colors[idx] = "#2ca02c" if deltas[idx] <= 0 else "#d62728"
        ax4.barh(y_pos, deltas, color=bar_colors, height=0.55, edgecolor="black", alpha=0.85)
        ax4.set_yticks(y_pos)
        ax4.set_yticklabels(names, fontsize=9)
        ax4.axvline(0, color="black", linewidth=0.8)
        ax4.set_xlabel("Änderung [%]", fontsize=10)
        ax4.set_title("Champion vs. Basisentwurf (Δ %)", fontsize=11, fontweight="bold")
        ax4.grid(True, linestyle="--", alpha=0.4, axis="x")
    else:
        ax4.text(0.5, 0.5, "Keine Vergleichsdaten verfügbar", ha="center", va="center")

    plt.tight_layout()
    out_base = out_dir / "00_dashboard_optimierung"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


def main():
    parser = argparse.ArgumentParser(
        description="Universelle, projektunabhängige Auswertung von Simulation_Results.csv.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("path", help="Pfad zu Simulation_Results.csv oder einem Ordner, der diese enthält")
    parser.add_argument("--out", default=None, help="Zielverzeichnis für die Plots (Standard: <CSV-Ordner>/Plots_Optimierung/)")
    parser.add_argument("--formats", default="png", help="Kommagetrennte Liste der Bildformate (z.B. png,pdf,svg; Standard: png)")
    parser.add_argument("--vector", action="store_true", help="Erzeugt zusätzlich Publikations-Vektorgrafiken (.pdf und .svg)")
    parser.add_argument("--show", action="store_true", help="Öffnet interaktive Matplotlib-Fenster")

    args = parser.parse_args()

    csv_file = find_results_csv(args.path)
    if not csv_file or not csv_file.is_file():
        print(f"[FEHLER] Keine Simulation_Results.csv gefunden unter: {args.path}")
        sys.exit(1)

    print(f"Lese Ergebnisdatei: {csv_file}")
    df = read_results_csv(csv_file)
    if df.empty:
        print("[FEHLER] Ergebnisdatei ist leer.")
        sys.exit(1)

    col_info = detect_columns(df)
    df_clean = clean_and_prepare_data(df, col_info)

    # Ausgabeverzeichnis festlegen
    if args.out:
        out_dir = Path(args.out)
    else:
        out_dir = csv_file.parent / "Plots_Optimierung"
    out_dir.mkdir(parents=True, exist_ok=True)

    formats = parse_export_formats(args)
    print(f"Erkannte Entwurfsparameter (Gene): {col_info['active_params']}")
    print(f"Erzeuge Optimierungs-Plots in: {out_dir} (Formate: {', '.join(formats)})")

    # Plots erzeugen
    plot_fitness_evolution(df_clean, col_info, out_dir, formats, show=args.show)
    plot_parameter_evolution(df_clean, col_info, out_dir, formats, show=args.show)
    plot_coefficients_history(df_clean, col_info, out_dir, formats, show=args.show)
    plot_pareto_front(df_clean, col_info, out_dir, formats, show=args.show)
    plot_champion_comparison(df_clean, col_info, out_dir, formats, show=args.show)
    plot_dashboard(df_clean, col_info, out_dir, formats, show=args.show)

    print("Optimierungsauswertung erfolgreich abgeschlossen.")


if __name__ == "__main__":
    main()
