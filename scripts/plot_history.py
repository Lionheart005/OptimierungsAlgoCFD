#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
Auswertung und Visualisierung von SU2-Konvergenzverläufen (history.csv, TODO-36).

Unterstützt drei Modi:
  Mode A: Übersichtstabelle über alle Varianten in einem Ordner (Standard ohne --var und ohne --compare).
  Mode B: Detaillierter 3-teiliger Konvergenzplot einer einzelnen Variante (als PNG neben der CSV).
  Mode C: Vergleich mehrerer Varianten in einem Überlagerungs-Plot (--compare).
"""

import sys
import os
import argparse
import json
from pathlib import Path

# Falls --show nicht übergeben wurde, Headless-Backend 'Agg' aktivieren,
# damit automatisierte Skripte/Tests nicht durch blockierende GUI-Fenster hängen bleiben.
if "--show" not in sys.argv:
    import matplotlib
    matplotlib.use("Agg")

import matplotlib.pyplot as plt
import pandas as pd
import numpy as np


# Standard-Präfixe für aerodynamische Beiwerte in SU2
KNOWN_AERO_PREFIXES = ["CD", "CL", "CSF", "CMX", "CMY", "CMZ", "CEFF"]


def parse_su2_cfg(cfg_path):
    """
    Liest ITER, CONV_CAUCHY_ELEMS und CONV_CAUCHY_EPS aus einer SU2-Konfigurationsdatei (.cfg).
    """
    params = {}
    if not os.path.isfile(cfg_path):
        return params

    try:
        with open(cfg_path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                # Kommentare entfernen
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
    """
    Sucht rekursiv nach Su2MaxIterations, CauchyElements und CauchyTolerance
    in einer effective-config.json.
    """
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
    """
    Findet die für die gegebene Datei oder den Ordner geltenden Konfigurationswerte
    (ITER, CONV_CAUCHY_ELEMS, CONV_CAUCHY_EPS).
    """
    target = Path(csv_path_or_dir)
    folder = target if target.is_dir() else target.parent

    # 1. Spezifische Config neben der CSV-Datei suchen (z. B. Config_Gen1_Var2.cfg für History_Gen1_Var2.csv)
    if target.is_file():
        stem = target.stem
        cfg_name = stem.replace("History_", "Config_") + ".cfg"
        specific_cfg = folder / cfg_name
        if specific_cfg.is_file():
            params = parse_su2_cfg(specific_cfg)
            if params:
                return params

    # 2. Beliebige Config_*.cfg im selben Ordner
    for cfg in sorted(folder.glob("Config_*.cfg")):
        params = parse_su2_cfg(cfg)
        if params:
            return params

    # 3. effective-config.json im Verzeichnis oder in Elternordnern
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
    """
    Liest eine SU2-History-CSV ein, bereinigt Spaltennamen (Quotes, Leerzeichen)
    und wandelt numerische Werte um.
    """
    df = pd.read_csv(csv_path, skipinitialspace=True)
    df.columns = [str(c).strip().strip("\"'").strip() for c in df.columns]
    # Unbenannte oder leere Spalten entfernen
    df = df.loc[:, ~df.columns.str.match(r"^Unnamed")]
    df = df.dropna(how="all")
    for col in df.columns:
        df[col] = pd.to_numeric(df[col], errors="coerce")
    return df


def find_iter_column(df):
    """Ermittelt die Spalte der Iteration (Inner_Iter, ITER, Iter, Iteration)."""
    for candidate in ["Inner_Iter", "ITER", "Iter", "Iteration"]:
        for col in df.columns:
            if col.lower() == candidate.lower():
                return col
    return None


def resolve_x_axis(df):
    """
    Ermittelt x-Achsen-Werte und Beschriftung (TODO-37):
    Falls URANS gerechnet wurde (Cur_Time oder Time_Iter vorhanden),
    wird physikalische Zeit oder Time_Iter verwendet und entsprechend beschriftet.
    Sonst reguläre Iteration.
    """
    for col in df.columns:
        if col.lower() in ("cur_time", "curtime", "time", "physical_time"):
            return df[col], "Physikalische Zeit [s]"
    for col in df.columns:
        if col.lower() in ("time_iter", "timeiter", "time_iteration"):
            return df[col], "Zeitschritt (Time_Iter)"
    iter_col = find_iter_column(df)
    if iter_col and iter_col in df.columns:
        return df[iter_col], "Iteration"
    return pd.Series(range(len(df))), "Iteration"


def get_iterations_count(df, iter_col):
    """
    Bestimmt die Anzahl gerechneter Iterationen:
    Wenn iter_col vorhanden ist und der letzte Wert n >= 0 ist -> n + 1 (da SU2 0-basiert zählt).
    Sonst Anzahl Zeilen.
    """
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
    """Gibt alle Spalten zurück, die mit 'rms[' beginnen."""
    return [c for c in df.columns if c.lower().startswith("rms[")]


def compute_residual_drop(df, rms_cols):
    """
    Berechnet den Residuenabfall in Dekaden (erste Zeile minus letzte Zeile),
    da SU2-rms-Werte bereits dekadische Logarithmen sind.
    """
    if not rms_cols:
        return float("nan")
    first_col = rms_cols[0]
    series = df[first_col].dropna()
    if len(series) < 2:
        return float("nan")
    return float(series.iloc[0] - series.iloc[-1])


def detect_metrics(df, requested_metrics=None):
    """
    Ermittelt die relevanten Metriken:
    Falls requested_metrics angegeben ist, werden diese gesucht.
    Andernfalls automatische Erkennung bekannter aero-Koeffizienten (CD, CL, CSF, CMx, CMy, CMz, CEff).
    """
    cols = list(df.columns)
    if requested_metrics:
        matched = []
        for req in requested_metrics:
            req_clean = req.strip()
            # Exakte Übereinstimmung (case-insensitive)
            found = None
            for c in cols:
                if c.lower() == req_clean.lower():
                    found = c
                    break
            if not found:
                # Präfix-Übereinstimmung (z.B. CD für CD_Wall)
                for c in cols:
                    if c.lower().startswith(req_clean.lower()):
                        found = c
                        break
            if found and found not in matched:
                matched.append(found)
        if matched:
            return matched

    # Automatische Erkennung
    found = []
    for prefix in KNOWN_AERO_PREFIXES:
        for c in cols:
            c_upper = c.upper()
            if c_upper == prefix or c_upper.startswith(prefix + "_") or c_upper.startswith(prefix + "-") or c_upper.startswith(prefix + "["):
                if c not in found:
                    found.append(c)

    if found:
        return found

    # Fallback: alle numerischen Spalten außer Iteration und rms[...]
    iter_col = find_iter_column(df)
    fallback = []
    for c in cols:
        if c == iter_col or c.lower().startswith("rms["):
            continue
        fallback.append(c)

    return fallback[:2] if fallback else (["CD"] if "CD" in cols else cols[:1])


def compute_metric_stats(series, cauchy_elems):
    """
    Berechnet Endwert, Min, Max, Mittelwert und Schwingungsamplitude (max - min) / |mean| in %
    über das Cauchy-Fenster (die letzten cauchy_elems Zeilen).
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
    """Extrahiert den Variantennamen aus dem Dateinamen (z.B. History_Gen2_Var3.csv -> Gen2_Var3)."""
    stem = Path(file_path).stem
    if stem.lower().startswith("history_"):
        return stem[8:]
    return stem


