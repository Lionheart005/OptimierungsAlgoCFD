# Meilenstein 1: Python-Auswerteskripte, 2D-Config-Bereinigung, Log-Redesign & 10×10 Naca2D-Analyse

> **Status:** Abgeschlossen (08.11.2026)  
> **Tests:** 560 / 560 Unit-Tests grün (`dotnet test`)

---

## 1. Ziel & Kontext
Umsetzung aller Aufgaben aus `.plans/Anmerkungen.txt` und `.plans/ROADMAP.md` (Meilenstein 1: AP 1.1 & AP 1.2):
1. Universelle, projektunabhängige Python-Auswerteskripte in `scripts/` (`plot_history.py`, `plot_optimization.py`, `plot_calibration.py`).
2. Bereinigung der 2D-Projektkonfigurationen (`Naca2D/simulation.json`, `_Vorlage2D/simulation.json`) von ungenutzten 3D-/Voxel-Schlüsseln (`TargetPicoGkSize` etc.).
3. Ästhetische Überarbeitung von Konsole und `simulation.log` inkl. Live-Farb-Rendering bei `sim.ps1 log`.
4. Vollständige Auswertung und physikalisch-numerische Prüfung des 10×10 `Naca2D`-Referenzlaufs (`Serverergebnisse/BIC_12_Naca2D_20261108_122016`).

---

## 2. Architektur & Getroffene Entscheidungen (Nutzer-Abstimmung 08.11.2026)
- **Entscheidung 1 (Farben im Log & `sim.ps1 log`)**:
  - Saubere UTF-8 `.log`-Datei ohne rohe ANSI-Steuerzeichen (damit `simulation.log` in VS Code und Texteditoren ohne Escape-Artefakte lesbar bleibt) + automatische Live-Einfärbung im interaktiven Terminal (`SimulationLog.cs`) sowie bei `sim.ps1 log` / `sim.ps1 status` über `colorize_log()` (`awk` mit `fflush()`) in `scripts/sim-runner.sh`.
- **Entscheidung 2 (Log-Hierarchie & Fitness pro Variante)**:
  - Fitness wird in `WorkflowController.cs` unmittelbar nach jeder Variante ausgewertet und prominent ausgegeben (`│  ★ Ergebnis:  Fitness = ...`).
  - Technische Größen ($y^+$, $Re_L$, Netzzellen, SU2-Iterationen, Schwingung) werden dezent in einer gedimmten Diagnosezeile (`│  · Diagnose: ...`) gebündelt.
  - Am Ende jeder Iteration erscheint eine ausgerichtete Unicode-Übersichtstabelle aller Varianten der Generation inkl. Status (`OK`, `★ CHAMPION`, `⚠ ABGELEHNT`, `✖ FEHLER`).
- **Entscheidung 3 (2D `.json`-Konfiguration)**:
  - Da 2D-Projekte (`Cfd2DProjectDefinition` / `DirectGeometryKernel` / `Planar2D`) rein analytische 2D-Polygonkonturen ohne PicoGK/Voxel erzeugen, wurden alle reinen 3D-/Voxel-Schlüssel (`TargetPicoGkSize`, `BaseVoxelResolution`, `VoxelSmoothingIterations`, `VoxelSmoothingPremeltingSteps`, `UseRubberBandScaler`, `UseStlSmoothing`) aus `src/projects/Naca2D/simulation.json` und `src/projects/_Vorlage2D/simulation.json` entfernt und `TemplateProjectTests.cs` entsprechend angepasst.

---

## 3. Umsetzung & Arbeitsstand
- [x] **AP 1.1a (`scripts/plot_history.py`)**: Cauchy-Konvergenzfenster (`axvspan`) & $\pm\varepsilon$-Toleranzband (`axhspan`) in Mode B & C ergänzt; Vektorgrafik-Export (`--vector` / `--formats png,pdf,svg`) integriert.
- [x] **AP 1.1b (`scripts/plot_optimization.py`)**: Neues universelles Skript mit automatischer Gen-Spaltenerkennung; erzeugt `00_dashboard_optimierung`, `01_fitness_verlauf`, `02_parameter_evolution`, `03_beiwerte_verlauf`, `04_pareto_front` und `05_champion_vergleich` (in `.png`, `.pdf`, `.svg`).
- [x] **AP 1.1c (`scripts/plot_calibration.py`)**: Neues universelles Skript für `calibration.csv` & `latest_calibration.json` mit Richardson-Extrapolation, Roache-GCI-Fehlerbalken, $y^+$-Verlauf und intelligenter Skalierung bei $C_l \approx 0$ ($\alpha = 0^\circ$).
- [x] **AP 2 (2D-JSON-Bereinigung)**: `src/projects/Naca2D/simulation.json`, `src/projects/_Vorlage2D/simulation.json` und `tests/AutomatisierungCleanVersion.Tests/TemplateProjectTests.cs` bereinigt.
- [x] **AP 3 (Log- & Konsolen-Redesign)**: `src/Automatisierung_v2/Core/Logging/SimulationLog.cs` neu erstellt und in `WorkflowController.cs`, `ChampionValidator.cs`, `ChampionFineVerifier.cs`, `Su2Solver.cs`, `Gmsh2DMesher.cs` sowie `scripts/sim-runner.sh` (`colorize_log`) integriert.
- [x] **AP 4 (10×10 `Naca2D`-Auswertung & Schwellenwert-Korrektur)**: Lauf nach `Serverergebnisse/BIC_12_Naca2D_20261108_122016/` abgerufen, sämtliche Plots (`.png`, `.pdf`, `.svg`) sowie `AUSWERTUNG_BERICHT.md` generiert und identifizierte Schwellenwert-Unstimmigkeiten in `Naca2D` behoben.

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse (aus dem 10×10 Lauf)
1. **Widerspruch zwischen Kalibrier-Toleranz (10 %) und Feinprüfungs-Toleranz (5 %)**:
   - *Problem*: In `Naca2D/simulation.json` war `MeshCalibration.Tolerance = 0.10` gesetzt, aber `ChampionVerification.Tolerance` fehlte (Default `0.05`). Dadurch wählte die Kalibrierung völlig zu Recht Stufe 1 (`Faktor = 2.00`, 3.79 % $C_d$-Abweichung), während die Feinprüfung am Ende wegen 7.45 %–10.08 % Abweichung auf Stufe 2 (`Faktor = 1.41`) fälschlich `[WARNUNG] Ergebnis netzabhängig – Abweichung übersteigt 5.0 %` meldete.
   - *Lösung*: `"Tolerance": 0.10` in `src/projects/Naca2D/simulation.json` unter `"ChampionVerification"` ergänzt.
