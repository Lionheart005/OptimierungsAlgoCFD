# Phase 3.5 — Korrektur- & Härtungsplan: Geometrie-Defaults & Multi-Fidelity (`TwoStageFilter`)

> **Status:** ✔ Abgeschlossen (23.11.2026 — umgesetzt via `/delegate`-Pipeline, **0 Warnungen / 0 Fehler**, **990/990 Unit-Tests** & **3/3 lokale `--smoke-test`-Läufe** grün)  
> **Auslöser:** Fachliches Review nach Abschluss von Phase 3 ([`Anmerkungen.txt`](Anmerkungen.txt) & gezieltes Code-Audit vom 23.11.2026).  
> **Ziel:** Zwei konkrete Qualitäts- und Plausibilitätsrisiken vor Phase 4 sauber beseitigen:
> 1. **Geometrie-Defaults & Best Practices:** `UseStlSmoothing` standardmäßig auf `false`, Empfehlung & Standardweg auf **feste Bauteilgröße + relative Formparameter + `UseRubberBandScaler = false`** ausrichten.
> 2. **Multi-Fidelity (`TwoStageFilter`) reparieren & physikalisch absichern:** Gefundene Code-Bugs in SU2-Stage-2, SU2-Stage-1, C#-Bayes-Kompatibilität und BoTorch-Constraints beheben, während `"Mode": "Off"` (normale automatische Netzkalibrierung) strikt der unveränderte Standard bleibt.

---

## 1. Block A: Geometrie-Defaults, Presets & Templates (`StlSmoother` & `RubberBandScaler`)

### Warum das geändert werden muss
1. **`UseStlSmoothing = true` als globaler Default ist riskant:**
   - [`StlSmoother.cs`](../src/FluidForge/Core/Utilities/StlSmoother.cs) wendet Laplace-Premelting, 40 Taubin-Iterationen und eine achsenweise Bounding-Box-Rückskalierung an. Das verändert dünne Kanten/Krümmungen subtil und darf insbesondere bei planaren Multi-Patch-Flanschen (`Internal3D` / `ShapeKernel`) nie unbemerkt aktiv sein.
   - `UseStlSmoothing` soll ein bewusstes **Opt-in** (`false` als Default) sein — nur für grobe organische `Lattice`/`Offset`-Formen (wie `MantaAuv`, wo es explizit in `MantaAuv/simulation.json` auf `true` steht).
2. **`UseRubberBandScaler = true` als Default fördert fehleranfällige Setups:**
   - Skalierung auf `TargetPicoGkSize = 50 mm` erzwingt Fehlerquellen (`DimensionalParameters`, `MetricScaling.Volume/Area/Linear`, Verwechslung von realen mm vs. Arbeitsraum-mm bei Wandstärken) und schrumpft sogar `PipeBend90` still auf 50 mm in PicoGK (effektiv gröbere Voxel!).
   - Strömungsmechanisch sauberer ist eine **feste Referenzgröße** (z. B. feste Sehnenlänge, Rumpflänge oder Anschlussdurchmesser) bei gleichzeitiger Variation **dimensionsloser/relativer Parameter** (`WidthRatio`, `BendRadiusRatio`, `TaperRatio`) und **`UseRubberBandScaler = false`** → konstante $Re$-Vergleichbarkeit, konstante Voxel-Auflösung, keine Skalierungs-Bugs.

### Konkrete Arbeitspakete (Code & Templates)
- [x] **AP 3.5-A1 (`SimulationConfig.cs` Defaults):**
  - `UseStlSmoothing` Default von `true` → **`false`** geändert.
  - `UseRubberBandScaler` Default von `true` → **`false`** geändert (`MantaAuv/simulation.json` setzt weiterhin explizit `"UseRubberBandScaler": true` und `"UseStlSmoothing": true` für Archiv-Reproduzierbarkeit).
