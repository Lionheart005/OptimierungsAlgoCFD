# Phase 1: Entschlackung, Bugfixes, Architektur-Sanierung, Tests & Englisch-Basis

> **Status:** ✔ **Abgeschlossen am 22.11.2026** — Alle 3 Etappen (`AP 1.1` bis `AP 1.6`) inklusive Umbenennung zu **`FluidForge`**, vollständiger Englisch-Umstellung, Server-/Branch-Cleanup und realem 10×10-`Naca2D`-End-to-End-Lauf auf `BIC_12` vollständig umgesetzt und verifiziert (**0 Warnungen, 848/848 Unit-Tests + 3/3 echte Integrationstests lokal + realer 100-Varianten-Serverlauf Exit-Code 0**). Bereit für **Phase 2 (`phase2_bedienung_physik_und_ziele.md`)**.  
> **Sprache dieses Plans:** Deutsch (Arbeits- & Planungssprache). **Zielsprache im Code/Repo:** Englisch (AP 1.6).  
> **Voraussetzung:** Schritt 0 (`MantaAuv`-Lauf auf `BIC_12` gestoppt & vollständig archiviert) ist abgeschlossen (`~/Archive/MantaAuv_2026-11-21_final/` + `Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/`).

---

## 1. Ziel & Kontext

Das Framework ist über die letzten Monate auf > 21.000 Zeilen Produktivcode, 17.000 Zeilen Tests und 127 Konfigurations-Keys angewachsen. Vor neuen Features (OpenFOAM, `ShapeKernel`, Hybrid-Optimierer, LLM) muss die bestehende Basis **entschlackt, repariert, architektonisch sauber getrennt und internationalisiert** werden:
1. Alle konkret gefundenen Mathe-, Physik- und Code-Bugs beheben.
2. Gebrochene Schichtengrenzen (`Core/` → `Solvers/`) dauerhaft schließen und automatisiert absichern.
3. Die vier God-Classes (> 1.000 Zeilen) sowie `Program.cs` (904 Zeilen) und 2D/3D-Duplikate sauber zerlegen.
4. Echte Integrations-Tests (PicoGK, Gmsh, SU2) ergänzen statt 100 % gemockter Tests.
5. Noch offene Restpunkte aus den archivierten v1-Plänen sauber abarbeiten.
6. Code, Logs, Fehlermeldungen, CSV-Header, Tests und Nutzer-Doku direkt auf **Englisch** umstellen (und optional den neuen Projektnamen einziehen), damit später kein zweites Groß-Refactoring nötig ist.

---

## 2. Getroffene Grundsatzentscheidungen

### Neuer internationaler Projektname: **`FluidForge`** ✔
* **Entscheidung (22.11.2026):** Das Framework wird im Zuge von `AP 1.6` vollständig von `OptimierungsAlgoCFD` auf **`FluidForge`** umbenannt (`FluidForge.sln`, `src/FluidForge/FluidForge.csproj`, `tests/FluidForge.Tests/FluidForge.Tests.csproj`, Namespaces `FluidForge.*`, Repo-Doku, `-RemoteDir` in `scripts/sim.ps1`).
* **Begründung:** Sofort verständlicher Fluidik-/Generative-Engineering-Bezug, kurz und sauber als C#-Root-Namespace (`FluidForge.Core`, `FluidForge.Solvers.Cfd`), im CFD-/Engineering-/.NET-Umfeld frei von relevanten Kollisionen.

### Getroffene Architektur-Entscheidungen für Phase 1
1. **`Core/` darf strikt KEINE Abhängigkeiten auf `Solvers/`, `Kernels/` oder `Projects/` haben.** Das wird nicht nur repariert, sondern durch einen dauerhaften **Architektur-Unit-Test** erzwungen.
2. **Kein blindes Weiterpflegen von Altlasten:** Das veraltete Schein-`RSM` (tatsächlich nur IDW `1/d³`, unfähig zur Extrapolation) und veraltete/doppelte Konfig-Pfade werden entschlackt bzw. als einfache, klar benannte Baselines (`IdwBaseline` / `EvolutionBaseline`) bereinigt oder aus dem Standard-Fokus entfernt.
3. **Englisch im Code & Produkt, Deutsch in `.plans/`:** Alle öffentlichen Schnittstellen, Konsolenausgaben, Fehlermeldungen, CSV-Spaltennamen, JSON-Kommentare, READMEs und Handbücher werden konsistent Englisch. Zentrale Sammlung von Meldungstexten statt über 160 Dateien verstreute Literale erleichtert Wartung und Tests.
4. **Integrationstests getrennt markieren:** `[Trait("Category", "Integration")]`, damit schnelle Unit-Tests (`dotnet test --filter Category!=Integration`) weiter in Sekunden laufen, echte PicoGK-/Gmsh-/SU2-Tests aber gezielt lokal und auf dem Server ausführbar sind.

