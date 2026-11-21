# Phase 1: Entschlackung, Bugfixes, Architektur-Sanierung, Tests & Englisch-Basis

> **Status:** In Arbeit — **Etappe 1 (`AP 1.1` Code-/Hygiene-Fixes & `AP 1.2` `Core/`-Isolation + Architektur-Test) am 21.11.2026 abgeschlossen und verifiziert (836/836 Tests grün, 0 Warnungen).** Offen: Server-/Branch-Cleanup (`AP 1.1`), Etappe 2 (`AP 1.3`–`AP 1.5`), Namensentscheidung & Etappe 3 (`AP 1.6`).  
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

## 2. Getroffene Grundsatzentscheidungen & Offener Vorab-Punkt

### Offener Vorab-Entscheidungspunkt: Neuer internationaler Projektname
* **Entscheidung (21.11.2026):** Die Umbenennung weg von `OptimierungsAlgoCFD` soll direkt im Zuge von Phase 1 erfolgen (Namespaces, `.sln`/`.csproj`, Repo-Doku, `-RemoteDir` in `sim.ps1`). Der finale Name wird **kurz vor Start von AP 1.6** festgelegt.
* **Namensvorschläge als Inspiration (für später, wenn Phase 1 startet):**
  - **`FlowForge`** / **`ForgeFlow`** (Computational Engineering + Generative Fluidics)
  - **`AeroVex`** / **`VoxelFlow`** (Fokus auf Voxel/Implicit + Fluidik)
  - **`OpenCEM`** / **`PicoOpt`** / **`PicoFlow`** (direkte Nähe zu Computational Engineering Models / PicoGK)
  - **`VeriFlow`** / **`VeriOpt`** (betont das Alleinstellungsmerkmal: *Verifiable* Computational Engineering Harness)
  - **`MorphFlow`** / **`SynthFlow`** (algorithmische Formfindung + Strömung)

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
- [ ] **Offene Server-/Git-Aufräumpunkte (bei nächstem Server-Kontakt / nach Commit):**
  - Alte Git-Branches (`WindschiffProjekt`, `MajorPhysikFixes`, `KI_Integration`) nach Prüfung bereinigen; auf `BIC_12` veralteten Ordner `src/Automatisierung_v2` aufräumen.
  - `RunTimeEstimator` & `sim.ps1 status`-Prognose (aus `.old/zeitschaetzung.md`) einmalig auf `BIC_12` beim nächsten Test-Deploy verifizieren.

### AP 1.2: `Core/`-Schichtenisolation reparieren & per Architektur-Test erzwingen
- [x] **`MeshCalibration.cs` entkoppelt:** `using OptimierungsAlgoCFD.Solvers.Cfd` entfernt; neue Core-Interfaces `ICalibrationSolverOptions` & `ICalibrationMesherOptions` (`Core/Interfaces/ICalibrationOptionsMetadata.cs`) explizit auf `Su2SolverOptions`, `GmshMesherOptions` und `Gmsh2DMesherOptions` implementiert; `ComputeFingerprint` hasht generisch alle `solvers/*.json` (inkl. `*.local.json`) alphabetisch; generischer JSON-Fallback für `ResolveYPlusLimits` & `ResolveMonitoredMetrics`.
- [x] **`VoxelResolutionAdvisor.cs` entkoppelt:** `using OptimierungsAlgoCFD.Solvers.Cfd` entfernt; neues Core-Interface `IMeshResolutionTarget` (`Core/Interfaces/IMeshResolutionTarget.cs`) auf `GmshCfdMesher` implementiert.
- [x] **SU2-/CFD-Leaks aus `Core/` beseitigt:**
  - `GmshCfdMesher` und `Gmsh2DMesher` implementieren beide `IMeshMetricsSource` und füllen `MeshCells*` via `MeshMetrics.PopulateSu2CellMetrics` selbst; `WorkflowController` liest `MeshCells` primär aus `IMeshMetricsSource` (mit Abwärtskompatibilitäts-Fallback für Mock-Mesher).
  - `ModelRecord.cs`: um `Artifacts` (`Dictionary<string, string>`) und `[JsonIgnore] GeometryPath`-Alias auf `StlPath` erweitert.
  - `Kernels/Planar2D/BBox2.cs` nach `Core/Models/BBox2.cs` verschoben (Ordner = Namespace `OptimierungsAlgoCFD.Core`).
- [x] **Architektur-Test hinzugefügt (`tests/OptimierungsAlgoCFD.Tests/ArchitectureBoundaryTests.cs`):** Erzwingt dauerhaft, dass `Core/` keine `using`/Typzugriffe auf `Solvers`, `Kernels`, `Composition`, `Projects` oder `PicoGK` hat und `Kernels/`/`Solvers/` nie den Namespace `Core` deklarieren. *(Hinweis: Verschiebung verbliebener Hilfsklassen wie `StlWriter`/`StlSmoother`/`RubberBandScaler` erfolgt sauber zusammen mit der Zerlegung in `AP 1.3`.)*

### AP 1.3: Zerlegung der God-Classes & Beseitigung von 2D/3D-Duplikaten
- [ ] **`BayesOptimizationAlgorithm.cs` (1.354 L) zerlegen** in klar getrennte Komponenten:
  - `SurrogateModelEnsemble` (Training, Log-Transform, `Metrics`- vs. `Fitness`-Modus, Erfolgs-GP)
  - `AcquisitionOptimizer` (EI, Kriging Believer, Exploration-Slot, Batch-Abstoßung)
  - `BayesDiagnosticsReporter` (`PredictFitnessBand`, `MeasureAccuracy`, `Bayes_Verlauf.csv`)
  - schlanker `BayesOptimizationAlgorithm` als Koordinator (~250–350 L)
