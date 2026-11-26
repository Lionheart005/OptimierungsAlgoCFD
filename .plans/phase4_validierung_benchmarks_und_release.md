# Phase 4: Validierung, Physik-Benchmarks, Showcase, Vascular-Kern & Community-Release

> **Status (26.11.2026):** Aktiv — **`4.1` (Server-/Ergonomie-Audit) und `4.2a` (`Naca2D` 3-Seed-Vergleich) sind abgeschlossen.**  
> **Aktueller Fokus:** `4.2b` — Allgemeine OpenFOAM-/SU2-Preset- & Konvergenz-Reife + sauberer Re-Run der 4 Physik-Benchmarks (B1–B3) auf `BIC_12` ([`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md)).  
> **Ziel:** Das System physikalisch nachweisbar genau, skaleninvariant und nutzerfreundlich machen, das große Showcase (`StatorCoolingRing`) rechnen, die vaskuläre Engine sauber ins Hauptprogramm übernehmen, alles auswerten (LaTeX-Bericht + Plots/ParaView) und als Open-Source-Release mit der CFD-Community teilen.

---

## 1. Überblick: Was ist fertig — was steht noch an?

| Block | Thema | Status | Detailplan |
| :--- | :--- | :--- | :--- |
| **AP 4.1** | Stabilitäts-, Ergonomie- & „Zero-to-First-Run“-Audit (`BIC_09`) | **✔ fertig** | archiviert in [`.old/phase4_ap41_und_b1_naca0012.md`](.old/phase4_ap41_und_b1_naca0012.md) |
| **AP 4.2a** | 3-Seed-Optimierer-Vergleich `Naca2D` (`Bayes`, `BoTorch`, `IdwBaseline`) & Calibration-Suffix-Bugfix | **✔ fertig** | `results/Naca2D_Benchmark/summary.md` (`9/9` Läufe `rc=0`) |
| **AP 4.2b** | **Physik-Benchmarks (B1–B3) & allgemeine Solver-/Preset-Reife** (Naca0012 0°-Diagnose, OpenFOAM-Konvergenz/AutoRestart/Auto-Meshing/$y^+$, `MaxCells3D = 15M`, Re-Run auf `BIC_12`) | **▶ JETZT** | **[`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md)** |
| **AP 4.3a** | **Vaskulären Algorithmus (`StatorCoolingRing v3`) nochmals kritisch prüfen & ggf. verbessern** | **offen** *(v3-Basis ✔)* | [`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md) |
| **AP 4.3b** | **Showcase `StatorCoolingRing` vollständig rechnen** (Kalibrierung, `BoTorch qNEHVI` Pareto-Front, Feinprüfung auf `BIC_12`) | **offen** | [`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md) |
| **AP 4.4** | **Vaskuläre Routing-Engine als Kernmodul & Preset (`_TemplateVascular3D`) ins Hauptprogramm übernehmen** | **offen** | [`phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md) |
| **AP 4.5** | **Gesamtauswertung, Visualisierungen, Prof-/Literatur-Preset-Abgleich, LaTeX-Bericht & Community-Release** | **offen** | *(in diesem Masterplan unten)* |

---

## 2. Dauerhafte Literatur-Benchmark-Suite (AP 4.2)

| # | Benchmark | Projekt(e) | Domäne / Solver | Literatur-Referenz | Stand 26.11.2026 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **B1** | **NASA TMR NACA 0012** ($Re = 6\cdot 10^6$, $\alpha = 0^\circ, 10^\circ$) | `BenchmarkNaca0012` | `External2D` / **SU2** | NASA TMR (`CL`, `CD`, `Cp`, `Cf`) | 1. Lauf fertig: $\alpha=10^\circ$ **0,11 %** $C_D$ / **0,85 %** $C_L$; $\alpha=0^\circ$ `CD_v` −1,5 %, aber `CD_p` fehlt (~−15,4 % gesamt) → A/B-Diagnose in `AP 4.2b` |
| **B2** | **90°-Rohrkrümmer** (intern, turbulent, $D = 30\text{ mm}$) | `BenchmarkPipeBend90` | `Internal3D` / **OpenFOAM** + `ShapeKernel` | Idelchik / Miller Druckverlustbeiwert $\zeta$ | 1. Lauf brach bei `6,27 Mio. > MaxCells3D (3M)` ab → skaleninvariantes Auto-Meshing + `MaxCells3D = 15M` in `AP 4.2b` |
| **B3** | **DARPA SUBOFF (Bare Hull AFF-1)** ($Re = 1.2\cdot 10^7$, Symmetrieebene) | `BenchmarkSuboffOpenFoam` + `BenchmarkSuboffSu2` | `External3D` / **OpenFOAM** vs. **SU2** | Liu & Huang (1998) $C_T \approx 0{,}00314\text{–}0{,}0033$ | SU2: `4,53 Mio. > MaxCells3D (3M)`. OpenFOAM: Fehlalarm durch `Cl ≈ 0`-Drift-Bug (`37,49 %`) + zu grobes Netz (`y+ = 143`, `Cd = 0.00414`) → Core-Fix & Auto-Meshing in `AP 4.2b` |

*Zusätzlich (abgeschlossen):* **3-Seed-Optimierer-Vergleich** auf `Naca2D` (`9/9` Läufe `rc=0`, `results/Naca2D_Benchmark/summary.md`: `Bayes` `53.23 ± 0.08` / Verified `66.92 ± 0.33`, `BoTorch` `53.32 ± 0.14` / Verified `66.97 ± 0.25`, `IdwBaseline` `50.04 ± 1.15` / Verified `59.80 ± 0.60`).

---

## 3. Detaillierte Checkliste Phase 4 (in exakter Arbeitsreihenfolge)

### AP 4.1: Stabilitäts-, Ergonomie- & „Fremdnutzer“-Audit — ✔ Abgeschlossen
- [x] **Fresh-Clone-Test („Zero-to-First-Run“)** auf sauberem Ubuntu-Server `BIC_09` vollständig rootless via `setup_server.sh` + `sim.ps1 doctor` + 2D (`Naca2D`) & 3D (`MantaAuv`, `PipeBend90`) verifiziert (`891347d`, `b85091c`, `75e9355`).
- [x] **Fehlermeldungen & Diagnostics** (`OptimizeCommand.Report`, SSH `BatchMode=yes`, `tmux`-FD-Fix, vollständiger `sim.ps1 doctor`-Check).
- [x] **Methodik-Schärfung & Calibration-Bugfix:** `*:VerificationFine` auf manuell höchste Detailstufe umgestellt; Non-Monotonic / False-Coarse-Match-Bug in `CalibrationEvaluator` & `MeshCalibration` behoben (`1074/1074` Tests grün).

> *Hinweis:* Optionales Docker-/Apptainer-Container-Setup ist nach Phase 5 verschoben ([`phase5_docker_container.md`](phase5_docker_container.md)).

### AP 4.2: Physik-Benchmarks (B1–B3) & allgemeine Solver-/Preset-Reife — ▶ JETZT AKTIV
- [x] **4.2a:** 3-Seed-Optimierer-Vergleich `Naca2D` abgeschlossen, heruntergeladen (`results/Naca2D_Benchmark/`) und in `summary.md` zusammengefasst.
- [ ] **4.2b (Detailplan: [`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md)):**
  - [x] Kurze `BenchmarkNaca0012` ($\alpha = 0^\circ$) A/B-Diagnose auf `BIC_12` → `VENKAT_LIMITER_COEFF = 0.05` eindeutig isoliert (`0.5` trifft NASA TMR auf `+0,45 %`); `VenkatLimiterCoeff` in `Su2SolverOptions` & `External2D:VerificationFine = 0.5` umgesetzt.
  - [x] **OpenFOAM-Konvergenz auf SU2-Niveau:** `ResultMetrics`-Filter, `MetricTolerance`, Stationaritäts-/Wirbelerkennung (`OpenFoam_Unsteady` + `TailMean`) und `AutoRestartOnDrift` (echte Prüfung, ob Zielgröße noch driftet) umgesetzt.
  - [x] **`MaxCells3D = 15.000.000`** (inkl. `OpenFoam.MaxGlobalCells = 15.000.000`) + klare Doku zum Anpassen/Abschalten (`0`) umgesetzt.
  - [x] **Skaleninvariantes OpenFOAM-Auto-Meshing** (`Internal3D` nach $D_h$, `External3D` nach $L$ + Wake-Box) + physikbasiertes `TargetYPlus`-`firstLayerThickness` + `nutUSpaldingWallFunction` + 2nd-Order-Turbulenz in `VerificationFine` umgesetzt.
  - [ ] Alle **4 Physik-Benchmarks (B1, B2, B3a, B3b)** auf `BIC_12` fertig rechnen *(läuft aktuell in `tmux physics_benchmarks`)*, herunterladen und gegen Literaturwerte verifizieren — **Augenmerk bei der Auswertung:** falls `BenchmarkSuboffSu2` (`External3D`) ähnlich starke Abweichungen / zu geringen Druckwiderstand zeigt, liegt das sehr wahrscheinlich an `VenkatLimiterCoeff = 0.05` (noch nicht in `External3D:VerificationFine` angehoben → dann gezielt auf `0.3`–`0.5` A/B-testen und anpassen).