def find_history_files(target_path):
    """
    Findet alle History-Dateien:
    - direkt eine Datei
    - im Verzeichnis (inklusive Unterordner Analyseergebnisse/Verlaeufe/ und Verlaeufe/)
    """
    p = Path(target_path)
    if p.is_file():
        return [p]
    if not p.is_dir():
        return []

    candidates = []
    sub1 = p / "Analyseergebnisse" / "Verlaeufe"
    if sub1.is_dir():
        candidates.extend(sorted(sub1.glob("History_*.csv")))
        candidates.extend(sorted(sub1.glob("history*.csv")))

    sub2 = p / "Verlaeufe"
    if sub2.is_dir():
        candidates.extend(sorted(sub2.glob("History_*.csv")))
        candidates.extend(sorted(sub2.glob("history*.csv")))

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
    Mode B: Erzeugt einen 3-teiligen Konvergenzplot für eine Variante.
    Subplot 1: Kennwerte über Iteration
    Subplot 2: Residuen (logarithmisch)
    Subplot 3: Konvergenz-Fenster (Cauchy) mit Toleranzband und Schraffur
    """
    csv_file = Path(csv_path)
    variant_name = extract_variant_name(csv_file)
    df = read_history_csv(csv_file)

    if df.empty:
        print(f"[WARNUNG] Datei {csv_file.name} ist leer. Überspringe Plot.")
        return None

    # Parameter auflösen (CLI überschreibt Config, sonst Code-Standard)
    max_iter = args.max_iter if args.max_iter is not None else cfg_defaults.get("max_iter", 500)
    cauchy_elems = args.cauchy_elems if args.cauchy_elems is not None else cfg_defaults.get("cauchy_elems", 150)
    cauchy_eps = args.cauchy_eps if args.cauchy_eps is not None else cfg_defaults.get("cauchy_eps", 1e-4)

    x, x_label = resolve_x_axis(df)

    metrics = detect_metrics(df, args.metrics)
    rms_cols = find_rms_columns(df)

    fig, (ax1, ax2, ax3) = plt.subplots(3, 1, figsize=(11, 9), sharex=True)
    fig.suptitle(f"Konvergenzverlauf: {variant_name}", fontsize=13, fontweight="bold")

    # -------------------------------------------------------------
    # 1. Kennwerte über Iteration / Zeit
    # -------------------------------------------------------------
    ax1.set_title(f"Kennwerte über {x_label}", fontsize=10, fontweight="bold")
    if len(metrics) == 1:
        m = metrics[0]
        ax1.plot(x, df[m], label=m, color="#1f77b4", linewidth=1.5)
        ax1.set_ylabel(m, fontsize=10)
        ax1.legend(loc="best")
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

        # Gemeinsame Legende
        lines = line1 + line2
        labels = [l.get_label() for l in lines]
        ax1.legend(lines, labels, loc="best")
    else:
        colors = plt.cm.tab10(np.linspace(0, 1, max(len(metrics), 3)))
        for idx, m in enumerate(metrics):
            ax1.plot(x, df[m], label=m, color=colors[idx % len(colors)], linewidth=1.5)
        ax1.set_ylabel("Beiwert", fontsize=10)
        ax1.legend(loc="best")

    ax1.grid(True, linestyle="--", alpha=0.5)

    # -------------------------------------------------------------
    # 2. Residuen (logarithmisch)
    # -------------------------------------------------------------
    ax2.set_title("Residuen (logarithmisch)", fontsize=10, fontweight="bold")
    if rms_cols:
        colors = plt.cm.tab10(np.linspace(0, 1, max(len(rms_cols), 3)))
        for idx, col in enumerate(rms_cols):
            ax2.plot(x, df[col], label=col, color=colors[idx % len(colors)], linewidth=1.2)
        ax2.legend(loc="best")
    else:
        ax2.text(0.5, 0.5, "Keine RMS-Residuenspalten vorhanden", transform=ax2.transAxes, ha="center", va="center", color="gray")

    ax2.set_ylabel("log10(RMS-Residuum)", fontsize=10)
    ax2.grid(True, linestyle="--", alpha=0.5)

    # -------------------------------------------------------------
    # 3. Konvergenz-Fenster (Cauchy)
    # -------------------------------------------------------------
    primary_metric = metrics[0] if metrics else "Wert"
    ax3.set_title(f"Konvergenz-Fenster (Cauchy) – {primary_metric}", fontsize=10, fontweight="bold")

    if primary_metric in df.columns:
        series = df[primary_metric]
        stats = compute_metric_stats(series, cauchy_elems)
        ax3.plot(x, series, label=f"{primary_metric} Verlauf", color="#1f77b4", linewidth=1.5)

        if stats:
            final_val = stats["final_val"]
            window_len = stats["window_len"]

            # Horizontale Linie für Endwert
            ax3.axhline(final_val, color="#2ca02c", linestyle="--", linewidth=1.3, label=f"Endwert ({final_val:.4f})")

            # Toleranzband final_val ± cauchy_eps schattieren
            ax3.axhspan(final_val - cauchy_eps, final_val + cauchy_eps, color="#2ca02c", alpha=0.2, label=f"Toleranzband (±{cauchy_eps:.1e})")

            # Cauchy-Fenster schattieren / schraffieren
            if len(x) >= window_len:
                x_start = x.iloc[-window_len]
                x_end = x.iloc[-1]
                ax3.axvspan(x_start, x_end, color="#7f7f7f", alpha=0.18, hatch="//", label=f"Cauchy-Fenster ({window_len} Iter.)")

            # Y-Achse zoomen, um das Verhalten im Cauchy-Fenster vergrößert darzustellen
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

    out_png = csv_file.with_suffix(".png")
    fig.savefig(out_png, dpi=150, bbox_inches="tight")
    print(f"Plot gespeichert: {out_png}")

    if args.show:
        plt.show()
    else:
        plt.close(fig)

    return out_png


def plot_comparison(history_files, target_dir, args, cfg_defaults):
    """
    Mode C: Überlagert die Kennwert- und Residuenverläufe mehrerer Varianten in einem Plot.
    Speichert History_Compare.png im Zielordner.
    """
    if not history_files:
        print("[FEHLER] Keine Verlaufsdateien für den Vergleich gefunden.")
        return None

    # Erste Datei für Metrikerkennung und Config analysieren
    first_df = read_history_csv(history_files[0])
    metrics = detect_metrics(first_df, args.metrics)
    primary_metric = metrics[0] if metrics else "CD"

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(11, 8), sharex=True)
    fig.suptitle(f"Vergleich der Varianten ({len(history_files)} gerechnet)", fontsize=13, fontweight="bold")

    colors = plt.cm.tab10(np.linspace(0, 1, max(len(history_files), 3)))
    x_label = "Iteration"

    for idx, f in enumerate(history_files):
        var_name = extract_variant_name(f)
        df = read_history_csv(f)
        if df.empty:
            continue

        x, x_label = resolve_x_axis(df)
        color = colors[idx % len(colors)]

        # 1. Kennwert-Verlauf
        if primary_metric in df.columns:
            ax1.plot(x, df[primary_metric], label=var_name, color=color, linewidth=1.5)

        # 2. Residuen-Verlauf (erste gefundene rms[...]-Spalte)
        rms_cols = find_rms_columns(df)
        if rms_cols and rms_cols[0] in df.columns:
            ax2.plot(x, df[rms_cols[0]], label=var_name, color=color, linewidth=1.2)

    ax1.set_title(f"Kennwert: {primary_metric}", fontsize=10, fontweight="bold")
    ax1.set_ylabel(primary_metric, fontsize=10)
    ax1.grid(True, linestyle="--", alpha=0.5)
    ax1.legend(loc="best")

    ax2.set_title("Residuen (logarithmisch)", fontsize=10, fontweight="bold")
    ax2.set_xlabel(x_label, fontsize=10)
    ax2.set_ylabel("log10(RMS-Residuum)", fontsize=10)
    ax2.grid(True, linestyle="--", alpha=0.5)
    ax2.legend(loc="best")

    plt.tight_layout()

    out_png = Path(target_dir) / "History_Compare.png"
    fig.savefig(out_png, dpi=150, bbox_inches="tight")
    print(f"Vergleichs-Plot gespeichert: {out_png}")

    if args.show:
        plt.show()
    else:
        plt.close(fig)

    return out_png


def print_overview_table(history_files, args, folder_cfg):
    """
    Mode A: Druckt eine sauber formatierte Übersichtstabelle aller gefundenen Varianten.
    Spalten: Variante | Iterationen | Konvergiert | Residuenabfall | Metriken (Endwert / Oscillation)
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
            "converged": "ja" if converged else "nein",
            "is_conv": converged,
            "res_drop": f"{res_drop:.2f} Dek." if not np.isnan(res_drop) else "-",
            "metrics": metric_data,
        })

    if not rows:
        print("[HINWEIS] Keine auswertbaren Daten in den Verlaufsdateien gefunden.")
        return

    # Tabellenköpfe und -breiten berechnen
    header_var = "Variante"
    header_iter = "Iterationen"
    header_conv = "Konvergiert"
    header_res = "Residuenabfall"

    col_widths = {
        "variant": max(len(header_var), max(len(r["variant"]) for r in rows)),
        "iterations": max(len(header_iter), max(len(r["iterations"]) for r in rows)),
        "converged": max(len(header_conv), max(len(r["converged"]) for r in rows)),
        "res_drop": max(len(header_res), max(len(r["res_drop"]) for r in rows)),
    }

    for m in detected_metric_names:
        m_title = f"{m} (Endwert / Osc)"
        max_val_len = max(len(r["metrics"].get(m, "-")) for r in rows)
        col_widths[m] = max(len(m_title), max_val_len)

    # Trennlinie und Formatstrings aufbauen
    col_order = ["variant", "iterations", "converged", "res_drop"] + detected_metric_names
    headers_map = {
        "variant": header_var,
        "iterations": header_iter,
        "converged": header_conv,
        "res_drop": header_res,
    }
    for m in detected_metric_names:
        headers_map[m] = f"{m} (Endwert / Osc)"

    def format_row(data_map):
        parts = [f" {str(data_map.get(c, '-')).ljust(col_widths[c])} " for c in col_order]
        return "|" + "|".join(parts) + "|"

    def make_divider(char="-"):
        parts = [char * (col_widths[c] + 2) for c in col_order]
        return "+" + "+".join(parts) + "+"

    divider = make_divider("-")
    header_line = format_row(headers_map)

    print("\nSU2-Konvergenzverläufe Übersicht:")
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
    print(f"Gefunden: {n_total} Varianten ({n_conv} konvergiert, {n_total - n_conv} nicht konvergiert / bei ITER beendet).\n")


