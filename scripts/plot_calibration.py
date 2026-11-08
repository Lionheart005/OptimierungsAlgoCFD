#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
plot_calibration.py
Universelle Auswertung von calibration.csv und latest_calibration.json (Roadmap AP 1.1).

Visualisiert Netzkonvergenzuntersuchungen (Grid Convergence Index / GCI, Richardson-Extrapolation,
Wandauflösung y+ und Rechenzeitbedarf):
  01_gitterkonvergenz: 4-teiliger Konvergenzplot (Kennwerte über Gitterfeinheit, Richardson-Extrapolation,
                       prozentuale/normierte Abweichung zur Referenz, Wandauflösung y+ und Rechenzeit).
"""

import sys
import os
import argparse
import json
from pathlib import Path

# Falls --show nicht übergeben wurde: Headless Backend 'Agg'
if "--show" not in sys.argv:
    import matplotlib
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import matplotlib.ticker as ticker
import pandas as pd
import numpy as np


def parse_export_formats(args):
    """Ermittelt Exportformate."""
    if getattr(args, "vector", False):
        return ["png", "pdf", "svg"]
    if getattr(args, "formats", None):
        formats = [f.strip().lower().lstrip(".") for f in args.formats.split(",") if f.strip()]
        return formats if formats else ["png"]
    return ["png"]


def save_figure(fig, base_path, formats, dpi=300):
    """Speichert Figur in den gewünschten Formaten."""
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
    """Findet CSV- und JSON-Dateien der Kalibrierung."""
    p = Path(path_arg)
    csv_file = None
    json_file = None

    if p.is_file():
        csv_file = p
        folder = p.parent
    else:
        folder = p
        # 1. Direkte Suche in folder
        for name in ["latest_calibration.csv", "calibration.csv"]:
            cand = folder / name
            if cand.is_file():
                csv_file = cand
                break
        # 2. Suche in Kalibrierung/
        if not csv_file and (folder / "Kalibrierung").is_dir():
            for name in ["latest_calibration.csv", "calibration.csv"]:
                cand = folder / "Kalibrierung" / name
                if cand.is_file():
                    csv_file = cand
                    folder = folder / "Kalibrierung"
                    break
        # 3. Rekursive Suche nach neuesten calibration*.csv
        if not csv_file:
            matches = sorted(folder.glob("**/calibration*.csv"))
            if matches:
                csv_file = matches[-1]
                folder = csv_file.parent

    if csv_file:
        # JSON-Begleitdatei suchen
        cand_json = folder / "latest_calibration.json"
        if cand_json.is_file():
            json_file = cand_json
        else:
            # Suche im selben Ordner nach *.json
            for jf in folder.glob("*.json"):
                if "calibration" in jf.name.lower():
                    json_file = jf
                    break

    return csv_file, json_file


def parse_calibration_json(json_path):
    """Liest Metadaten (Toleranz, gewählter Faktor, GCI) aus latest_calibration.json."""
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
        print(f"[WARNUNG] Konnte {json_path} nicht vollständig parsen: {e}")

    return info


def read_calibration_csv(csv_path):
    """
    Liest calibration.csv ein und bereinigt Spalten:
    - Entfernt '%' aus AbweichungRef
    - Konvertiert alle relevanten Spalten in float
    """
    df = pd.read_csv(csv_path, sep=";", decimal=".", skipinitialspace=True)
    df.columns = [str(c).strip().strip("\"'").strip() for c in df.columns]
    df = df.loc[:, ~df.columns.str.match(r"^Unnamed")]

    # Prozentzeichen bereinigen
    for c in df.columns:
        if c.lower() in ("abweichungref", "abweichung", "deviation"):
            df[c] = df[c].astype(str).str.replace("%", "").str.strip()
            df[c] = pd.to_numeric(df[c], errors="coerce")

    for c in df.columns:
        df[c] = pd.to_numeric(df[c], errors="coerce")

    return df


def detect_calibration_columns(df):
    """
    Erkennt Standardspalten (Stufe, Faktor, MeshCells, Dauer_s, AbweichungRef, Konvergiert, YPlus_P95, NormiertRef)
    und extrahiert die dazwischen liegenden Metriken (z. B. Drag, Lift).
    """
    known_system = [
        "stufe", "faktor", "meshcells", "dauer_s", "dauer", "duration",
        "abweichungref", "abweichung", "konvergiert", "converged",
        "yplus_p95", "yplus_ok", "normiertref"
    ]
    cols = list(df.columns)
    metrics = []

    for c in cols:
        c_low = c.lower()
        if c_low not in known_system:
            metrics.append(c)

    # Standard-Metriken Fallback
    if not metrics:
        for c in ["Drag", "Lift"]:
            if c in cols:
                metrics.append(c)

    return metrics


def calculate_richardson_extrapolation(factors, values):
    """
    Führt Richardson-Extrapolation für die feinsten 3 bzw. 2 Gitter durch:
    factors: Array aufsteigend nach Feinheit (h1 < h2 < h3)
    values: Array der zugehörigen Werte (f1, f2, f3)
    Rückgabe: (f_extrap, order_p, gci_pct)
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
        h3 = factors[2]
        r23 = h3 / h2 if h2 > 0 else 1.414

        # Monotone Konvergenz prüfen: sign(f2 - f1) == sign(f3 - f2)
        diff12 = f2 - f1
        diff23 = f3 - f2
        if diff12 * diff23 > 0:
            ratio = abs(diff23 / diff12)
            # Ordnung p = ln(ratio) / ln(r)
            try:
                order_p = np.log(ratio) / np.log(r)
                if order_p < 0.5:
                    order_p = 2.0  # Fallback auf theoretische Ordnung
                elif order_p > 4.0:
                    order_p = 2.0
            except Exception:
                order_p = 2.0
        else:
            order_p = 2.0  # Oszillatorische Konvergenz -> theoretische Ordnung 2
    else:
        order_p = 2.0

    # Richardson-Extrapolation: f_extrap = f1 + (f1 - f2) / (r^p - 1)
    denom = (r ** order_p) - 1.0
    if abs(denom) > 1e-6:
        f_extrap = f1 + (f1 - f2) / denom
        # GCI nach Roache (1998): Fs = 1.25 für 3 Gitter
        rel_diff = abs((f1 - f2) / f1) if abs(f1) > 1e-8 else 0.0
        gci_pct = (1.25 * rel_diff / denom) * 100.0
    else:
        f_extrap = f1
        gci_pct = 0.0

    return f_extrap, order_p, gci_pct