---

## 3. Arbeitspakete (Checkliste)

### AP 1.1: Konkrete Bugs & Repo-Hygiene sofort beheben
- [x] **GP-Unsicherheitsband korrigieren (`Core/Algorithms/BayesOptimizationAlgorithm.cs:1147-1185`):** `ISurrogateModel.ObservationNoiseVariance` (Default `0.0`) + `GaussianProcess` (`Math.Exp(_theta[d+1]) * _outputScale^2`) ergänzt; `PredictFitnessBand` addiert `ObservationNoiseVariance` im `Fitness`- und `Metrics`-Modus (latente `Predict`-Varianz bleibt unverändert für EI/Kriging Believer).
- [x] **GCI-Berechnung ehrlich machen (`Core/Pipeline/CalibrationEvaluator.cs` & `MeshCalibration.cs`):** `GciResult` um `bool IsAsymptotic = true` erweitert; bei `!isMonotonic` oder `ratio <= 1.0f` werden `ObservedOrderP`, GCI und Richardson-Extrapolation als `float.NaN` + klare Warnung geliefert (JSON serialisiert `NaN` sauber als `null` via `BuildGciJsonObject`, Konsole zeigt `"n/a"` + Warnung).
- [x] **Geometrie-Messreihenfolge korrigieren (`src/projects/MantaAuv/MantaGeometryGenerator.cs` & `_Vorlage3D/MeinProjektGeometryGenerator.cs`):** `SaveToStlFile` + `StlSmoother.SmoothStl` laufen zuerst; bei erfolgreicher Glättung werden `Volume`, `SurfaceArea`, `FrontalArea` (`REF_AREA`) und `Span` direkt auf dem geglätteten STL via `StlWriter.ReadTriangles` + `MeshMetrics.Measure` gemessen.
- [x] **Zellzahl-Schutz auch in der Optimierung (`Core/Pipeline/WorkflowController.cs`):** Prüft nach Vernetzung `_context.CellLimit` (Kalibrierung) bzw. `_context.Config.MeshCalibration.MaxCells3D` als Sicherheitsdeckel im Optimierungsbetrieb und überspringt bei Überschreitung den Solver (`CellLimitExceededKey = 1f`).
- [x] **Gmsh Schritt-1-Winkel-Retry implementieren (`Solvers/Cfd/GmshCfdMesher.cs`):** `BuildClassifyAngleLadder` (`[configured, 30, 20, 50]`), `WriteGeoScript` akzeptiert optionales `classifyAngleDeg`, Schritt 1 probiert bei Fehlschlag die Leiter durch und protokolliert `BoundaryLayerClassifyAngleDeg` in `_lastMetrics`.
- [x] **2D-Vorkalibrierung Config-Bug fixen (`Program.cs`):** `ResolvePrecalibrationGmsh2DOptions(context)` lädt `solvers/gmsh2d.json` falls vorhanden und erbt sonst kompatible Felder (`TargetYPlus`, `BoundaryLayerGrowthRate`, `DetailBoxLengthFactor`) aus geladenen `GmshMesherOptions`.
- [x] **Deploy- & Repo-Hygiene (Code/Repo) bereinigt:**
  - `src/projects/MantaAuv/OptimierungsAlgoTestlaufBericht.pdf` (2,9 MB) nach `docs/archive/MantaAuv_TestlaufBericht_v1.pdf` verschoben; `.pdf` in `scripts/sim.ps1` (`Get-DeployFiles`) explizit ausgeschlossen.
  - Benutzerspezifische absolute Pfade (`/home/lpleissner/...`) aus `MantaAuv/simulation.json` und `Naca2D/simulation.json` entfernt.