- [x] **AP 3.5-A2 (`SimulationPresets.cs`):**
  - In allen drei `Internal3D:*`-Presets (`FastConcept`, `StandardRans`, `VerificationFine`) explizit `UseStlSmoothing = false` und `UseRubberBandScaler = false` gesetzt.
  - Bestehende Projekte (`Naca2D`, `MantaAuv`, `PipeBend90`) lokal via `--smoke-test` verifiziert (alle Exit 0).
- [x] **AP 3.5-A3 (Templates `_Template3D`, `_TemplateInternal3D` & Beispiel `PipeBend90`):**
  - `_Template3D` (`project.json`, `MyProjectProject.cs`, `MyProjectGeometryGenerator.cs`) auf feste `ReferenceLengthMm = 100.0` in `OptimizationTargets` + relative Formparameter (`WidthRatio`, `HeightRatio`) + `"DimensionalParameters": []` umgestellt (12 Keys ≤ 15).
  - `_TemplateInternal3D` (`project.json`, `MyInternalProjectProject.cs`, `MyInternalProjectGeometryGenerator.cs`) auf festen `InletRadiusMm = 10.0` + `LengthToDiameterRatio` + `"DimensionalParameters": []` umgestellt (11 Keys ≤ 15).
  - `PipeBend90` (`project.json`, `PipeBend90Project.cs`, `PipeBend90GeometryGenerator.cs`, `PipeBend90GeometryConstraints.cs`) auf festen `PipeRadiusMm = 12.0` + relative Verhältnisse (`BendRadiusRatio`, `InletStraightRatio`, `OutletStraightRatio`, `RadiusModulationMid`) + `"DimensionalParameters": []` umgestellt (11 Keys ≤ 15, Constraint-Fallback erhalten).
- [x] **AP 3.5-A4 (Schemas, Doku-Defaults & Tests):**
  - `dotnet run --project src/FluidForge -- --export-schemas schemas` ausgeführt (`schemas/simulation.schema.json` synchronisiert).
  - `docs/config-reference.md`, `docs/physics-presets.md` (§6) und `docs/creating-a-project.md` aktualisiert.
  - Unit-Tests (`PresetAndSchemaTests`, `TemplateProjectTests`, `OpenFoamSolverTests`, `HandbookCoverageTests`) ergänzt und grün verifiziert.

---

## 2. Block B: Multi-Fidelity (`TwoStageFilter`) — Gefundene Fehler & physikalische Härtung

> **Grundsatz:** `MultiFidelity.Mode = "Off"` bleibt **unverändert der Standard** in `SimulationConfig` und in allen 9 Presets (`FastConcept`, `StandardRans`, `VerificationFine`). Normale automatische Netzkalibrierung (`MeshCalibration.Mode = "Auto"`) und Single-Fidelity-RANS bleiben immer der primäre, empfohlene Weg.

Beim Code-Audit vom 23.11.2026 wurden **5 konkrete Fehler bzw. Plausibilitäts-Fallstricke** im neuen `TwoStageFilter` gefunden, die vor jeder produktiven Nutzung behoben werden müssen:

### Fehler 1 (Kritisch): Stage-2-Tag `"Fine"` löst bei SU2 versehentlich `ControlRuns` (20.000 Iter.) aus
- **Fundstelle:** [`WorkflowController.cs:207`](../src/FluidForge/Core/Pipeline/WorkflowController.cs#L207) übergibt bei der Stage-2-Promotion `customTag: "Fine"`. [`Su2ResultArchiver.cs:41-48`](../src/FluidForge/Solvers/Cfd/Su2ResultArchiver.cs#L41-L48) wertet jeden Tag, der mit `"Fine"` beginnt, als **Control Run** → [`Su2Solver.cs:354-355`](../src/FluidForge/Solvers/Cfd/Su2Solver.cs#L354-L355) schaltet auf `WithControlRunProfile()` hoch (bis zu **20.000 Iterationen**, `CauchyTolerance = 1e-5`, `CauchyElements = 500`) statt auf das reguläre Optimierungsprofil!
- **Behebung:**
  - [x] Stage-2-Promotion in `WorkflowController.cs` auf `"Stage2"` umgestellt (triggert nicht mehr `Su2ResultArchiver.IsControlRunTag`).
  - [x] `RunJournal`-Kompatibilität & Unit-Tests in `MultiFidelityTests.cs` ergänzt (beweist `IsControlRunTag("Stage2") == false` sowie reguläres `SimulationConfig.Su2MaxIterations`-Budget in Stage 2).

### Fehler 2 (Kritisch): `CoarseMaxIterations = 500` kollidiert bei SU2 mit `TreatUnconvergedAsFailed = true`
- **Fundstelle:** [`SimulationConfig.cs:338`](../src/FluidForge/Core/Models/SimulationConfig.cs#L338) setzt `CoarseMaxIterations = 500`. In OpenFOAM `simpleFoam` (`Internal3D` mit Wall-Functions) reichen 500 Iterationen oft (z. B. `PipeBend90`: ~97 Iter.). Bei turbulenten 3D-SU2-Außenumströmungen (`y+ ≈ 1`, `CauchyElements = 300`) sind 500 Iterationen jedoch viel zu wenig — die Strömung driftet dann noch, und [`Su2DiagnosticsExtractor.cs:222-234`](../src/FluidForge/Solvers/Cfd/Su2DiagnosticsExtractor.cs#L222-L234) markiert die Variante wegen `TreatUnconvergedAsFailed = true` als **`SimulationFailed = true`**, sodass Stage 1 massenhaft Varianten wegwirft statt zu ranken!
- **Behebung:**
  - [x] **`CoarseMaxIterations` Default auf `0`** (uncapped = regulärer Konvergenzabbruch des Solvers) gesetzt.
  - [x] Startup-Warnung über `MultiFidelitySelector.GetStartupWarnings(...)` ergänzt: warnt beim Start mit `[WARNING]`, wenn SU2 mit engem `CoarseMaxIterations` (`<= 2 * CauchyElements` oder `< 1000`) kombiniert wird.
  - [x] **`CoarseMeshFactor` Default von `2.0` auf konservativere `1.5`** angepasst (~3,4× statt 8× weniger Zellen in 3D, deutlich geringeres Rank-Reversal-Risiko).

### Fehler 3 (Wichtig): Eingebauter C#-`Bayes` lernt aus Stage-1-Punkten gar nichts
- **Fundstelle:** [`SurrogateTrainingSet.cs:95`](../src/FluidForge/Core/Surrogates/SurrogateTrainingSet.cs#L95) filtert strikt auf `SameMeshLevel(record, context.EffectiveMeshRefinementFactor)`. Alle nicht-promovierten Stage-1-Varianten (z. B. 8 von 10 pro Runde) haben `MeshRefinementFactor = coarseFactor` und werden von C#-`Bayes` komplett ignoriert. Dadurch verschwendet C#-`Bayes` die Information, welche Regionen in Stage 1 schlecht waren, und kann sie erneut vorschlagen.
- **Behebung:**
  - [x] **Startup-Warnung** in `MultiFidelitySelector.GetStartupWarnings(...)` + `WorkflowController.cs` umgesetzt: warnt bei `TwoStageFilter` + eingebautem C#-`Bayes` und empfiehlt `"OptimizationAlgorithm": "BoTorch"` für echtes Multi-Fidelity-Surrogate-Modeling.
  - [x] **Saubere `SurrogateTrainingSet`-Verbesserung** umgesetzt: netzunabhängige `IsPreConstraintInfeasible`-Records (`PreConstraintPoints` + Pre-Solve-`AllConstraintRecords`) sowie fehlgeschlagene Stage-1-Coarse-Läufe (`ExcludedPoints` + `FailedPoints` außer `MeshFailed`) werden erfasst, ohne grobe CFD-Fitnesswerte oder Post-CFD-Constraints in den High-Fidelity-GP zu mischen.

### Fehler 4 (Wichtig): `botorch_worker.py` verliert Constraints im Multi-Fidelity-Pfad & Fidelity-Vorschlag ungenutzt
- **Fundstelle:** [`scripts/optimizers/botorch_worker.py:445-475`](../scripts/optimizers/botorch_worker.py#L445-L475) erstellt im `elif use_mf:`-Zweig `qLogNoisyExpectedImprovement(model=obj_gps[0], ...)` **ohne `con_gps` / `constraints`**! Bei einem Constrained-Single-Objective-Problem mit Multi-Fidelity werden Nebenbedingungen in BoTorch dadurch still ignoriert. Außerdem ist `SingleTaskMultiFidelityGP` derzeit auf `eff_M == 1` beschränkt, und `WorkflowController` nutzt das zurückgelieferte `candidate.fidelity` im `TwoStageFilter`-Modus nicht (fester zweistufiger Ablauf).
- **Behebung:**
  - [x] Im `use_mf`-Pfad von `scripts/optimizers/botorch_worker.py` werden `con_fid_by_idx` gesammelt und bei aktiven Constraints `(d+1)`-dimensionale `con_gps_mf` (`SingleTaskMultiFidelityGP` bei ≥2 Fidelities mit Fallback auf `SingleTaskGP`) via `ModelListGP(obj_gps[0], *con_gps_mf)` in `qLogNEI_MultiFidelity_Constrained` integriert — mit sauber geloggtem Fallback auf `qLogNEI_Constrained_MFFallback` (High-Fidelity-Subset) statt stillem Weglassen von Constraints.
  - [x] Unit-Test (Request-Serialisierung & Worker-Vertrag) + `[PythonOptFact]`-Live-Test `BoTorch_MultiFidelity_Constrained_Preserves_Constraints_Live` in `ExternalOptimizerTests.cs` ergänzt.

### Fehler 5 (Qualitätssicherung): Transparenz über Rank-Reversal im Log
- **Problem:** Der Nutzer merkt bei `TwoStageFilter` aktuell gar nicht, ob das grobe Netz die Rangfolge der Varianten überhaupt verlässlich abbildet oder ob Grob und Fein völlig verschiedene Ergebnisse liefern.
- **Behebung:**
  - [x] `MultiFidelitySelector.CompareStage1AndStage2(...)` (`StageCandidateComparison`, `StageComparisonReport`) + Log-Ausgabe im `WorkflowController` ergänzt: loggt nach jeder Stage-2-Promotion `[MULTI-FIDELITY] Stage-1 vs Stage-2 delta: ...` inkl. relativer Abweichungen und emittiert `[WARNING]` bei Rank-Reversal, > 25 % Abweichung oder Stage-2-Fehler.

---

## 3. Verifikations- & Abnahmekriterien (alle ✔ erfüllt am 23.11.2026)

1. [x] `dotnet build FluidForge.sln -warnaserror` → **0 Warnungen, 0 Fehler**.
2. [x] Alle bestehenden + neuen Unit-Tests grün (`dotnet test --filter "Category!=Integration"` → **990/990 bestanden, 0 fehlgeschlagen, 0 übersprungen**), inkl.:
   - `UseStlSmoothing == false`, `UseRubberBandScaler == false`, `CoarseMeshFactor == 1.5`, `CoarseMaxIterations == 0` als neue Defaults,
   - Stage-2-Promotion nutzt Tag `"Stage2"` und triggert **nicht** `Su2ResultArchiver.IsControlRunTag`,
   - Startup-Warnungen bei SU2 + engem `CoarseMaxIterations` sowie bei C#-`Bayes` + `TwoStageFilter`,
   - Stage-1-`PreConstraint`/`SimulationFailed`-Tracking in `SurrogateTrainingSet`,
   - Constrained-Multi-Fidelity-Tests in `ExternalOptimizerTests.cs` + `python -m py_compile scripts/optimizers/botorch_worker.py` (OK),
   - Stage-1-vs-Stage-2-Diagnostik & Rank-Reversal-Warnung im Log.
3. [x] `HandbookCoverageTests`, `PresetAndSchemaTests` und `TemplateProjectTests` zu 100 % grün, `schemas/simulation.schema.json` frisch über `--export-schemas schemas` synchronisiert.
4. [x] `Naca2D --smoke-test` (PASS 6/6, 3/3), `MantaAuv --smoke-test` (PASS 6/6, 5/5) und `PipeBend90 --smoke-test` (PASS 6/6, 4/4) lokal alle mit Exit-Code `0` gelaufen.

---

## 4. Abschluss- & Übergabeprotokoll (Handover für Folge-Agenten)

### Getroffene Architekturentscheidungen
- **Globale Defaults auf `false` (`UseRubberBandScaler`, `UseStlSmoothing`):** Saubere Trennung zwischen neuem Standard (reale mm, feste Referenzgröße + relative Formparameter) und Altprojekten (`MantaAuv/simulation.json` setzt beide explizit auf `true`, sodass volle Reproduzierbarkeit gegenüber dem Archiv `2026-11-21` erhalten bleibt). Zusätzlich sichern alle `Internal3D:*`-Presets beide Flags explizit als `false` ab.
- **`Core/`-Isolation bei Solver-/Optimierer-Warnungen:** `MultiFidelitySelector.GetStartupWarnings(...)` arbeitet rein auf `SimulationConfig`, `IReadOnlyList<string> solverNames` und `string optimizerName` — keine Abhängigkeit von `Core/` zu `Solvers.Cfd` (`ArchitectureBoundaryTests` bleibt 100 % sauber).
- **BoTorch Constrained Multi-Fidelity:** `con_gps` (`d`-dim) bleiben für `FeasibilitySearch` und `pred_feas_list` erhalten; im `use_mf`-Pfad werden eigene `(d+1)`-dim `con_gps_mf` für `ModelListGP(obj_gps[0], *con_gps_mf)` gebaut, mit robustem Fallback auf `qLogNEI_Constrained_MFFallback`.

### Gefundene Besonderheiten & Hinweise
- Auf dem lokalen Windows-Host ist `torch`/`botorch` nicht in Python 3.14.7 installiert (`py_compile` war OK); der neue Live-Test `BoTorch_MultiFidelity_Constrained_Preserves_Constraints_Live` (`[PythonOptFact]`) sollte beim nächsten Deploy auf `BIC_12` zusammen mit `dotnet test --filter "Category=PythonOpt"` live gegen `opt-py` ausgeführt werden.
- Keine bestehenden Unit-Tests außerhalb von `MultiFidelityTests`, `OpenFoamSolverTests`, `PresetAndSchemaTests` und `TemplateProjectTests` brachen durch die Default-Umstellung von `UseRubberBandScaler` / `UseStlSmoothing`.

### Ausstehende nächste Schritte (Handover)
1. **Optionaler Server-Sync (`BIC_12`):** Änderungen committen/deployen und auf `BIC_12` einmal `dotnet test --filter "Category=PythonOpt"` (inkl. neuem Constrained-MF-BoTorch-Live-Test) laufen lassen.
2. **Start von Phase 4 ([`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md)):** Feature-Freeze, Zero-to-First-Run-Ergonomie, optionales Docker-/Apptainer-Setup ([`phase4_docker_container.md`](phase4_docker_container.md)), 3 dauerhafte Literatur-Benchmarks (NASA TMR NACA 0012, 90°-Rohrkrümmer, DARPA SUBOFF), Showcase-Projekt, LaTeX-Bericht & Release.