- [ ] **`GmshCfdMesher.cs` (1.259 L) & `Gmsh2DMesher.cs` (594 L) zusammenführen/zerlegen:**
  - Gemeinsame Basis/Helfer (`GmshProcessRunner`, `GmshGeoScriptBuilder`, `GmshErrorParser`, `WakeAndDetailBoxBuilder`) extrahieren; doppelten `.su2`-NELEM-Parser (`GmshCfdMesher.cs:1176`) entfernen und zentral nutzen.
- [ ] **`Su2Solver.cs` (1.245 L) zerlegen:**
  - Trennung von Prozessausführung (`Su2ProcessRunner`), Auto-Restart-Orchestrierung (`Su2RestartController`), Ergebnis-/VTU-Archivierung (`Su2ResultArchiver`) und Diagnose-Extraktion (`y+`, `CD_p`/`CD_v`).
- [ ] **`MeshCalibration.cs` (1.061 L) & `Program.cs` (904 L) entschlacken:**
  - CLI-Parsing und Subcommands (`--calibrate`, `--recheck`, `--list-projects`, `--print-config`, `--smoke-test`) aus `Program.cs` in eigene `Cli/`-Handler auslagern.
  - `CfdProjectDefinition` und `Cfd2DProjectDefinition` auf eine gemeinsame konfigurierbare Basis vereinfachen.

### AP 1.4: Alte Optimierer bereinigen
- [ ] **Ehrliche Bereinigung von `RsmOptimizationAlgorithm` & `EvolutionaryAlgorithm`:**
  - `EvolutionaryAlgorithm.cs`: Unnormierten euklidischen Distanzcheck (`IsTooSimilarToHistory`) auf normierten `[0,1]^d`-Raum umstellen, verschwendeten Parent-Re-Eval-Slot (`Variant 1`) fixen, sinnvolle Sigma-Grenzen setzen.
  - `RsmOptimizationAlgorithm.cs`: Entweder ehrlich als `IdwBaseline` kennzeichnen/verschlanken oder komplett als Legacy entfernen, falls `Bayes` + `Evolution` (und später `BoTorch`/`pymoo`) alle Anwendungsfälle sauberer abdecken (Entscheidung mit kurzem Check in der Session).

### AP 1.5: Echte Integrations-Test-Suite aufbauen
- [ ] **Testkategorie `[Trait("Category", "Integration")]` etablieren** (im Standard-`dotnet test` optional ausfilterbar, in CI/Lokal gezielt ausführbar).
- [ ] **Echter `PicoGK`-Test:** Erzeugt eine reale kleine Voxel-Geometrie, exportiert STL, glättet sie, prüft Wasserdichtheit, Volumen und projizierte Stirnfläche.
- [ ] **Echter 2D-End-to-End-Integrationstest (`Planar2D → Gmsh2D → SU2`):** Schneller grober NACA-0012-Einzellauf (Dauer < 30–60 s), der echten Netzbau, echten SU2-Aufruf, `history.csv`-Parsing und `y+`-Auswertung ohne Mocks prüft.
- [ ] **Echter 3D-Meshing-Integrationstest (`STL → Gmsh3D Prism`):** Prüft an einer kleinen Test-STL (inkl. spitzer Kante), dass Schritt 1 + Schritt 2 echte Prismen ohne Pyramiden erzeugen.

### AP 1.6: Projekt-Umbenennung (nach Namenswahl) & komplette Englisch-Umstellung
- [ ] **Namensentscheidung finalisieren** und Solution/Namespaces/Ordner/`sim.ps1 -RemoteDir` sauber umbenennen (erst wenn Name feststeht).
- [ ] **Code, Logs & Fehlermeldungen auf Englisch:**
  - Alle Konsolenausgaben, Log-Meldungen, CSV-Spaltennamen (`Iteration`, `Variant`, `MeshCells`, `Duration_s`, `Bayes_History.csv` etc.), Fehlertexte und Code-Kommentare auf klares technisches Englisch umstellen.
  - Zentrale Meldungskonstanten/Ressourcen nutzen, damit Tests gegen strukturierte Fehlercodes/Keys statt gegen fragile deutsche Sätze prüfen.
  - Alle ~800 Unit-Tests sauber auf die englischen Texte/Codes anpassen (`dotnet test` 100 % grün, 0 Warnungen).
- [ ] **READMEs & Handbücher auf Englisch umstellen** (schlanke Struktur vorbereiten — das eigentliche Kürzen der 88-KB-Handbuchlast passiert direkt Hand in Hand mit der Konfigurations-Diät in Phase 2).
- [ ] **Hinweis:** `.plans/` bleibt ausdrücklich **auf Deutsch**!

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

### Nächste Schritte (Etappe 2 & 3)
- **Nächster empfohlener Commit-Schritt:** Etappe 1 jetzt sauber committen, dann in einem frischen `/delegate`-Lauf **Etappe 2 (`AP 1.3` God-Classes zerlegen + `AP 1.4` alte Optimierer bereinigen + `AP 1.5` echte Integrations-Tests)** umsetzen.
- **Vor Start von Etappe 3 (`AP 1.6`):** Unbedingt mit Leonardo den finalen englischen Projektnamen klären! Das alte 88-KB-`BEDIENHANDBUCH.md` in `AP 1.6` bewusst **nicht** mehr komplett übersetzen, sondern in Phase 2 (`AP 2.1`) direkt durch modulare englische `docs/` ersetzen.
- Beim Refactoring von `BayesOptimizationAlgorithm`, `GmshCfdMesher` und `Su2Solver` zuerst Charakterisierungs-Tests prüfen, danach zerlegen (keine stillen Verhaltensänderungen).