- [x] **Server-/Git-Aufräumpunkte abgeschlossen (22.11.2026):**
  - Vollständig gemergte lokale Branches `WindschiffProjekt` (`dcfbe17`) und `KI_Integration` (`a48dbe2`) gelöscht (`MajorPhysikFixes` existierte bereits nicht mehr).
  - Veralteten Server-Ordner `~/Documents/Automatisierung_v2` auf `BIC_12` entfernt (`~/Documents/AutomatisierungCleanVersion` und `~/Archive/` bewusst unangetastet belassen).
  - `FluidForge` nach `~/Documents/FluidForge` auf `BIC_12` deployed (`0 Warnungen`), `sim.ps1 doctor` grün und `RunTimeEstimator` + `sim.ps1 status`-Prognose (`[FORECAST]`) real in einem vollständigen 10×10-`Naca2D`-Lauf (100 Varianten + 5-Stufen-Kalibrierung + Fine-Verifikation, Exit-Code 0 in ~21 min) verifiziert.

### AP 1.2: `Core/`-Schichtenisolation reparieren & per Architektur-Test erzwingen
- [x] **`MeshCalibration.cs` entkoppelt:** `using OptimierungsAlgoCFD.Solvers.Cfd` entfernt; neue Core-Interfaces `ICalibrationSolverOptions` & `ICalibrationMesherOptions` (`Core/Interfaces/ICalibrationOptionsMetadata.cs`) explizit auf `Su2SolverOptions`, `GmshMesherOptions` und `Gmsh2DMesherOptions` implementiert; `ComputeFingerprint` hasht generisch alle `solvers/*.json` (inkl. `*.local.json`) alphabetisch; generischer JSON-Fallback für `ResolveYPlusLimits` & `ResolveMonitoredMetrics`.
- [x] **`VoxelResolutionAdvisor.cs` entkoppelt:** `using OptimierungsAlgoCFD.Solvers.Cfd` entfernt; neues Core-Interface `IMeshResolutionTarget` (`Core/Interfaces/IMeshResolutionTarget.cs`) auf `GmshCfdMesher` implementiert.
- [x] **SU2-/CFD-Leaks aus `Core/` beseitigt:**
  - `GmshCfdMesher` und `Gmsh2DMesher` implementieren beide `IMeshMetricsSource` und füllen `MeshCells*` via `MeshMetrics.PopulateSu2CellMetrics` selbst; `WorkflowController` liest `MeshCells` primär aus `IMeshMetricsSource` (mit Abwärtskompatibilitäts-Fallback für Mock-Mesher).
  - `ModelRecord.cs`: um `Artifacts` (`Dictionary<string, string>`) und `[JsonIgnore] GeometryPath`-Alias auf `StlPath` erweitert.
  - `Kernels/Planar2D/BBox2.cs` nach `Core/Models/BBox2.cs` verschoben (Ordner = Namespace `OptimierungsAlgoCFD.Core`).
- [x] **Architektur-Test hinzugefügt (`tests/OptimierungsAlgoCFD.Tests/ArchitectureBoundaryTests.cs`):** Erzwingt dauerhaft, dass `Core/` keine `using`/Typzugriffe auf `Solvers`, `Kernels`, `Composition`, `Projects` oder `PicoGK` hat und `Kernels/`/`Solvers/` nie den Namespace `Core` deklarieren. *(Hinweis: Verschiebung verbliebener Hilfsklassen wie `StlWriter`/`StlSmoother`/`RubberBandScaler` erfolgt sauber zusammen mit der Zerlegung in `AP 1.3`.)*

### AP 1.3: Zerlegung der God-Classes & Beseitigung von 2D/3D-Duplikaten
- [x] **`BayesOptimizationAlgorithm.cs` (1.356 → 385 L) zerlegt** in klar getrennte `Core`-Komponenten (alle ≤ 500 L, unveränderte RNG-Reihenfolge & Public-API):
  - `SurrogateModelEnsemble.cs` (294 L): Training, Log-Transform, `Metrics`- vs. `Fitness`-Modus, Erfolgs-GP
  - `AcquisitionOptimizer.cs` (487 L): EI, Kriging Believer, TuRBO-Trust-Region, Exploration-Slot, Batch-Abstoßung
  - `BayesDiagnosticsReporter.cs` (217 L): `PredictFitnessBand` (inkl. `ObservationNoiseVariance`), `MeasureAccuracy`, `Bayes_Verlauf.csv`
  - schlanker `BayesOptimizationAlgorithm.cs` (385 L) als Koordinator