def determine_selected_row(df, json_info):
    """
    Ermittelt die gewählte Betriebsstufe:
    1. Aus JSON (SelectedFactor)
    2. Nach Kalibrierregel: Größter Faktor mit Konvergiert==1, YPlus_Ok==1 und Abweichung <= Toleranz
    3. Fallback: feinste Stufe
    """
    selected_factor = json_info.get("selected_factor")
    if selected_factor is not None:
        match = df[np.isclose(df["Faktor"], selected_factor, atol=0.05)]
        if not match.empty:
            return match.iloc[0]

    # Kalibrierregel anwenden
    tol_pct = json_info.get("tolerance", 0.02) * 100.0
    valid_mask = pd.Series(True, index=df.index)
    if "Konvergiert" in df.columns:
        valid_mask = valid_mask & (df["Konvergiert"] == 1)
    if "YPlus_Ok" in df.columns:
        valid_mask = valid_mask & (df["YPlus_Ok"] == 1)

    if "NormiertRef" in df.columns and df["NormiertRef"].notna().any():
        valid_mask = valid_mask & (df["NormiertRef"] <= 1.0)
    elif "AbweichungRef" in df.columns:
        valid_mask = valid_mask & (df["AbweichungRef"] <= tol_pct)

    candidates = df[valid_mask]
    if not candidates.empty:
        # Größter Faktor = gröbstes zulässiges Gitter
        return candidates.sort_values(by="Faktor", ascending=False).iloc[0]

    # Fallback: feinste Stufe (kleinster Faktor)
    return df.sort_values(by="Faktor", ascending=True).iloc[0]


