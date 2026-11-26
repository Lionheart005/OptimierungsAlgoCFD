# Phase 4.2 — OpenFOAM-/SU2-Preset-Reife, Konvergenz-Upgrade, Skaleninvariantes Auto-Meshing & Physik-Benchmarks (B1–B3)

> **Status:** AP 0–AP 4 & AP 5 (Tests, Peer-Review `APPROVE`, Deploy `1cffca99592528b4`) abgeschlossen; 4-Benchmark-Kette läuft auf `BIC_12` in `tmux` (`physics_benchmarks`) (Stand 26.11.2026)  
> **Vorgänger (archiviert):** [`.old/phase4_presets_benchmarks_und_calibration_fix.md`](.old/phase4_presets_benchmarks_und_calibration_fix.md), [`.old/phase4_b1_2d_fix_und_naca2d_benchmark.md`](.old/phase4_b1_2d_fix_und_naca2d_benchmark.md)  
> **Ziel:** Alle 4 Physik-Benchmarks (`BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2`) durch **allgemeingültige** Core- & Preset-Verbesserungen (ohne Einzelprojekt-Hacks!) sauber, reproduzierbar und physikalisch valide auf `BIC_12` durchrechnen und gegen Literaturwerte auswerten.

---

## 1. Ausgangslage & Befunde vom 1. Benchmark-Lauf (`BIC_12`, 26.11.2026)

Die Ergebnisse des ersten `*:VerificationFine`-Laufs auf `BIC_12` (`Hash e0738467fe29e1f6`) wurden komplett nach `results/Benchmark*/` heruntergeladen (und auf `BIC_12` vor dem Re-Run unter `results/_archive_run1_e0738467_*/` archiviert) und analysiert:

| Benchmark | Ergebnis (26.11.2026, Run 1) | Diagnostizierte Hauptursache(n) |
| :--- | :--- | :--- |
| **B1 `BenchmarkNaca0012`** (`External2D:VerificationFine`, `475.899` Zellen) | **Konvergiert**, aber $\alpha=0^\circ$: `CD = 0.00693` (**−15,44 %** ggü. TMR `0.00819`); $\alpha=10^\circ$: `CD = 0.01232` (**+0,11 %**), `CL = 1.08158` (**−0,85 %** / PG **+0,01 %**) | Durch AP 0 zu 100 % isoliert: hartcodiertes `VENKAT_LIMITER_COEFF = 0.05` erzeugte übermäßige Dissipation im Subsonik-/Inkompressibel-Regime und zerstörte $C_{D,p}(0^\circ)$ (`0.00006` statt `0.00121`). |
| **B2 `BenchmarkPipeBend90`** (`Internal3D:VerificationFine`, `1.0x`) | **Abgebrochen vor Solver (`FailedStage = Mesh`)**: `6.267.279` echte Fluidzellen > `MaxCells3D = 3.000.000` | (1) `MaxCells3D = 3.000.000` zu niedrig. (2) Starre `BaseCellSizeMm = 1.2 mm` + `Level (2,3)` skalierte nicht mit $D_h = 30\text{ mm}$. |
| **B3a `BenchmarkSuboffOpenFoam`** (`External3D:VerificationFine`, `0.8x`, `131.230` Zellen) | **Solver-Fail nach 178 Iter.** (`Trailing-window drift 37.49% > 0.50%`), `Cd = 0.00414` (**+32 %** ggü. `ReferenceCt = 0.00314`), `y+ = 143` | (1) 3 Bugs in `OpenFoamConvergenceJudge` (`Cl ≈ 1e-6` ohne `MetricTolerance`, `PressureDrop` in `External3D`, kein `AutoRestartOnDrift`). (2) Ignoriertes `TargetYPlus`, `nutkWallFunction`, manuelle Mesh-Overrides in `openfoam.json`. |
| **B3b `BenchmarkSuboffSu2`** (`External3D:VerificationFine`, `0.8x`) | **Abgebrochen vor Solver (`FailedStage = Mesh`)**: `4.533.028` Zellen (`31` Prism-Layer, `y+ = 1`) > `MaxCells3D = 3.000.000` | Zu niedriges `MaxCells3D = 3.000.000`-Cap im Core. |

---