- [x] **`GmshCfdMesher.cs` (1.327 → 298 L) & `Gmsh2DMesher.cs` (600 → 116 L) zusammengeführt/zerlegt:**
  - Gemeinsame Helfer extrahiert: `GmshProcessRunner.cs` (102 L, inkl. gemeinsamem `CopyGeoToFehlerLogs`), `GmshErrorParser.cs` (59 L), `WakeAndDetailBoxBuilder.cs` (155 L), `GmshTunnelBuilder.cs` (424 L), `Gmsh3DGeoScriptBuilder.cs` (335 L), `Gmsh2DGeoScriptBuilder.cs` (378 L).
  - Doppelter `.su2`-NELEM-Parser in `GmshCfdMesher.ReadSu2BoundaryLayerHexShare` entfernt; delegiert jetzt zentral an `MeshMetrics.ReadSu2CellBreakdown`.
- [x] **`Su2Solver.cs` (1.245 → 345 L) zerlegt:**
  - `Su2ProcessRunner.cs` (131 L; zusätzlich um direkten OpenMP-/Single-Rank-Start ohne `mpirun` bei leerem/`"none"` `MpiRunPath` oder `MpiCores <= 1` inkl. `OMP_NUM_THREADS` + 14 Unit-Tests in `Su2ProcessRunnerTests.cs` erweitert), `Su2RestartController.cs` (234 L), `Su2ResultArchiver.cs` (144 L), `Su2DiagnosticsExtractor.cs` (428 L).
- [x] **`MeshCalibration.cs` (1.150 → 477 L) & `Program.cs` (931 → 88 L) entschlackt:**
  - `MeshCalibrationValidation.cs` (281 L) & `MeshCalibrationReporter.cs` (369 L) in `Core/Pipeline/` (strikt ohne äußere Abhängigkeiten).
  - CLI-Parsing und Subcommands in `src/OptimierungsAlgoCFD/Cli/` ausgelagert: `CliArguments.cs` (122 L), `InfoCommands.cs` (98 L), `CalibrationCommand.cs` (176 L), `RecheckCommand.cs` (267 L), `OptimizeCommand.cs` (212 L); `Program.cs` (88 L) behält delegierende `internal static`-Wrapper für Test-Kompatibilität.
  - `CfdProjectDefinition` (28 L) & `Cfd2DProjectDefinition` (29 L) auf gemeinsame Basis `Composition/CfdProjectDefinitionBase.cs` (52 L) vereinheitlicht.

### AP 1.4: Alte Optimierer bereinigen
- [x] **Ehrliche Bereinigung gemäß Nutzerentscheidung (22.11.2026):**
  - **`EvolutionaryAlgorithm` vollständig entfernt** (schlechte Vergleichsleistung gegenüber `Bayes` und IDW): `EvolutionaryAlgorithm.cs`, `EvolutionaryAlgorithmTests.cs` sowie die 5 exklusiven Evolution-Config-Keys (`MinimalDeviationExploration`, `MinimalDeviationExploitation`, `RoleFineTuner`, `RoleCautious`, `RoleNormal`) aus `SimulationConfig.cs`, `MantaAuv`/`Naca2D`/`_Vorlage2D`/`_Vorlage3D`-JSONs, `BEDIENHANDBUCH.md` und Tests entfernt (`"Evolution"` fällt in der Factory mit Warnung auf `"Bayes"` zurück; `ProjectConfig.MaxDeviations` bleibt bis Phase 2 `AP 2.1` unberührt).
  - **`RsmOptimizationAlgorithm` ehrlich in `IdwBaselineAlgorithm` umbenannt & gehärtet:** Neue Datei `Core/Algorithms/IdwBaselineAlgorithm.cs` (`Name = "IDW-Baseline (Inverse Distance Weighting)"`), ehrliche Doku über IDW-Grenzen (keine Extrapolation, keine Unsicherheit), sichere `TryGetValue`-Grenzenprüfung, Umbenennung der 4 Config-Keys auf `IdwVirtualSimulations`, `IdwPower`, `IdwExploitationRatio`, `IdwExploitationSigma` (inkl. JSONs, Handbuch & `IdwBaselineAlgorithmTests.cs`). `OptimizationAlgorithmFactory` unterstützt `"Bayes, IdwBaseline"` und behält `"Rsm"` als Legacy-Alias mit klarem Konsolen-Hinweis.

