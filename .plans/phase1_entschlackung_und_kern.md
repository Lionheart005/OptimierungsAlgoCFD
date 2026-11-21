# Phase 1: Entschlackung, Bugfixes, Architektur-Sanierung, Tests & Englisch-Basis

> **Status:** Geplant (Startpunkt der großen Überarbeitung v2)  
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
- [ ] **GP-Unsicherheitsband korrigieren (`Core/Algorithms/BayesOptimizationAlgorithm.cs:1147-1185`):** In `PredictFitnessBand` das gelernte Beobachtungsrauschen `sn2` (plus `extraNoise`) zur prädiktiven Varianz addieren (behebt die systematische 30-%-statt-95-%-Bandabdeckung).
- [ ] **GCI-Berechnung ehrlich machen (`Core/Pipeline/CalibrationEvaluator.cs:82-131`):** Bei nicht-asymptotischer Verfeinerung (`ratio <= 1`, divergierende Differenzen oder Vorzeichenwechsel) explizit `IsAsymptotic = false` / ungültigen GCI melden statt `Math.Abs(Log(ratio))` + Clamp auf `p = 0.50` mit unphysikalischer `CD_ext`.
- [ ] **Geometrie-Messreihenfolge korrigieren (`src/projects/MantaAuv/MantaGeometryGenerator.cs:82-107` & Vorlagen):** `Volume` und `FrontalArea` (`REF_AREA`) erst **nach** `StlSmoother.SmoothStl` auf dem tatsächlich an Gmsh/SU2 übergebenen Mesh messen (Konsistenz zwischen Geometrie-Metrik und CFD!).
- [ ] **Zellzahl-Schutz auch in der Optimierung (`Core/Pipeline/WorkflowController.cs:434`):** `CellLimit` (`MaxCells3D`) nicht nur in `MeshCalibration`, sondern auch während der Optimierung prüfen/absichern (mindestens konfigurierbare harte Sicherheitsgrenze + klare Warnung, um unkontrollierte 4,2M-Zellen-Explosionen abzufangen).
- [ ] **Gmsh Schritt-1-Winkel-Retry implementieren (`Solvers/Cfd/GmshCfdMesher.cs`):** Den noch offenen Punkt aus `.plans/.old/wandschicht_zellzahl.md` umsetzen — bei `Wrong topology of boundary mesh for parametrization` (wie `Gen1_Var5` bei spitzem Heck) automatischer Retry mit angepasstem `ClassifyAngleDeg` (z. B. `40° → 30° → 20°`).
- [ ] **2D-Vorkalibrierung Config-Bug fixen (`Program.cs:517`):** `solvers/gmsh.json` des Projekts laden statt `new Gmsh2DMesherOptions()` hart zu instanziieren.
- [ ] **Deploy- & Repo-Hygiene bereinigen:**
  - `src/projects/MantaAuv/OptimierungsAlgoTestlaufBericht.pdf` (2,9 MB) aus `src/projects/` entfernen (nicht mehr bei jedem `sim.ps1`-Deploy hochladen; ggf. nach `docs/archive/` oder außerhalb des Deploy-Baums legen, und `.pdf` in `Get-DeployFiles` explizit ausschließen).
  - Benutzerspezifische absolute Pfade (`/home/lpleissner/...`) aus `MantaAuv/simulation.json` und `Naca2D/simulation.json` entfernen (gehören ausschließlich in gitignorierte `*.local.json` oder werden durch `ExecutableResolver` / `sim-runner.sh` gefunden).
  - Offene Aufräum-Punkte aus v1: alte Git-Branches (`WindschiffProjekt`, `MajorPhysikFixes`, `KI_Integration`) nach Prüfung bereinigen; auf `BIC_12` veralteten Ordner `src/Automatisierung_v2` aufräumen.
  - `RunTimeEstimator` & `sim.ps1 status`-Prognose (aus `.old/zeitschaetzung.md`) einmalig auf `BIC_12` mit dem nächsten Test-Deploy verifizieren.

### AP 1.2: `Core/`-Schichtenisolation reparieren & per Architektur-Test erzwingen
- [ ] **`MeshCalibration.cs:11` entkoppeln:** Direkten Import `using OptimierungsAlgoCFD.Solvers.Cfd` sowie das hartkodierte Lesen von `solvers/gmsh.json`/`su2.json` durch ein sauberes Interface (z. B. `ISolverCalibrationAdapter` / `IStageConfigFingerprintProvider`) ersetzen.
- [ ] **`VoxelResolutionAdvisor.cs:5` entkoppeln:** Direkten Cast `stage.Mesher is GmshCfdMesher` entfernen; relevante Ziel-Oberflächenzellgröße über ein allgemeines Interface (`IMeshResolutionTarget`) abfragen.
- [ ] **SU2-/CFD-Leaks aus `Core/` beseitigen:**
  - `WorkflowController.cs:409-424`: Mesher meldet seine Zellzahl + Zellklassen selbst über `IMeshMetricsSource` (auch in `Gmsh2DMesher` implementieren!), statt im Controller `MeshMetrics.ReadSu2CellCount` direkt aufzurufen.
  - `ModelRecord.cs`: `StlPath` zu allgemeinem `GeometryPath` (oder `GeometryArtifact`) verallgemeinern; `Artifacts`-Dictionary (`Dictionary<string, string>`) als saubere Dateischnittstelle zwischen Stufen einführen (bereitet auch OpenFOAM & späteres FEM vor!).
  - `Kernels/Planar2D/BBox2.cs:4`: Namespace-Fehlplatzierung sauber lösen (entweder geometrisches `Bounds2D` nach `Core/Models/` oder sauber trennen).
  - Solver-/Geometry-spezifische Utilities (`MeshMetrics` `.su2`-Parser, `StlWriter`, `StlSmoother`, `RubberBandScaler`) sauber in `Kernels/` bzw. `Solvers/` einordnen.
- [ ] **Architektur-Test hinzufügen (`ArchitectureBoundaryTests.cs`):** Automatischer Test, der prüft, dass keine Datei unter `Core/` ein `using` auf `Solvers`, `Kernels`, `Composition` oder `Projects` enthält.

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

## 5. Handover-Hinweis für Umsetzungs-Agenten (`/delegate`)
- Phase 1 eignet sich ideal für eine Aufteilung in kleine, voneinander unabhängige PRs/Pakete (`AP 1.1` → `AP 1.2` → `AP 1.3` → `AP 1.4/1.5` → `AP 1.6`).
- Vor Start von `AP 1.6` unbedingt mit Leonardo den finalen englischen Projektnamen klären!
- Beim Refactoring von `BayesOptimizationAlgorithm`, `GmshCfdMesher` und `Su2Solver` zuerst Charakterisierungs-Tests prüfen, danach zerlegen (keine stillen Verhaltensänderungen außer den explizit gelisteten Bugfixes in `AP 1.1`).