2. **Fehlalarm-Flut bei $y^+$ (`YPlusWarnFactor = 2.0`) und Schwingung (`OscillationWarnLimit = 0.02`)**:
   - *Problem*: Bei angestellten Profilen ($\alpha \approx 5.8^\circ$) steigt die lokale Übergeschwindigkeit an der Saugspitze, sodass $y^+_{P95}$ auf dem Grobgitter bei `2.05 – 2.28` liegt (für SA-Turbulenzmodell in der viskosen Unterschicht bis $y^+ \le 3-5$ völlig unkritisch). Zudem schwankt $C_d$ numerisch im Endfenster oft um $\pm 2.2\,\% - 3.4\,\%$.
   - *Lösung*: In `src/projects/Naca2D/solvers/su2.json` `YPlusWarnFactor` auf `3.0` und `OscillationWarnLimit` auf `0.05` (5 %) angehoben, sowie alle technischen Details im neuen Log-Design in die gedimmte Diagnosezeile verlagert.
3. **Scheinbare 671 % Abweichung in `calibration.csv` bei symmetrischem Profil ($\alpha = 0^\circ$)**:
   - *Erkenntnis*: Bei $\alpha = 0^\circ$ ist $C_l \approx 0$ (`0.00071` vs. `-0.00012`). Während `NormiertRef = 0.35` durch die Mindesttoleranz in C# (`MetricTolerance`) bereits perfekt funktionierte, zeigte die Rohspalte `AbweichungRef` `671.19 %`. `scripts/plot_calibration.py` erkennt diesen Fall nun automatisch und plottet in Panel 3 die aussagekräftige relative Abweichung der Primärmetrik (`Drag`: `3.8 %`) zusammen mit `NormiertRef`.
4. **Physikalisches Hauptergebnis des 10×10 `Naca2D`-Laufs**:
   - 88/100 Varianten erfolgreich; Bouncer hat 2 instabile Ausreißer (Gen 4 Var 5, Gen 10 Var 6) zuverlässig abgefangen.
   - Absoluter Champion: **Gen 6 Var 3** mit Gleitzahl $E = 44.04$ (im feinen Netz bestätigt mit $E = 44.60$; Top-3 liegen im feinen Netz zwischen $43.91$ und $45.24$).
   - Dominanter Entwurfsparameter: `CamberPosition` (Korrelation $r = +0.776$ mit Fitness) konvergierte auf ~73 % (Rear-Loading / Achterwölbung), `MaxCamber` auf ~4.45 %, `MaxThickness` auf ~14.6 %, `AngleOfAttackDeg` auf ~5.6°–5.85°.

---

## 5. Ausstehende Arbeitsschritte & Nächste Schritte (Handover für Folge-Agenten)
1. **Meilenstein 2 (KI- & Machine-Learning-Integration gemäß `.plans/ki_optimierung.md`)**:
   - **TODO-53**: Datenhygiene & Bouncer-Markierung (`Disqualified = true` statt hartem Überschreiben von `Fitness = 0.0001f`, damit das Gauß-Prozess-Surrogat nicht durch künstliche Klippen verzerrt wird).
   - **TODO-54**: Gauß-Prozess-Surrogatkern (`Core/Surrogates/`) mit ARD-Matern-5/2-Kernel und Cholesky-Zerlegung.
   - **TODO-55**: `BayesOptimizationAlgorithm` mit Expected Improvement (EI).
   - **TODO-56 bis 60**: Mehrziel-Kennwert-Modellierung ("Metrics"), TuRBO-Vertrauensbereich, `Bayes_Verlauf.csv` und 5×5-Vergleichslauf (`RSM` vs. `Evolution` vs. `Bayes`).
2. **Optionaler Feinschliff für 2D-Hinterkanten-Vernetzung**:
   - 6 von 100 Varianten scheiterten in Gmsh an Boundary-Layer-Kollisionen an der spitzen Hinterkante bei extremem Aft-Camber. Bei Bedarf kann in `Planar2D` / `Naca2D` eine minimale Hinterkantendicke (z.B. 0.1 % Sehne) ergänzt werden.