### AP 1.5: Echte Integrations-Test-Suite aufbauen
- [x] **Testkategorie `[Trait("Category", "Integration")]` etabliert** unter `tests/OptimierungsAlgoCFD.Tests/Integration/` (`dotnet test --filter Category!=Integration` läuft rein als Unit-Suite, `dotnet test --filter Category=Integration` führt alle realen Tests aus).
- [x] **Echter `PicoGK`-Test (`Integration/PicoGkIntegrationTests.cs`, ~0,34 s):** Erzeugt reale Kapsel-Voxel-Geometrie via `PicoGkKernel` (`Library.Go(..., bEndAppWithTask: true)`), exportiert STL, glättet via `StlSmoother`, prüft Wasserdichtheit (`openEdges=0, nonManifold=0`), positive Normalen, Volumen (`4284,8 mm³` vs. analytisch `4297,7 mm³`, Δ 0,30 %) und Stirnfläche (`112,9 mm²` vs. `113,1 mm²`, Δ 0,15 %).
- [x] **Echter 2D-End-to-End-Integrationstest (`Integration/Naca2DEndToEndIntegrationTests.cs`, ~2,43 s):** Reale Kette `Naca2DGeometryGenerator → Gmsh2DMesher → Su2Solver` (OpenMP Direct-Mode, 80 Iter., ohne Mocks): `MeshCells=7866` (`Tri2D=6928`, `Quad2D=938`), `CD=0,03258`, `CL=0,20936`, `YPlus_Median=1,503`, `YPlus_P95=2,021`, `YPlus_Max=2,741`.
- [x] **Echter 3D-Meshing-Integrationstest (`Integration/Gmsh3DPrismIntegrationTests.cs`, ~0,36 s):** Scharfkantiger 3D-Doppelkeil (`50×20×12 mm`) über `GmshCfdMesher` (`BoundaryLayerMode="Prism"`): `TotalCells=5590` (`Tet=3484`, `Prism=2106`, `Pyramid=0`, `Hex=0`, `MeshFallback=0`, `BoundaryLayerStage=1`).

### AP 1.6: Projekt-Umbenennung zu `FluidForge` & komplette Englisch-Umstellung
- [x] **Namensentscheidung finalisiert (22.11.2026):** **`FluidForge`**
- [x] **Solution, Ordner, `.csproj`, Namespaces, Vorlagen & Skripte auf `FluidForge` umbenannt:**
  - `FluidForge.sln`, `src/FluidForge/FluidForge.csproj`, `tests/FluidForge.Tests/FluidForge.Tests.csproj`, sämtliche `FluidForge.*`-Namespaces, `ArchitectureBoundaryTests.cs`.
  - Vorlagen umbenannt: `src/projects/_Template3D/` (`MyProject*`) und `src/projects/_Template2D/` (`MyProject2D*`); `scripts/new-project.ps1`, `scripts/sim.ps1` (`-RemoteDir = 'Documents/FluidForge'`, `-Out = 'server-results'`), `scripts/sim-runner.sh` (`FluidForge.csproj`/`FluidForge.dll`, englische Output-Pfade).
  - `.gitignore` ergänzt um `server-results/`, `__pycache__/`, `*.pyc` (lokales Bestandsarchiv `Serverergebnisse/` bleibt unangetastet & ignoriert).