def plot_grid_convergence(df, metrics, json_info, out_dir, formats, show=False):
    """
    Erzeugt den professionellen 4-teiligen Netzkonvergenzplot (01_gitterkonvergenz):
    - Oben links: Primärmetrik (z. B. Drag) über MeshCells mit Richardson-Extrapolation & GCI
    - Oben rechts: Sekundärmetrik (z. B. Lift) oder Skalierung Rechenzeit über MeshCells
    - Unten links: Prozentuale Abweichung / NormiertRef über Verfeinerungsstufe
    - Unten rechts: Wandauflösung y+P95 und Rechenzeit je Stufe
    """
    fig, ((ax1, ax2), (ax3, ax4)) = plt.subplots(2, 2, figsize=(13, 9.5))
    fig.suptitle("Netzunabhängigkeits- und Konvergenzstudie (Grid Convergence)", fontsize=14, fontweight="bold")

    # Nach Feinheit sortieren (feinstes Gitter = kleinster Faktor zuerst für Extrapolation)
    df_sorted_fine = df.sort_values(by="Faktor", ascending=True).reset_index(drop=True)
    df_by_stage = df.sort_values(by="Stufe", ascending=True).reset_index(drop=True)

    selected_row = determine_selected_row(df, json_info)
    selected_factor = selected_row["Faktor"]
    selected_cells = selected_row["MeshCells"]
    tol_pct = json_info.get("tolerance", 0.02) * 100.0

    primary_metric = metrics[0] if metrics else "Drag"
    secondary_metric = metrics[1] if len(metrics) > 1 else None

    # -------------------------------------------------------------
    # PANEL 1: Primärmetrik über MeshCells + Richardson-Extrapolation
    # -------------------------------------------------------------
    if primary_metric in df.columns:
        vals = df_sorted_fine[primary_metric].values
        factors = df_sorted_fine["Faktor"].values
        cells = df_sorted_fine["MeshCells"].values

        # Messpunkte
        ax1.plot(cells, vals, marker="o", color="#1f77b4", linewidth=1.8, label=f"{primary_metric} (Berechnet)", zorder=3)

        # Faktor-Labels an den Punkten
        for c, v, h in zip(cells, vals, factors):
            ax1.annotate(f"h={h:.2f}", (c, v), textcoords="offset points", xytext=(0, 7),
                         ha="center", fontsize=8, color="#333333")

        # Feinste Referenzlinie
        f_ref = vals[0]
        ax1.axhline(f_ref, color="#7f7f7f", linestyle=":", label=f"Referenz h={factors[0]:.2f} ({f_ref:.4f})")

        # Toleranzband um Referenz (±tol_pct %)
        band_lower = f_ref * (1.0 - tol_pct / 100.0)
        band_upper = f_ref * (1.0 + tol_pct / 100.0)
        ax1.axhspan(band_lower, band_upper, color="#2ca02c", alpha=0.12,
                    label=f"Toleranzband (±{tol_pct:.1f} %)")

        # Richardson-Extrapolation
        f_extrap, order_p, gci = calculate_richardson_extrapolation(factors, vals)
        if json_info.get("gci_fine_pct") is not None:
            gci = json_info["gci_fine_pct"]
        if json_info.get("gci_order_p") is not None:
            order_p = json_info["gci_order_p"]

        if f_extrap is not None:
            gci_abs = abs(f_ref * (gci / 100.0)) if gci else 0.0
            # Extrapolierten Punkt am feinsten Gitter oder leicht links zeichnen
            ax1.errorbar(
                [cells[0] * 1.15],
                [f_extrap],
                yerr=[gci_abs],
                fmt="D",
                color="#d62728",
                markersize=6,
                capsize=5,
                elinewidth=1.5,
                label=f"Extrapolation (GCI: ±{gci:.1f} %, p={order_p:.2f})",
                zorder=5
            )

        # Gewähltes Gitter hervorheben
        sel_val = selected_row[primary_metric]
        ax1.scatter([selected_cells], [sel_val], color="#2ca02c", marker="*", s=220,
                    edgecolor="black", label=f"Gewählt: h={selected_factor:.2f}", zorder=6)

        ax1.set_title(f"Konvergenz {primary_metric} über Zellenzahl", fontsize=11, fontweight="bold")
        ax1.set_xlabel("Zellenzahl (MeshCells)", fontsize=10)
        ax1.set_ylabel(primary_metric, fontsize=10)
        ax1.grid(True, linestyle="--", alpha=0.5)
        ax1.legend(loc="best", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 2: Sekundärmetrik (Lift) oder Skalierung Rechenzeit
    # -------------------------------------------------------------
    if secondary_metric and secondary_metric in df.columns:
        vals2 = df_sorted_fine[secondary_metric].values
        factors2 = df_sorted_fine["Faktor"].values
        cells2 = df_sorted_fine["MeshCells"].values

        ax2.plot(cells2, vals2, marker="s", color="#ff7f0e", linewidth=1.8, label=f"{secondary_metric} (Berechnet)", zorder=3)
        for c, v, h in zip(cells2, vals2, factors2):
            ax2.annotate(f"h={h:.2f}", (c, v), textcoords="offset points", xytext=(0, 7),
                         ha="center", fontsize=8, color="#333333")

        f_ref2 = vals2[0]
        ax2.axhline(f_ref2, color="#7f7f7f", linestyle=":", label=f"Referenz h={factors2[0]:.2f} ({f_ref2:.4f})")
        half_band2 = max(abs(f_ref2) * (tol_pct / 100.0), 0.005 if secondary_metric.lower() == "lift" else 0.001)
        ax2.axhspan(f_ref2 - half_band2, f_ref2 + half_band2,
                    color="#2ca02c", alpha=0.12,
                    label=f"Toleranzband (±{half_band2:.4f})" if abs(f_ref2) < 0.05 else f"Toleranzband (±{tol_pct:.1f} %)")

        sel_val2 = selected_row[secondary_metric]
        ax2.scatter([selected_cells], [sel_val2], color="#2ca02c", marker="*", s=220,
                    edgecolor="black", label=f"Gewählt: h={selected_factor:.2f}", zorder=6)

        ax2.set_title(f"Konvergenz {secondary_metric} über Zellenzahl", fontsize=11, fontweight="bold")
        ax2.set_xlabel("Zellenzahl (MeshCells)", fontsize=10)
        ax2.set_ylabel(secondary_metric, fontsize=10)
        ax2.grid(True, linestyle="--", alpha=0.5)
        ax2.legend(loc="best", fontsize=8.5)
    else:
        # Fallback: Rechenzeit über MeshCells im doppelt-logarithmischen Maßstab
        if "Dauer_s" in df.columns:
            dur = df_sorted_fine["Dauer_s"].values
            cells_d = df_sorted_fine["MeshCells"].values
            ax2.loglog(cells_d, dur, marker="o", color="#9467bd", linewidth=1.8, label="Dauer t [s]")
            for c, t, h in zip(cells_d, dur, df_sorted_fine["Faktor"].values):
                ax2.annotate(f"h={h:.2f}\n{t:.1f}s", (c, t), textcoords="offset points", xytext=(0, 8),
                             ha="center", fontsize=8)

            sel_t = selected_row["Dauer_s"]
            ax2.scatter([selected_cells], [sel_t], color="#2ca02c", marker="*", s=220,
                        edgecolor="black", label=f"Gewählt: {sel_t:.1f} s", zorder=6)

            ax2.set_title("Rechenzeit-Skalierung (log-log)", fontsize=11, fontweight="bold")
            ax2.set_xlabel("Zellenzahl (MeshCells)", fontsize=10)
            ax2.set_ylabel("Rechenzeit [s]", fontsize=10)
            ax2.grid(True, which="both", linestyle="--", alpha=0.5)
            ax2.legend(loc="best", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 3: Abweichungen über Verfeinerungsstufen
    # -------------------------------------------------------------
    stages = df_by_stage["Stufe"].values
    factors_by_st = df_by_stage["Faktor"].values
    stage_labels = [f"Stufe {int(s)}\n(h={h:.2f})" for s, h in zip(stages, factors_by_st)]

    if "AbweichungRef" in df_by_stage.columns:
        abw = df_by_stage["AbweichungRef"].values.copy()
        abw_label = "Abweichung [%]"
        # Falls eine Sekundärmetrik nahe 0 liegt (z.B. Lift bei symmetrischem Profil & AoA=0°),
        # explodiert die unnormierte relative Abweichung in AbweichungRef (>100%), während NormiertRef <= 1 bleibt.
        # In diesem Fall zeigen wir die aussagekräftige relative Abweichung der Primärmetrik (Drag).
        if primary_metric in df_by_stage.columns and "NormiertRef" in df_by_stage.columns:
            norm_vals = df_by_stage["NormiertRef"].values
            if np.nanmax(abw) > 100.0 and np.nanmax(norm_vals) <= 2.0:
                ref_p = df_sorted_fine[primary_metric].iloc[0]
                if abs(ref_p) > 1e-8:
                    abw = np.abs(df_by_stage[primary_metric].values - ref_p) / abs(ref_p) * 100.0
                    abw_label = f"Abweichung {primary_metric} [%]"

        bars = ax3.bar(stages, abw, color="#3182bd", alpha=0.75, width=0.55, edgecolor="black", label=abw_label)
        ax3.axhline(tol_pct, color="#d62728", linestyle="--", linewidth=1.5, label=f"Toleranzgrenze ({tol_pct:.1f} %)")

        # Toleranzbereich grün schattieren
        ax3.axhspan(0, tol_pct, color="#2ca02c", alpha=0.12, label="Zulässiger Bereich")

        # Prozentwerte an die Balken
        y_offset = max(np.nanmax(abw), tol_pct) * 0.02
        for b, v in zip(bars, abw):
            ax3.text(b.get_x() + b.get_width() / 2, v + y_offset, f"{v:.1f}%", ha="center", va="bottom", fontsize=8.5)

        # Gewählte Stufe markieren
        sel_stage = selected_row["Stufe"]
        sel_idx = int(np.where(stages == sel_stage)[0][0]) if sel_stage in stages else 0
        ax3.scatter([sel_stage], [abw[sel_idx]], color="#2ca02c", marker="*",
                    s=220, edgecolor="black", label=f"Gewählt (Stufe {int(sel_stage)})", zorder=6)

        # NormiertRef auf Sekundärachse, falls vorhanden
        if "NormiertRef" in df_by_stage.columns and df_by_stage["NormiertRef"].notna().any():
            ax3_twin = ax3.twinx()
            ax3_twin.plot(stages, df_by_stage["NormiertRef"], color="#ff7f0e", marker="D",
                          linewidth=1.5, label="NormiertRef (<= 1.0)")
            ax3_twin.set_ylabel("Normierte Abweichung [-]", color="#ff7f0e", fontsize=10)
            ax3_twin.tick_params(axis="y", labelcolor="#ff7f0e")

        ax3.set_title("Abweichung zur feinsten Referenzstufe", fontsize=11, fontweight="bold")
        ax3.set_xticks(stages)
        ax3.set_xticklabels(stage_labels, fontsize=8.5)
        ax3.set_ylabel("Abweichung [%]", fontsize=10)
        ax3.grid(True, linestyle="--", alpha=0.5)
        ax3.legend(loc="upper right", fontsize=8.5)

    # -------------------------------------------------------------
    # PANEL 4: Wandauflösung y+P95 und Rechenzeit
    # -------------------------------------------------------------
    if "YPlus_P95" in df_by_stage.columns:
        yplus = df_by_stage["YPlus_P95"].values
        color_yp = "#2b5c8f"
        ax4.plot(stages, yplus, marker="o", color=color_yp, linewidth=2.0, label="y+ (95%-Perzentil)")
        ax4.set_ylabel("Wandauflösung y+ [-]", color=color_yp, fontsize=10)
        ax4.tick_params(axis="y", labelcolor=color_yp)

        # Schwellenwertlinien für y+
        ax4.axhline(1.0, color="#2ca02c", linestyle=":", linewidth=1.2, label="y+ <= 1.0 (Low-Re)")
        ax4.axhline(2.0, color="#e6ab02", linestyle=":", linewidth=1.2, label="y+ <= 2.0 (Grenzbereich)")

        # Rechenzeit auf Sekundärachse als Balken
        if "Dauer_s" in df_by_stage.columns:
            ax4_twin = ax4.twinx()
            durations = df_by_stage["Dauer_s"].values
            bars_dur = ax4_twin.bar(stages, durations, color="#7570b3", alpha=0.35, width=0.45, label="Rechenzeit [s]")
            ax4_twin.set_ylabel("Dauer [s]", color="#7570b3", fontsize=10)
            ax4_twin.tick_params(axis="y", labelcolor="#7570b3")
            for b, d in zip(bars_dur, durations):
                ax4_twin.text(b.get_x() + b.get_width() / 2, d * 0.5, f"{d:.0f}s", ha="center", va="center",
                              fontsize=8, color="#333333")

        # Gewählte Stufe markieren
        ax4.scatter([selected_row["Stufe"]], [selected_row["YPlus_P95"]], color="#2ca02c", marker="*",
                    s=220, edgecolor="black", label="Gewählte Stufe", zorder=6)

        ax4.set_title("Wandauflösung y+ und Rechenzeit je Stufe", fontsize=11, fontweight="bold")
        ax4.set_xticks(stages)
        ax4.set_xticklabels(stage_labels, fontsize=8.5)
        ax4.grid(True, linestyle="--", alpha=0.5)
        ax4.legend(loc="upper left", fontsize=8.5)

    plt.tight_layout()
    out_base = out_dir / "01_gitterkonvergenz"
    saved = save_figure(fig, out_base, formats)
    if show:
        plt.show()
    plt.close(fig)
    return saved


def main():
    parser = argparse.ArgumentParser(
        description="Universelle Auswertung von calibration.csv und latest_calibration.json.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("path", help="Pfad zu calibration.csv oder Verzeichnis mit Kalibrierdaten")
    parser.add_argument("--out", default=None, help="Zielordner für die Plots (Standard: <CSV-Ordner>/Plots_Kalibrierung/)")
    parser.add_argument("--formats", default="png", help="Kommagetrennte Liste der Bildformate (z.B. png,pdf,svg; Standard: png)")
    parser.add_argument("--vector", action="store_true", help="Erzeugt zusätzlich Publikations-Vektorgrafiken (.pdf und .svg)")
    parser.add_argument("--show", action="store_true", help="Öffnet interaktive Matplotlib-Fenster")

    args = parser.parse_args()

    csv_file, json_file = find_calibration_files(args.path)
    if not csv_file or not csv_file.is_file():
        print(f"[FEHLER] Keine calibration.csv gefunden unter: {args.path}")
        sys.exit(1)

    print(f"Lese Kalibrierungs-CSV: {csv_file}")
    df = read_calibration_csv(csv_file)
    if df.empty:
        print("[FEHLER] Kalibrierungs-CSV ist leer.")
        sys.exit(1)

    json_info = parse_calibration_json(json_file)
    if json_file:
        print(f"Lese Kalibrierungs-JSON: {json_file}")

    metrics = detect_calibration_columns(df)
    print(f"Erkannte Kalibriermetriken: {metrics}")

    if args.out:
        out_dir = Path(args.out)
    else:
        out_dir = csv_file.parent / "Plots_Kalibrierung"
    out_dir.mkdir(parents=True, exist_ok=True)

    formats = parse_export_formats(args)
    print(f"Erzeuge Kalibrierungs-Plots in: {out_dir} (Formate: {', '.join(formats)})")

    plot_grid_convergence(df, metrics, json_info, out_dir, formats, show=args.show)

    print("Kalibrierungsauswertung erfolgreich abgeschlossen.")


if __name__ == "__main__":
    main()