### AP 4.3: Showcase-Projekt `StatorCoolingRing` (Review + Optimierungslauf)
- [x] **Mechanik-Basis (`v2`) & Topologie-Evolution (`v3`) implementiert & verifiziert:** Emergentes 12-Pfad-Gefäßnetz (`14D`-DNA, Force-Directed Edge Bundling, 3D-SDF-Hindernis-Lernen, Murray’s Law, maskierte 1D-Vorbalancierung, Rippenmatrix, Post-Fillet-SW-24-Sechskant; `1077/1077` Tests, `BIC_09` `--smoke-test` `6/6`, `14/14 EFFECTIVE`, `87 %` Feasibility, `69 s` `snappyHexMesh`).
- [ ] **4.3a — Vaskulären Algorithmus nochmals prüfen & ggf. verbessern:** Geometrie-Qualität, Verzweigungs-/Rippen-Übergänge, hydraulische Balance, 3D-Druckbarkeit und Zusammenspiel mit dem neuen skaleninvarianten OpenFOAM-Auto-Meshing prüfen und bei Bedarf nachschärfen.
- [ ] **4.3b — Vollständigen Optimierungslauf auf `BIC_12` rechnen:** `StandardRans` + automatische Netzkalibrierung + Pre-/Post-Constraints + `BoTorch qNEHVI` Pareto-Front (`PressureDrop` vs. `FlowImbalance`) + `FinalTopN`-Feinprüfung durchführen und alle Daten + Gewinner-STLs sauber archivieren.