- [x] **Code, Logs, Output-Pfade, JSONs & CSV-Header vollständig auf Englisch:**
  - Neue zentrale Konventionsklasse `src/FluidForge/Core/Logging/OutputConventions.cs` (`results/`, `error-logs/`, `analysis/histories/`, `calibration/`, `resume/`, `source-snapshot/`, `results.csv`, `Bayes_History.csv`, `verification.csv`, `OVERALL CHAMPION`, strukturierte Log-Tags `[WARNING]`, `[ERROR]`, `[CALIBRATION]`, etc.).
  - Englische CSV-Header (`Bayes_History.csv`, `calibration.csv`, `results.csv`, `verification.csv`), englische `run-info.json`- & `effective-config.json`-Keys, englische Run-Tags (`Cal1`..`Cal5`, `Fine*`, `Su2ResultArchiver.IsControlRunTag`).
  - Python-Plotskripte (`plot_calibration.py`, `plot_history.py`, `plot_optimization.py`) vollständig englisch **mit automatischem Abwärtskompatibilitäts-Fallback** für alte deutsche Archive (`Simulation_Results.csv`, `Analyseergebnisse/Verlaeufe`, alte Spaltennamen).
  - Alle **848 Unit-Tests** (`Category!=Integration`) und **3 Integrationstests** (`Category=Integration`) auf `FluidForge` & englische Verträge umgestellt und 100 % grün (0 Warnungen, 0 Fehler).
- [x] **Aktive READMEs & Projekt-JSON-Kommentare auf Englisch umgestellt:** `README.md`, `THIRD_PARTY_NOTICES.md`, `src/FluidForge/README.md`, `scripts/README.md`, `config/README.md`, `src/projects/README.md`, `src/projects/_Template3D/README.md`, `src/projects/_Template2D/README.md`, `Kernels/PicoGk/README.md`, `Kernels/Planar2D/README.md` sowie sämtliche JSONC-Kommentare unter `src/projects/`. Das Bestands-Handbuch `src/projects/BEDIENHANDBUCH.md` (90 KB) blieb wie vereinbart unangetastet (`HandbookCoverageTests` grün; Ablösung durch modulare englische `docs/` in Phase 2 `AP 2.1`).
- [x] **Hinweis:** `.plans/` blieb vollständig **auf Deutsch** (`.plans/Anmerkungen.txt` unangetastet).

---

## 4. Verifikations- & Abnahmekriterien

1. `dotnet build` mit 0 Warnungen, 0 Fehlern.
2. Alle Unit-Tests grün (`dotnet test --filter Category!=Integration`), inkl. neuem `ArchitectureBoundaryTests` (0 Leaks aus `Core/`).
3. Alle neuen echten Integrationstests (`PicoGK`, `Gmsh`, `SU2`) mindestens einmal real lokal oder auf `BIC_12` grün durchgelaufen.
4. Keine God-Class > ~500–600 Zeilen mehr in `Core/` oder `Solvers/`.
5. `git grep` zeigt keine hartkodierten `/home/lpleissner/...`-Pfade und keine 2,9-MB-PDF im Deploy-Baum mehr.
6. Code, Logs, CSVs und aktive Nutzer-READMEs sind vollständig auf Englisch; `.plans/` bleibt auf Deutsch.

---

## 5. Umsetzungsprotokoll & Handover-Hinweis für Folge-Agenten (`/delegate`)

### Etappe 1 (`AP 1.1` + `AP 1.2`) — ✔ Abgeschlossen am 21.11.2026
- **Entscheidungen & Erkenntnisse:**
  - `ICalibrationSolverOptions` / `ICalibrationMesherOptions` wurden auf den Solver-/Mesher-Optionsklassen **explizit** implementiert, damit bestehende Reflection-Tests (`ConfigSchema`, `HandbookCoverageTests`, `_Vorlage`-Key-Prüfungen) `Is3D` oder `MonitoredMetricNames` nicht als fehlende JSON-Keys melden.
  - `Gmsh2DMesherOptions` besitzt kein `BoundaryLayerCoverage` (nur `TargetYPlus`, `BoundaryLayerGrowthRate`, `DetailBoxLengthFactor`); `ResolvePrecalibrationGmsh2DOptions` übernimmt gezielt diese kompatiblen Werte bzw. lädt `solvers/gmsh2d.json` falls vorhanden, ohne 3D-Keys aus `solvers/gmsh.json` gegen das strenge 2D-Schema zu parsen.
  - `ModelRecord.GeometryPath` trägt `[JsonIgnore]`, damit `RunJournal` (`journal.jsonl`) keine doppelten Pfadfelder serialisiert.