## 2. AP 0 Messergebnisse (`BenchmarkNaca0012`, $\alpha = 0^\circ$ auf `BIC_12`)

Isolierte 2×2-Faktoriell-A/B-Diagnose in `/tmp/naca0012_ab_diag` auf `BIC_12` (18 Kerne, `orig.su2`, `475.899` Zellen, `INC_RANS`, `SA`, `Re = 6e6`):

| Fall | `SLOPE_LIMITER_FLOW` | `VENKAT_LIMITER_COEFF` | `MUSCL_TURB` | $C_D$ | $C_{D,p}$ | $C_{D,v}$ | $C_L$ | Abweichung vs. NASA TMR (`0.00819`) |
| :--- | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **NASA TMR Ref** | — | — | — | **`0.00819`** | **`0.00121`** | **`0.00698`** | `0.00000` | `0.00 %` |
| **A0 Baseline** | `VENKATAKRISHNAN` | `0.05` | `NO` | `0.006930` | `0.000060` | `0.006870` | `+0.000110` | **−15,38 %** |
| **B1** | `VENKATAKRISHNAN` | `0.05` | `YES` | `0.006931` | `0.000043` | `0.006879` | `+0.000115` | **−15,37 %** |
| **B3** *(gewählt)* | `VENKATAKRISHNAN` | **`0.5`** | `NO` | **`0.008227`** | **`0.001342`** | **`0.006884`** | **`-0.000010`** | **+0,45 %** |
| **B2** | `VENKATAKRISHNAN` | `1.0` | `NO` | `0.008197` | `0.001314` | `0.006882` | `+0.000252` | +0,09 % |
| **A1** | `VENKATAKRISHNAN` | `1.0` | `YES` | `0.008301` | `0.001403` | `0.006897` | `+0.000957` | +1,35 % |
| **B4** | `NONE` | — | `NO` | Divergenz (Iter 13) | — | — | — | NaN |

**Fazit (Konsens mit Peer-Reviewer `gemini-4-hydrogen-xhigh`):**
- `VENKAT_LIMITER_COEFF = 0.05` war eindeutig die alleinige Ursache. `MUSCL_TURB = YES` bringt keinen Vorteil (verschlechtert Symmetrie); `NONE` divergiert; ein dickeres 2D-Grenzschichtnetz ist nicht erforderlich.
- `Su2SolverOptions.VenkatLimiterCoeff` (Default `0.05f`, validiert `[CONFIG]` `> 0` & finite) eingeführt; in `External2D:VerificationFine` auf **`0.5f`** gesetzt (beste Symmetrie `CL = -1e-5` und robust für $\alpha = 10^\circ$). `External3D:VerificationFine` bleibt zunächst bei `0.05f`, bis eine 3D-SUBOFF-Baseline vorliegt.

---

## 3. Architektur & Getroffene Entscheidungen (inkl. Peer-Reviewer-Abstimmung)

Alle Entscheidungen wurden im `/delegate`-Verfahren vor Umsetzung (Pre-Decision Review) und nach Umsetzung inkl. Korrekturschleife (Post-Implementation & Delta Review) mit `reviewer` (`gemini-4-hydrogen-xhigh`) abgestimmt und mit beidseitigem **`APPROVE`** abgenommen:

1. **Gemeinsame Konvergenzstatistik (`Core/Pipeline/ConvergenceStatistics.cs`)**: `BatchStatistics` und `DetrendedPeakToPeak` aus `Su2HistoryAnalyzer` extrahiert und von SU2 sowie OpenFOAM gemeinsam genutzt.
2. **OpenFOAM-Konvergenz & `AutoRestartOnDrift` (AP 1)**:
   - `OpenFoamPostProcessor`: `FindAllDatFilesChronological` + zeitstempelbasiertes Mergen über alle Restart-Ordner (`postProcessing/<func>/<time>/`), späterer Ordner überschreibt überlappende Zeitstempel; `EvaluateYPlus` ignoriert leere Restart-Dateien.
   - `OpenFoamConvergenceJudge`: strikte Filterung auf `ResultMetrics.Values` (inkl. Missing-Series-Guard & Domain-Filter im Leer-Fallback), `MassBalanceError`-Gate auf `Internal3D` begrenzt (in `External3D` weiterhin geloggt), `MetricTolerance` (`rtol * |mean| + atol` inkl. `AbsoluteTolerances`), Stationaritätserkennung via `BatchStatistics` (`OpenFoam_Unsteady = 1`, `Uncertainty_<Metric>` relativ, `TailMean`, kein Fail bei stationärer Wirbelablösung).
   - `OpenFoamSimpleSolver`: `decomposePar` & `potentialFoam` einmal vor der Schleife; `reconstructPar -latestTime` & `foamToVTK` nach der Schleife; Restart-Schleife (`AutoRestartOnDrift = true`, `MaxConvergenceRestarts = 3`, `ResidualTargetFloor = 1e-7f`) prüft `verdict.MassBalanceOk && verdict.HasMonotonicDrift`, prüft Zeitordner in `processor0/<t>/` (parallel) bzw. `<caseDir>/<t>/` (seriell), respektiert `endTime` aus `controlDict`, setzt `startFrom latestTime;` und patcht mittels `MatchEvaluator` strikt nur den `residualControl`-Block in `fvSolution`.
3. **Zellzahl-Obergrenzen & Transparenz (AP 2)**:
   - `MeshCalibrationConfig.MaxCells3D = 15_000_000`, `OpenFoamOptions.MaxGlobalCells = 15_000_000`, `MaxLocalCells = 4_000_000` (bei `MpiCores <= 1` automatisch auf `MaxGlobalCells` angehoben).
   - `MeshCalibration.cs` behandelt `MaxCells3D <= 0` und `MaxCells2D <= 0` konsistent überall als unlimitiert (inkl. `RefineAdaptively` und Preview-Hint).
   - Dokumentiert in `README.md`, `docs/physics-presets.md` und `docs/config-reference.md`.
4. **Skaleninvariantes OpenFOAM Auto-Meshing & Physikbasiertes `TargetYPlus` (AP 3)**:
   - `OpenFoamMeshPlan` in `OpenFoamCaseInfo` speist `blockMeshDict`, `snappyHexMeshDict`, `meshDict` (`cfMesh`) und `OpenFoamSnappyMesher.PopulateMetrics` zentral.
   - Alle 6 `Internal3D:*` / `External3D:*` Presets nutzen `BaseCellSizeMm = 0` & `BoundaryLayerLayers = 0` als Auto-Trigger (Code-Defaults `2.0` / `5` und explizite Werte `> 0` inkl. manuellem `RelativeLayerSizes = false` bleiben 100 % abwärtskompatibel).
   - `Internal3D`: skaliert nach $D_h$ (`CellsPerCharacteristicLength`: `10 / 20 / 32`) mit Guard `dx_base <= D_h / 5` und konsistent nachgezogenem `targetSurfM`.
   - `External3D`: Fernfeld `0.25 * L * rf`, Oberfläche nach $L$ (`CellsPerCharacteristicLength`: `60 / 120 / 200`), automatische `LevelMin/Max`, sowie automatische `wakeBox` (`+3L`) & `nearfieldBox` `refinementRegions` (Level `>= 1`, an Tunnel & `SymmetryPlane` geclippt).
   - Physikbasiertes Layer-Sizing: `firstLayerThickness = 2 * y_p` (Zellzentrum!), `minThickness = max(0.25 * firstLayer, 1e-8)` in Metern, `minVol 1e-18`, Auto-Layer-Anzahl `[3, 28]` (Expansion adaptiv bis `1.25`).
   - All-$y^+$ Wandbehandlung: `nutUSpaldingWallFunction` für `kOmegaSST`/`kOmegaSSTLM`/`SpalartAllmaras`, `kLowReWallFunction` für `0/k` bei `TargetYPlus <= 5` (`TargetYPlus = 5.0` in `StandardRans` & `VerificationFine`), `SecondOrderTurbulence = true` (`bounded Gauss linearUpwind grad(...)` + `cellLimited Gauss linear 1`) in `*:VerificationFine`.
   - `BenchmarkSuboffOpenFoam/solvers/openfoam.json` vollständig von manuellen Mesh-Overrides bereinigt.

---

## 4. Umsetzung & Arbeitsstand