def filter_by_variants(history_files, var_filter):
    """Filtert eine Liste von Dateipfaden nach den in var_filter genannten Variantennamen."""
    if not var_filter:
        return history_files

    filtered = []
    # Alle Varianten-Suchmuster normalisieren
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
        description="Auswertung und Plotten von SU2-Konvergenzverläufen (history.csv).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Beispiele:
  # Übersichtstabelle aller Varianten in einem Ergebnisordner anzeigen:
  python scripts/plot_history.py Serverergebnisse/MantaAuv

  # Einzelne Variante plotten (PNG wird neben der CSV gespeichert):
  python scripts/plot_history.py Serverergebnisse/MantaAuv --var Gen2_Var3
  python scripts/plot_history.py Serverergebnisse/MantaAuv/Analyseergebnisse/Verlaeufe/History_Gen2_Var3.csv

  # Mehrere Varianten vergleichen:
  python scripts/plot_history.py Serverergebnisse/MantaAuv --compare
  python scripts/plot_history.py Serverergebnisse/MantaAuv --compare --var Gen1_Var1 Gen2_Var3

  # Interaktiv anzeigen:
  python scripts/plot_history.py Serverergebnisse/MantaAuv --var Gen2_Var3 --show
""",
    )

    parser.add_argument("path", help="Pfad zu einer History-CSV-Datei oder einem Verzeichnis mit Verläufen")
    parser.add_argument("--var", nargs="*", default=None, help="Name der auszuwertenden Variante(n), z.B. Gen2_Var3")
    parser.add_argument("--metrics", nargs="*", default=None, help="Zu plottende Metriken (z.B. CD CL; Standard: automatische Erkennung)")
    parser.add_argument("--compare", action="store_true", help="Erzeugt einen Vergleichs-Plot über mehrere Varianten (History_Compare.png)")
    parser.add_argument("--cauchy-elems", type=int, default=None, dest="cauchy_elems", help="Fenstergröße der Cauchy-Konvergenz (Vorgabe: aus Config oder 150)")
    parser.add_argument("--cauchy-eps", type=float, default=None, dest="cauchy_eps", help="Toleranzband der Cauchy-Konvergenz (Vorgabe: aus Config oder 1e-4)")
    parser.add_argument("--max-iter", type=int, default=None, dest="max_iter", help="Maximale Iterationsanzahl ITER (Vorgabe: aus Config oder 500)")
    parser.add_argument("--show", action="store_true", help="Öffnet das interaktive Matplotlib-Fenster")

    args = parser.parse_args()

    target = Path(args.path)
    if not target.exists():
        print(f"[FEHLER] Pfad existiert nicht: {target}")
        sys.exit(1)

    cfg_defaults = resolve_run_config(target)

    # 1. Fall: Pfad ist eine einzelne Datei
    if target.is_file():
        plot_single_variant(target, args, cfg_defaults)
        sys.exit(0)

    # 2. Fall: Pfad ist ein Ordner
    history_files = find_history_files(target)
    if not history_files:
        print(f"[FEHLER] Keine History_*.csv oder history.csv im Ordner gefunden: {target}")
        sys.exit(1)

    # Wenn --var angegeben ist: filtern
    if args.var:
        matching_files = filter_by_variants(history_files, args.var)
        if not matching_files:
            print(f"[FEHLER] Keine Datei gefunden, die zu den Varianten {args.var} passt.")
            sys.exit(1)
    else:
        matching_files = history_files

    # Mode C: --compare
    if args.compare:
        out_dir = target if target.is_dir() else target.parent
        # Wenn Verlaeufe-Ordner vorhanden, dort speichern, sonst im Zielordner
        if (target / "Analyseergebnisse" / "Verlaeufe").is_dir():
            out_dir = target / "Analyseergebnisse" / "Verlaeufe"
        elif (target / "Verlaeufe").is_dir():
            out_dir = target / "Verlaeufe"
        plot_comparison(matching_files, out_dir, args, cfg_defaults)
        sys.exit(0)

    # Mode B: wenn --var explizit angegeben wurde
    if args.var:
        for f in matching_files:
            plot_single_variant(f, args, cfg_defaults)
        sys.exit(0)

    # Mode A: Standard-Übersichtstabelle
    print_overview_table(matching_files, args, cfg_defaults)


if __name__ == "__main__":
    main()