- **Verifikation:** `dotnet test OptimierungsAlgoCFD.sln --verbosity minimal` — **836/836 bestanden** (0 Fehler, 0 übersprungen, 0 Warnungen; +16 neue Tests).

### Etappe 2 (`AP 1.3` + `AP 1.4` + `AP 1.5`) — ✔ Abgeschlossen am 22.11.2026
- **Entscheidungen & Erkenntnisse:**
  - **AP 1.4 Nutzerentscheidung (22.11.2026):** `EvolutionaryAlgorithm` wurde wegen deutlich schwächerer Performance vollständig entfernt (inkl. der 5 Evolution-only-Keys in `SimulationConfig`/JSONs/Handbuch); `RsmOptimizationAlgorithm` wurde ehrlich als `IdwBaselineAlgorithm` (`Idw*`-Keys + Legacy-Alias `"Rsm"`) bereinigt.
  - **Windows OpenMP `SU2_CFD`-Start (wichtige Erkenntnis):** Auf Windows liegt `SU2_CFD.exe` als `win64-omp`-Build ohne `mpirun` im PATH vor. `Su2ProcessRunner.BuildExecutionCommand` startet `SU2_CFD` daher direkt (und setzt bei `MpiCores > 1` automatisch `OMP_NUM_THREADS`), sobald `MpiRunPath` leer/`"none"` oder `MpiCores <= 1` ist; bei konfiguriertem MPI bleibt `-n {MpiCores}` unverändert erhalten.
  - **PicoGK in xUnit:** `PicoGK.Library.Go(..., bEndAppWithTask: true)` kehrt unter Windows sauber aus dem xUnit-Testlauf zurück (ohne vorzeitigen Prozessabbruch); über `[Collection(ProcessWideStateCollection.Name)]` sauber serialisiert.
  - **2D-Quad-Zählung:** `MeshMetrics.ReadSu2CellBreakdown` füllt `SurfaceQuads` nur aus 3D-Wandmarkern (`NMARK`), sodass in 2D `MeshCells_Quad` stets 0 bleibt; der 2D-Integrationstest prüft `Quad2D` (VTK-Typ 9) daher direkt aus `NELEM=`.
  - **Dateigrößen-Transparenz:** Alle 4 zerlegten God-Classes (`BayesOptimizationAlgorithm` 385 L, `GmshCfdMesher` 298 L / `Gmsh2DMesher` 116 L, `Su2Solver` 345 L, `MeshCalibration` 477 L) sowie `Program.cs` (88 L) liegen jetzt alle unter 500 Zeilen. Die übrigen größeren Bestandsdateien außerhalb von AP 1.3 sind aktuell `Su2SurfaceAnalyzer.cs` (858 L), `BoundaryLayerWallField.cs` (683 L), `MeshMetrics.cs` (620 L) und `WorkflowController.cs` (598 L).
  - **Hinweis `Core/Utilities/`:** `StlWriter.cs`, `StlSmoother.cs` und `RubberBandScaler.cs` sind bereits reines `Core` (`System.Numerics`), kein Verschieben erforderlich.
- **Verifikation:** `dotnet build OptimierungsAlgoCFD.sln` (0 Warnungen, 0 Fehler), `dotnet test --filter Category!=Integration` (**848/848 bestanden**), `dotnet test --filter Category=Integration` (**3/3 real bestanden** in ~3,5 s).