### AP 4.4: Vaskuläre Routing-Engine als Kernmodul & Preset ins Hauptprogramm
- [ ] Umsetzung nach Detailplan [`phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md):
  - Pure-C#-Modul `src/FluidForge/Kernels/Vascular/` (`M×N`-Ports, SDF-KeepIn/KeepOut, DNA-Profile, `Core/` bleibt 100 % physik-/geometrieneutral!).
  - PicoGK-Brücke `VascularVoxelBuilder` unter `src/FluidForge/Kernels/PicoGk/InternalFlow/`.
  - Projektvorlage `_TemplateVascular3D` + `scripts/new-project.ps1 -Template Vascular3D` + englische Doku `docs/vascular-routing.md`.
  - Dogfooding-Refaktorierung von `StatorCoolingRing` (Zero-Regression auf alle Tests & Morphologien).

### AP 4.5: Gesamtauswertung, Technischer LaTeX-Bericht & Community-Release
- [ ] **Auswertung & Visualisierungen:**
  - Benchmarks B1–B3 (`BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam` vs. `BenchmarkSuboffSu2`) und 3-Seed-`Naca2D`-Vergleich vollständig auswerten.
  - Konvergenz-, GCI-, Pareto- und ARD-Sensitivitätsplots (`scripts/*.py`) + ParaView-Renderings (Stromlinien, Druckfelder, $y^+$, Schnittansichten inkl. `StatorCoolingRing` + `*_context_environment.stl`).
  - *(Bonus, falls Lust & Zeit):* Gewinner-STL von `StatorCoolingRing` real 3D-drucken.
- [ ] **Fachlicher Preset-Abgleich vor Veröffentlichung:** Preset-Einstellungen aller Physik-Vorlagen gemeinsam mit Professor / aktueller Literatur nochmals gegenprüfen, damit Nutzer „out of the box“ wissenschaftlich fundierte Vorgaben erhalten.
- [ ] **Englisches technisches Dokument / Showcase-Bericht (LaTeX):** Kompakt, wissenschaftlich sauber, mit Architekturüberblick, Benchmark-Validierung (B1–B3 + 3-Seed-Optimierer), Showcase-Ergebnissen (`StatorCoolingRing` + Vascular-Engine), ehrlicher Diskussion der Grenzen (Low-$Re$/Transition, RANS) und Ausblick auf Phase 5 (LLM) & FEM (Notizen: `docs/report/report_notes.md`).
- [ ] **GitHub-Release-Reife prüfen:**
  - `LICENSE` (MIT) & vollständige `THIRD_PARTY_NOTICES.md` (PicoGK, LEAP71_ShapeKernel, Clipper2, SU2, Gmsh, OpenFOAM, MPICH, optional BoTorch/pymoo).
  - Starke englische `README.md` mit klarem Wertversprechen, Titelbild/ParaView-Visualisierung, Benchmark-Tabelle und 5-Minuten-Quickstart.
- [ ] **Community-Feedback einholen:** Gezielt vorstellen und ehrliche Kritik einsammeln (z. B. **CFD-Online**, **r/CFD**, **LEAP 71 PicoGK GitHub Discussions**, **SU2 / OpenFOAM Community**) und in Phase 5 spiegeln.

---

## 4. Abnahmekriterien für Phase 4

1. Alle drei Literatur-Benchmarks (B1, B2, B3) sind mit den allgemeinen `*:VerificationFine`-Presets vollständig gerechnet, reproduzierbar dokumentiert und liegen innerhalb physikalisch plausibler RANS-Toleranzen (mit ausgewiesenem `y+` und Konvergenzstatus).
2. `StatorCoolingRing` ist vollständig optimiert (Pareto-Front + Feinprüfung) und der vaskuläre Algorithmus ist als allgemeines Modul + `_TemplateVascular3D`-Vorlage im Hauptprogramm verankert.
3. Ein Außenstehender kann ohne Vorwissen anhand der englischen `README.md` in `< 15` Minuten einen ersten Lauf starten und verstehen.
4. Preset-Vorgaben sind mit Literatur/Professor abgeglichen und Community-Feedback ist eingesammelt.