- [x] **AP 0**: `BenchmarkNaca0012` ($\alpha = 0^\circ$) 2×2-A/B-Diagnose auf `BIC_12` durchgeführt, `VENKAT_LIMITER_COEFF = 0.05` eindeutig isoliert, mit `reviewer` abgestimmt (`APPROVE`).
- [x] **AP 1**: OpenFOAM-Konvergenz auf SU2-Niveau (`ConvergenceStatistics.cs`, `OpenFoamPostProcessor.cs`, `OpenFoamConvergenceJudge.cs`, `OpenFoamSimpleSolver.cs`, `OpenFoamOptions.cs`) umgesetzt.
- [x] **AP 2**: `MaxCells3D = 15_000_000`, `MaxGlobalCells = 15_000_000`, `MaxLocalCells = 4_000_000`, `0`-Unlimited-Fix in `MeshCalibration.cs`, Schemas regeneriert (`schemas/*.schema.json`), Doku in `README.md`, `docs/physics-presets.md`, `docs/config-reference.md` aktualisiert.
- [x] **AP 3**: Skaleninvariantes Auto-Meshing (`OpenFoamMeshPlan`), `TargetYPlus`-Layer-Sizing, `nutUSpaldingWallFunction`, `kLowReWallFunction`, `SecondOrderTurbulence`, Preset-Updates (`SimulationPresets.cs`) & Bereinigung von `BenchmarkSuboffOpenFoam/solvers/openfoam.json`.
- [x] **AP 4**: `Su2SolverOptions.VenkatLimiterCoeff`, `Su2ConfigGenerator`-Validierung/Ausgabe & `External2D:VerificationFine` (`0.5f`).
- [x] **AP 5a (Verifikation & Review)**:
  - `dotnet build FluidForge.sln -warnaserror` → **0 Warnungen / 0 Fehler**.
  - `dotnet test --no-build --filter "Category!=Integration"` → **1104 bestanden / 0 fehlgeschlagen / 0 übersprungen** (inkl. neuer Suiten `OpenFoamConvergenceAndPostProcessorTests.cs` & `OpenFoamAutoSizingAndRestartTests.cs`).
  - `--print-config` & `--smoke-test` für alle 6 Projekte (`BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2`, `PipeBend90`, `StatorCoolingRing`) mit Exit-Code `0` bestanden.
  - Post-Implementation & Delta Review durch `reviewer` (`gemini-4-hydrogen-xhigh`) → **`APPROVE`**.
- [x] **AP 5b (Deploy & Start auf `BIC_12`)**: Deploy `1cffca99592528b4` (`0W/0E`), `doctor` für SU2 & OpenFOAM grün, Run-1-Ergebnisse auf `BIC_12` archiviert, 4-Benchmark-Kette (`scripts/run_physics_benchmarks_on_server.sh`) in `tmux physics_benchmarks` gestartet.
- [ ] **AP 5c (Auswertung nach Laufende)**: Nach `results/PHYSICS_BENCHMARKS_DONE` auf `BIC_12` alle 4 Ergebnisordner herunterladen und physikalisch gegen NASA TMR, Idelchik/Miller ($\zeta$) und Liu & Huang (DARPA SUBOFF $C_T$) auswerten.

---

## 5. Gefundene Fehler, Probleme & Wichtige Erkenntnisse