### Etappe 3 (`AP 1.6` + offene `AP 1.1`-Server-/Git-Punkte) — ✔ Abgeschlossen am 22.11.2026
- **Entscheidungen & Erkenntnisse:**
  - **Zentrale Konventionen (`Core/Logging/OutputConventions.cs`):** Alle Output-Ordner (`results`, `error-logs`, `analysis`, `histories`, `calibration`, `resume`, `source-snapshot`), Dateinamen (`results.csv`, `Bayes_History.csv`, `verification.csv`, `journal.jsonl`, `effective-config.json`, `run-info.json`) und Log-Tags (`[WARNING]`, `[ERROR]`, `[CALIBRATION]`, `OVERALL CHAMPION`, etc.) liegen zentral an einer Stelle statt als verstreute Literale.
  - **Echte Funde beim Live-Deploy auf `BIC_12`:**
    1. `scripts/sim.ps1` brach beim allerersten Deploy in ein noch nicht existierendes `$RemoteDir` (`Documents/FluidForge`) ab, weil `Invoke-Remote -Quiet` unter PowerShell 5.1 (`$ErrorActionPreference = 'Stop'`) bei `stderr` von `cd` eine terminierende Exception warf. Behoben durch `Test-RemoteDeployed`-Guard in `Invoke-Deploy` sowie temporäres `ErrorActionPreference = 'Continue'` in `Invoke-Remote -Quiet` / `Test-RemoteDeployed`.
    2. `Write-RemoteDeployInfo` in `scripts/sim.ps1` schrieb `DeployTime` statt `DeployedAt` (`RunInfoWriter.DeployInfo`). Angeglichen — `run-info.json` auf `BIC_12` enthält jetzt den exakten `DeployedAt`-Zeitstempel.
    3. `Su2ResultArchiver.IsControlRunTag` prüfte noch `"Kal"`/`"Fein"` statt `"Cal"`/`"Fine"` — durch `ConvergenceAndToleranceTests` sofort gefangen und auf `"Cal"`/`"Fine"`/`"Recheck"` umgestellt.
    4. `scripts/__pycache__/*.pyc` wurde per `.gitignore` ausgeschlossen und aus dem Repo entfernt.
  - **Abwärtskompatible Plot-Skripte:** `scripts/plot_calibration.py`, `plot_history.py` und `plot_optimization.py` lesen primär die neuen englischen Dateinamen/Header, unterstützen aber weiterhin als Fallback die alten deutschen Archive (`Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/`).
  - **Hygiene-Audit:** 0 verbleibende deutsche Umlaute im Code/Skripten/Tests (außer Eigennamen `Möller–Trumbore` / `Leonardo Pleißner` und Link-Anker auf das Bestands-`BEDIENHANDBUCH.md`), 0 Treffer für `lpleissner` außerhalb `.plans/`/`BEDIENHANDBUCH.md`, reine LF-Zeilenenden in `scripts/sim-runner.sh` und `src/FluidForge/setup_server.sh`.
- **Verifikation:**
  - Lokal: `dotnet build FluidForge.sln --no-incremental` (**0 Warnungen, 0 Fehler**), `dotnet test --filter "Category!=Integration"` (**848/848 bestanden**), `dotnet test --filter "Category=Integration"` (**3/3 real bestanden** in 2 s).
  - Server `BIC_12` (`~/Documents/FluidForge`): Deploy + Linux-Build (**0 Warnungen**), `sim.ps1 doctor` grün, vollständiger **10×10-`Naca2D`-End-to-End-Lauf** (5-Stufen-Netzkalibrierung + 100 Bayes-Varianten + Bouncer + Fine-Verifikation + Boundary-Hint + ParaView-VTUs, Champion `GlideRatio = 59.14`, Exit-Code 0, Laufzeit ~21 min) inkl. Live-Verifikation von `RunTimeEstimator` & `sim.ps1 status` (`[FORECAST]`) sowie aller englischen CSV-/JSON-Artefakte.

### Nächste Schritte (Übergabe an Phase 2)
- **Phase 1 ist zu 100 % abgeschlossen.**
- Nächster Arbeitsschritt: **[`phase2_bedienung_physik_und_ziele.md`](phase2_bedienung_physik_und_ziele.md)** starten (`AP 2.1` Konfigurations-Diät mit Presets & modulare englische `docs/` als Ersatz für `BEDIENHANDBUCH.md`, `AP 2.2` Trennung Objectives + Pre-/Post-CFD-Constraints, `AP 2.3` Bouncer-Entkopplung, `AP 2.4` Symmetrieebene/SU2-Konvergenz/Multi-Point/`--smoke-test`, `AP 2.5` C#-Bayes-Feinschliff).
- *Hinweis für nachfolgende Modelle:* Dieser Plan gilt als abgeschlossen; für die nächste `/delegate`-Session direkt `phase2_bedienung_physik_und_ziele.md` als Startpunkt laden.