- **`.NET Regex.Replace`-Falle bei `PatchFvSolutionResidualControl` (`OpenFoamSimpleSolver.cs:308`)**: Ersatzstring `$"$1{targetStr}$2"` mit `targetStr = "1E-05"` wurde von .NET als nicht-existierende Gruppe `$11` interpretiert und korrumpierte `fvSolution` (`$11E-05;`). Durch neuen Unit-Test sofort entdeckt und auf `MatchEvaluator` (`inner => inner.Groups[1].Value + targetStr + inner.Groups[2].Value`) umgestellt.
- **`snappyHexMesh` `minThickness`- & `minVol`-Fallen bei `relativeSizes false`**: Default `MinLayerThickness = 0.1` bedeutet bei `relativeSizes false` `0,1 m` (10 cm!) und löscht alle Layer; ebenso schneidet `minVol 1e-13` dünne High-Re-Zellen ab. Gelöst durch `minThickness = max(0.25 * firstLayer, 1e-8)` und `minVol 1e-18`.
- **Serieller `maxLocalCells`-Deckel**: Bei `MpiCores == 1` deckelte `maxLocalCells = 4M` trotz `maxGlobalCells = 15M`. Gelöst durch automatische Anhebung auf `max(MaxLocalCells, MaxGlobalCells)` im seriellen Modus.
- **Peer-Review-Funde (#18)**: `canRestart` auf explizites `verdict.MassBalanceOk && verdict.HasMonotonicDrift` eingeschränkt (verhindert sinnlose Restarts bei reinem Massenbilanz-Fail); `External3D`-Leer-Fallback filtert `PressureDrop`; manuelles `RelativeLayerSizes = false` bleibt bei `BaseCellSizeMm > 0 && BoundaryLayerLayers > 0` unangetastet; `targetSurfM` wird nach `dh / 5.0f`-Clamp nachgezogen.
- **AP 0 SU2 Restart-Dateiname**: SU2 schreibt bei `OUTPUT_FILES = (RESTART)` auf `BIC_12` `restart.dat` (nicht `restart_flow.dat`).

---

## 6. Ausstehende Arbeitsschritte & Nächste Schritte (Handover)

1. **Status auf `BIC_12` überwachen**:
   - `ssh BIC_12 "tmux ls; cat ~/Documents/FluidForge/results/physics_benchmarks.log; ls -la ~/Documents/FluidForge/results/PHYSICS_BENCHMARKS_*"`
2. **Nach `PHYSICS_BENCHMARKS_DONE` Ergebnisse herunterladen**:
   - Für jedes der 4 Projekte (`BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2`) via `.\scripts\sim.ps1 fetch -Project <Name>` nach `results/` herunterladen.
3. **Physikalische Validierung gegen Literatur (Abnahmekriterien)**:
   - **B1 `BenchmarkNaca0012`**: Verifizieren, dass $\alpha = 0^\circ$ jetzt nahe NASA TMR (`0.00819`, erwartet `~0.00823` / `+0.45 %`) liegt UND $\alpha = 10^\circ$ (`CD ≈ 0.01231`, `CL ≈ 1.091` / PG-korrigiert) mit `VenkatLimiterCoeff = 0.5` stabil konvergiert.
   - **B2 `BenchmarkPipeBend90`**: Realistische Zellzahl durch $D_h$-Auto-Meshing, `MassBalanceError < 1 %`, $\zeta$ gegen Idelchik/Miller prüfen.
   - **B3a `BenchmarkSuboffOpenFoam` vs. B3b `BenchmarkSuboffSu2`**: Beide Solver ohne Projekt-Mesh-Hacks auf $C_T$ gegen Liu & Huang (1998, `0.00314–0.0033`) quervergleichen; prüfen ob `AutoRestartOnDrift`, `TargetYPlus = 5` und `External3D` Auto-Meshing sauber greifen.
   - **Wichtiges Augenmerk `VenkatLimiterCoeff` (insbesondere `External3D` / `BenchmarkSuboffSu2`):**
     - `External3D:VerificationFine` (sowie `External2D:StandardRans` und `External3D:StandardRans`) laufen aktuell bewusst noch auf dem konservativen Default `VenkatLimiterCoeff = 0.05` (erst saubere 3D-Baseline abwarten).
     - **Falls `BenchmarkSuboffSu2` (`External3D`) ähnlich große Abweichungen bzw. einen systematisch zu niedrigen Druckwiderstand / $C_T$ zeigt** (analog zu den −15,4 % bzw. fehlenden ~11,5 Drag Counts $C_{D,p}$ bei `BenchmarkNaca0012` $\alpha = 0^\circ$), ist **`VenkatLimiterCoeff = 0.05` der primäre Verdächtige** (übermäßige numerische Dissipation im Subsonik-/Inkompressibel-Regime). In diesem Fall direkt auf dem fertigen SUBOFF-Netz einen gezielten A/B-Test (`0.05` vs. `0.3` / `0.5`) fahren und den Wert bei stabiler Konvergenz im Preset `External3D:VerificationFine` (auf `0.3`–`0.5`) sowie nach erfolgreichem $\alpha = 10^\circ$-Check auch in `External2D:StandardRans` (auf `0.3`–`0.5`) anheben (`FastConcept` bleibt wegen Robustheit bei `0.05`).
