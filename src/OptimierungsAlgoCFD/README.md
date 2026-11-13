# Architektur- & Entwicklerhandbuch (`src/OptimierungsAlgoCFD/`)

> **Weiterführende Dokumente (ohne Doppelung):**
> - **Bedienung & Alle JSON-Einstellungen (ohne Programmierkenntnisse):** [`src/projects/BEDIENHANDBUCH.md`](../projects/BEDIENHANDBUCH.md)
> - **Neues Bauteil / Projekt anlegen (3D & 2D):** [`src/projects/README.md`](../projects/README.md)
> - **3D-Geometrie-Handbuch (PicoGK):** [`src/OptimierungsAlgoCFD/Kernels/PicoGk/README.md`](Kernels/PicoGk/README.md)
> - **2D-Geometrie-Handbuch (`Planar2D`-Kernel):** [`src/OptimierungsAlgoCFD/Kernels/Planar2D/README.md`](Kernels/Planar2D/README.md)
> - **Linux-Server-Steuerung, Deployment & Einrichtung:** [`scripts/README.md`](../../scripts/README.md)
> - **Lokale Ausnahmekonfiguration (`*.local.json`):** [`config/README.md`](../../config/README.md)

Dieses Dokument richtet sich an Entwickler und beschreibt die Software-Architektur des C#-Frameworks, die Schnittstellenverträge, das Datenmodell, die interne Kopplung von PicoGK, Gmsh und SU2 sowie das Testsystem.

---

## 1. Repository-Struktur & Schichtentrennung

Das Repository trennt das wiederverwendbare Framework strikt von den einzelnen Bauteilprojekten. Der Kern (`Core`) kennt weder ein konkretes Projekt noch einen konkreten Solver oder Geometrie-Kernel — diese Trennung wird vom Compiler über die Namespaces erzwungen (`Core/` besitzt kein einziges `using` auf `Projects`, `Solvers` oder `Kernels`).

```text
OptimierungsAlgoCFD/
├── src/
│   ├── OptimierungsAlgoCFD/            Das Framework — wird für ein neues Projekt NIE angefasst
│   │   ├── Program.cs                 OptimierungsAlgoCFD                  Composition Root & CLI
│   │   ├── Composition/               OptimierungsAlgoCFD.Composition      CfdProjectDefinition (3D) & Cfd2DProjectDefinition (2D)
│   │   ├── Core/                      OptimierungsAlgoCFD.Core             Reiner Domänenkern
│   │   │   ├── Interfaces/            Die neun zentralen Schnittstellenverträge
│   │   │   ├── Models/                ModelRecord, GeometryResult, SimulationContext, SolverStage
│   │   │   ├── Configuration/         JsonConfigLoader, ConfigSchema, ProjectRegistry, ProjectPaths
│   │   │   ├── Pipeline/              WorkflowController, ChampionValidator, MeshCalibrationService,
│   │   │   │                          ChampionVerification, RunJournal, RunInfoWriter
│   │   │   ├── Algorithms/            EvolutionaryAlgorithm, RsmOptimizationAlgorithm, Factory
│   │   │   └── Utilities/             RubberBandScaler, StlSmoother, StlWriter, MeshMetrics,
│   │   │                              Shape2D, ExecutableResolver
│   │   ├── Kernels/PicoGk/            OptimierungsAlgoCFD.Kernels.PicoGk   PicoGkKernel, PicoGkMeshMetrics
│   │   └── Solvers/Cfd/               OptimierungsAlgoCFD.Solvers.Cfd      GmshCfdMesher, Gmsh2DMesher,
│   │                                                                   Su2Solver, Su2HistoryAnalyzer,
│   │                                                                   BoundaryLayerCalculator, YPlusAnalyzer
│   └── projects/                      Alle Bauteilprojekte (je ein Ordner = ein Projektname)
│       ├── _Vorlage3D/                  OptimierungsAlgoCFD.Projects.MeinProjekt    (3D-Kopiervorlage)
│       ├── _Vorlage2D/                OptimierungsAlgoCFD.Projects.MeinProjekt2D  (2D-Kopiervorlage)
│       ├── MantaAuv/                  OptimierungsAlgoCFD.Projects.MantaAuv       (3D-AUV-Rumpf)
│       └── Naca2D/                    OptimierungsAlgoCFD.Projects.Naca2D         (2D-NACA-Profil)
├── scripts/                           PowerShell-, Bash- & Python-Skripte (Deploy, Server, Plots)
├── tests/                             780+ Unit- und Architekturtests (laufen ohne externe Solver)
├── config/                            Notausgang für maschinenspezifische *.local.json
└── Ergebnisse/                        Projektbezogene Simulationsergebnisse (Ergebnisse/<Projekt>/)
```

### Warum `src/projects/` neben dem Framework liegt
„Hier das Gerüst, dort meine Projekte" ist im Dateibaum sofort sichtbar. Technisch bindet `src/OptimierungsAlgoCFD/OptimierungsAlgoCFD.csproj` die Projekte über `..\projects\**\*.cs` ein, schließt dabei aber **`_Vorlage3D/` und `_Vorlage2D/`** aus dem Programm-Build aus: Sonst gäbe es Projektdefinitionen mit den Platzhalternamen `MeinProjekt` bzw. `MeinProjekt2D`, zu denen kein gleichnamiger Ordner existiert. Compiliert werden beide Vorlagen stattdessen vom Testprojekt (`tests/OptimierungsAlgoCFD.Tests/`) — ändert sich ein Interface im Framework, bricht sofort `dotnet test`.

### Kapselung von PicoGK
`using PicoGK;` existiert im gesamten Framework nur an zwei Stellen (`Kernels/PicoGk/PicoGkKernel.cs` und `Kernels/PicoGk/PicoGkMeshMetrics.cs`) sowie in den 3D-Geometriegeneratoren unter `src/projects/`. Reine 2D-Projekte (wie `Naca2D`) oder analytische 3D-Projekte kommen komplett ohne PicoGK und ohne Xvfb/OpenGL aus: Sie verwenden `DirectGeometryKernel`.

---

## 2. Lokale Entwicklungsumgebung (PC-Grundausstattung)

Für die Entwicklung am Framework oder an Bauteilgeometrien unter Windows genügt eine schlanke Grundausstattung:

1. **.NET SDK 9.0** (oder neuer) installieren.
2. **Visual Studio Code** mit der Erweiterung **„C# Dev Kit"** installieren.
3. **Bauen & Testen (ohne installierte CFD-Solver):**
   ```powershell
   dotnet build OptimierungsAlgoCFD.sln
   dotnet test OptimierungsAlgoCFD.sln
   ```
4. **Standalone-Geometrie mit PicoGK exportieren (Grundprinzip):**
   Innerhalb eines 3D-Geometriegenerators erzeugt PicoGK aus einem `Voxels`-Volumen ein Oberflächennetz (`Mesh`) und speichert es als STL:
   ```csharp
   Mesh exportMesh = new Mesh(finalVoxels);
   exportMesh.SaveToStlFile(stlFilePath);
   ```
   Neue `.cs`-Dateien im Projekt müssen nirgends manuell registriert werden — solange der Namespace stimmt, nimmt das SDK sie automatisch in den Build auf.

---

## 3. Die Schnittstellen (`Core/Interfaces/`)

| Interface | Aufgabe | Implementierungen |
| --- | --- | --- |
| `IProjectDefinition` | Projekt verdrahten: Name, Code-Defaults, Kernel, Geometrie, Fitness, Solver-Stufen | `MantaProject` (über `CfdProjectDefinition`), `Naca2DProject` (über `Cfd2DProjectDefinition`) |
| `IGeometryGenerator` | Aktive Parameter → STL-Export (3D) bzw. `Shape2D`-Kontur (2D) + benannte Metriken | `MantaGeometryGenerator`, `Naca2DGeometryGenerator` |
| `IGeometryKernel` | Laufzeitumgebung für die Geometrieerzeugung bereitstellen | `PicoGkKernel` (startet `Library.Go(...)`), `DirectGeometryKernel` (direkter Aufruf ohne OpenGL/PicoGK) |
| `IMeshGenerator` | Geometrie → Rechennetz (`.su2`) + Netzmetriken (`MeshCells`, `BoundaryLayerHexShare`). Optional zusätzlich `IMeshMetricsSource`: Wandschicht-Kennwerte (`BoundaryLayerLayers`, `BoundaryLayerThicknessMm`, `BoundaryLayerStage` …), die der `WorkflowController` in die `PassiveParameters` übernimmt. Scheitert die Vernetzung, wirft der Mesher `MeshGenerationException`. | `GmshCfdMesher` (3D), `Gmsh2DMesher` (2D) |
| `ISimulationSolver` | Rechennetz → Strömungslösung & Messwerte in `record.PassiveParameters` | `Su2Solver` |
| `IFitnessCalculator` | Messwerte in `ModelRecord` → skalare `Fitness` (größer = besser) | `MantaFitnessCalculator`, `Naca2DFitnessCalculator` |
| `IOptimizationAlgorithm` | Aus bisheriger Historie die nächste Parametergeneration vorschlagen | `BayesOptimizationAlgorithm` (Standard), `RsmOptimizationAlgorithm`, `EvolutionaryAlgorithm` |
| `IModelValidator` | Generationssieger mit Parameter-Jitter gegenprüfen („Türsteher" / Bouncer) | `ChampionValidator` |
| `IHasRepresentativeSection` | Optional für 3D-Projekte: liefert repräsentativen 2D-Profilschnitt (`Shape2D`) für schnelle 2D-Netz-Vorkalibrierung | (derzeit kein Projekt; im Test mit Stub abgedeckt) |

### `IProjectDefinition` und `Composition/`
`IProjectDefinition` hält `Program.cs` komplett frei von Projektwissen:

```csharp
string Name { get; }                                  // = Ordnername unter src/projects/
ProjectConfig      CreateDefaults();                  // Code-Basiswerte; project.json überlagert sie
IGeometryKernel    CreateKernel();
IGeometryGenerator CreateGeometry(ProjectContext c);
IFitnessCalculator CreateFitness(ProjectContext c);
SolverStage[]      CreateStages(ProjectContext c);
```

`ProjectContext` bündelt alles, was ein Projekt beim Verdrahten braucht: Projektname, Projektordner, die geladene `SimulationConfig` und `ProjectConfig` sowie die generische Methode `LoadSolverOptions<T>("su2")`, die bereits auf den richtigen Projektordner zeigt.

In `Composition/` liegen zwei vorgefertigte Basisklassen, von denen Projekte erben können:
- **`CfdProjectDefinition`**: Verdrahtet die 3D-Kette `PicoGkKernel` → `GmshCfdMesher` → `Su2Solver`.
- **`Cfd2DProjectDefinition`**: Verdrahtet die 2D-Kette `DirectGeometryKernel` → `Gmsh2DMesher` → `Su2Solver` (`Dimensions = 2`).

---

## 4. Ablaufsteuerung & Pipeline (`Core/Pipeline/`)

[`WorkflowController.RunOptimization()`](Core/Pipeline/WorkflowController.cs) steuert den Gesamtlauf. Jede einzelne Variante durchläuft `RunPipeline(parameters, iteration, variant)`:

```text
RubberBandScaler.Create(...)          Aktive Längenparameter auf TargetPicoGkSize skalieren
  -> _geometry.GenerateAndExport(...) Erzeugt STL / Shape2D und meldet GeometryResult mit Metriken
  -> ApplyGeometryMetrics(...)        Skaliert Metriken gemäß MetricScaling (Linear/Area/Volume) zurück
  -> für jede SolverStage:
       Mesher.GenerateMesh(...)       Erzeugt Netz (Mesh-Cache: gleiche Mesher-Instanz = 1 Netz)
       Solver.Solve(...)              Schreibt Beiwerte & Diagnosegrößen in record.PassiveParameters
```

### Fehlerbehandlung und Pflicht-Metriken
Bricht die Vernetzung oder der Solver ab, wird lediglich `record.SimulationFailed = true` gesetzt — der Gesamtlauf läuft weiter. Der Kern kennt dabei **keine Metriknamen**; wie ein fehlgeschlagenes Modell bewertet wird (z. B. `Fitness = 0.0001f`), entscheidet allein der `IFitnessCalculator` des Projekts.

Nach der **allerersten gelungenen Variante** prüft der Controller einmalig, ob alle vom Fitness-Rechner in `RequiredMetrics` angeforderten Größen tatsächlich in `record.PassiveParameters` vorhanden sind. Fehlt eine Größe (z. B. weil `Lift` in der Fitnessformel genutzt, aber in `solvers/su2.json` unter `ResultMetrics` vergessen wurde), bricht der Lauf sofort mit einer klaren Fehlermeldung ab, statt stundenlang mit einer Ersatz-Fitness weiterzurechnen.

### Türsteher (`ChampionValidator`)
_Schwankende Rechnungen:_ Türsteher, Feinverifikation und Netzkalibrierung vergleichen mit der eingestellten Toleranz, mindestens aber mit der doppelten kombinierten Unsicherheit beider Werte (`MetricTolerance.WithUncertainty`, Schlüssel `Uncertainty_<Kennwert>`). Ruhig auskonvergierte Rechnungen (Unsicherheit nahe 0) ändert das nicht; bei Wirbelablösung zählt reines Rauschen nicht als Instabilität oder Netzabhängigkeit. Eine Stufe der Kalibrierung mit „instationär, aber stabil" (`Su2_Unsteady`) gilt als konvergiert.

Am Ende jeder Iteration wird der beste Kandidat mit einem leicht verjitterten Parameter (`BouncerJitter`, relativ zur Spannweite `ParameterBounds` und hart auf `[Min, Max]` geklemmt) unter der Variantennummer `99` nachgerechnet. Weicht die **Fitness** um mehr als `BouncerTolerance` ab, gilt der Kandidat als instabiler Zufallstreffer und der Nächstbeste der Runde wird geprüft.

### Netzkalibrierung (`MeshCalibrationService`) & Feinverifikation (`ChampionVerification`)
- **Netzkalibrierung (`--calibrate` oder `MeshCalibration.Mode = "Auto"`):** Rechnet die Startgeometrie auf sukzessive feineren Netzen (Faktor $\div \sqrt{2}$ je Stufe) mit dem strengen `ControlRuns`-Profil durch, berechnet den **Grid Convergence Index (GCI)** nach Celik et al. (2008) und bestimmt die gröbste Netzstufe, auf der alle `ResultMetrics` innerhalb der kombinierten relativen und absoluten Toleranz (`Tolerance` + `AbsoluteTolerances`) zur feinsten Referenzstufe liegen. Unterstützt optional eine schnelle 2D-Vorkalibrierung (`IHasRepresentativeSection`), bevor 3D bestätigt wird.
- **Champion-Verifikation & Recheck (`--recheck`):** Rechnet den Gesamtsieger (`"Final"`), die Top-N (`"FinalTopN"`) oder jeden Rundensieger (`"EachIteration"`) auf einem um `RefinementSteps` Stufen feineren Netz nach (`Verifikation.csv`). Beim Recheck mit `--unsteady` wird zuerst stationär auf dem feinen Netz gerechnet und anschließend aus den erzeugten Restart-Dateien (`restart_00000.dat` / `restart_00001.dat`) eine zeitgenaue URANS-Simulation (Dual-Time-Stepping 2. Ordnung über 20 Perioden) gestartet.

### Bayes-Optimierer und Ersatzmodelle (`Core/Algorithms/BayesOptimizationAlgorithm.cs`, `Core/Surrogates/`)
`"OptimizationAlgorithm": "Bayes"` lernt in jedem Lauf neu (kein Vortraining, keine Python- oder ML-Bibliothek, nur CPU) einen **Gauß-Prozess** aus den bisherigen Rechnungen und wählt die nächsten Varianten per **Expected Improvement**: Die Auswahl fragt, wo die Chance am größten ist, den bisher Besten zu schlagen, und wägt Verfeinern (nahe am Besten) und Erkunden (im „Nebel", wo die Unsicherheit groß ist) rechnerisch gegeneinander ab. Jeder Vorschlag wird echt gerechnet; das Modell ersetzt keine Rechnung.

- **Bausteine in `Core/Surrogates/`:** `ISurrogateModel` (Schnittstelle: `Fit`, `Predict`, `AddPseudoObservation`, `ParameterRelevance`; der Optimierer kennt nur sie, ein anderes Modell lässt sich einstecken), `GaussianProcess` (Matérn-5/2-Kern mit eigener Längenskala je Parameter, gelerntes Rauschen, Hyperparameter über die Log-Marginal-Likelihood mit analytischem Gradienten und Adam), `DenseCholesky` (Zerlegung mit Jitter-Leiter, kein NuGet-Paket), `LatinHypercube`, `NormalDistribution`, `ParameterSpace` (Normierung auf [0, 1], feste Parameter), `SurrogateTrainingSet` (**die eine Regel für Trainingsdaten**) und `TrustRegionState`.
- **Trainingsdaten-Regel:** nicht fehlgeschlagen (`SimulationFailed`), nicht vom Türsteher verworfen (`ModelRecord.Disqualified`), endliche Fitness, keine Kontrollrechnung (Variante 99) und dieselbe Netzstufe (`MeshRefinementFactor`) wie der Lauf. Fehlgeschlagene und verworfene Orte stoßen die Suche in ihrer Umgebung ab. **Vernetzungsfehler** (`MeshFailed` = 1, `MeshGenerationException`) und **Ersatznetze** (`MeshFallback` = 1) zählen nicht als Strömungsabsturz: sie stoßen nur lokal ab und speisen das Erfolgsmodell nicht, damit eine Form, an der nur der Vernetzer scheiterte, nicht die ganze Zone sperrt.
- **Modi (`Bayes.SurrogateTarget`):** `"Fitness"` lernt die Note als Ganzes. `"Metrics"` lernt **je Kennwert aus `IFitnessCalculator.RequiredMetrics`** ein Modell und lässt die Fitness-Formel des Projekts mit Wegwerf-Datensätzen darüber rechnen (Monte-Carlo-EI). Der Kern kennt weder Formel noch Metriknamen. Eine Gegenprobe vergleicht die Formel mit der echten Fitness; liest sie mehr als deklariert, fällt der Lauf mit Warnung auf `"Fitness"` zurück. `"Auto"` wählt `"Metrics"`, wenn `RequiredMetrics` nicht leer ist.
- **Viele Parameter, lange Läufe:** Ab 10 freien Parametern sucht das Modell in einem Vertrauensbereich (TuRBO-Stil) um den besten Punkt; der Würfel wächst nach 3 Erfolgen und halbiert sich nach `⌈max(4, d) / V⌉` Misserfolgen, bei Kollaps beginnt ein Neustart. Ein Deckel (400 Punkte) begrenzt die Lernmenge, weil die Rechenzeit mit n³ wächst. Diese inneren Stellschrauben stehen in `BayesTuning` und sind bewusst nicht in `simulation.json`; einstellbar sind nur `Bayes.SurrogateTarget` und `Bayes.InitialSamples`. Der **Erkunder** (letzte Variante einer Runde ab 4 Varianten) sucht weiter im ganzen Raum.
- **Auswahl, wenn nichts mehr zu verbessern ist (08.11.2026):** Gilt die erwartete Verbesserung überall als vernachlässigbar (`< 0,2 %` bei logarithmischer Fitness; typisch, wenn der bisher Beste ein Ausreißer nach oben ist oder das Optimum erreicht wurde), **verfeinert** `PickByExpectedImprovement` bei der höchsten *vorhergesagten* Fitness (Abstoßung gegen Dubletten bleibt). Vorher wich die Regel bei „exakt null" auf die größte Unsicherheit im ganzen Raum aus und verbrauchte im Serverlauf (10 × 10, Naca2D) ganze Runden an den Rändern des Suchraums; auch winzige Restwerte der erwarteten Verbesserung, die der Quotient Lift/Drag mit unabhängigen Einzelmodellen im Strömungsabriss hat, lenkten dorthin. Der Erkunder wählt jetzt die größte Unsicherheit nur unter Punkten, deren optimistische Schätzung (bester von 8 Stichproben bzw. `μ + 2,4σ`) mindestens die halbe beste Fitness erreicht. Regressionstests: `BayesRealDataReplayTests` (echte Serverdaten, `BayesRealDataFixtures`) und `BayesConvergedPhaseTests`.
- **Abstürze und unsichere Ergebnisse (Paket B, 08.11.2026):** Echte Abstürze (`SimulationFailed`) lernt ein eigener Gauß-Prozess auf 1 = gelungen / 0 = abgestürzt; seine Vorhersage (Erfolgswahrscheinlichkeit) dämpft die Auswahlregel, so dass ganze Absturzzonen gemieden werden. Vom Türsteher verworfene Varianten stoßen nur lokal ab. Schreibt der Solver `Uncertainty_<Kennwert>` (relative 1σ-Unsicherheit, Konvention in `Core/Models/MetricUncertainty.cs`), geht sie als Rauschen je Punkt in den Gauß-Prozess ein. `Su2Solver` unterscheidet dafür drei Zustände: konvergiert, instationär-stabil (Blockmittel ohne gesicherten Trend, `Su2_Unsteady = 1`, Fenstermittel zählt) und echte Drift (nur diese kann mit `TreatUnconvergedAsFailed` als Fehlschlag gelten).
- **Transparenz:** Spalten `Bayes_PredictedFitness/Low/High/Explorer` in `Simulation_Results.csv`, `[BAYES]`-Logzeilen (Treffsicherheit der letzten Runde, Parameter-Wichtigkeit) und `Analyseergebnisse/Bayes_Verlauf.csv`.
- **Eigenschaften, auf die man sich verlassen kann:** Das Modell bricht nie den Lauf ab (bei numerischem Scheitern wird die Runde mit Latin-Hypercube-Punkten gefüllt). Mit gleichem Seed und gleichen Ergebnissen macht der Optimierer bitgleich dieselben Vorschläge, damit die Wiederaufnahme (`RunJournal`) greift; getestet in `ReproducibilityAndResumeTests`. `EvaluateAndSelectBest` setzt nur Fitness und Vorhersagespalten, der Zustand ändert sich allein in `PrepareNextIteration`. Voraussetzungen (Parametergrenzen) prüft `IOptimizationPreflight.Preflight` **vor** der Netzkalibrierung.
- **Grenzen:** Ganzzahlige Parameter werden stetig behandelt (der Generator rundet). `MaxDeviations` wirkt nicht. Das Verfahren ist für etwa 5 bis 20 Parameter gedacht. Bei einer Formel mit Sprüngen und ohne deklarierte `RequiredMetrics` hat der Modus `"Fitness"` keinen Vorteil gegenüber `"Rsm"`; im synthetischen Manta-Test (`BayesMetricsModeTests`) war `"Metrics"` dagegen rund 35 % besser als beide.
- **Andockstelle für fremde Modelle (z. B. ein PINN eines Partners):** entweder als weiterer, schneller Solver (`ISimulationSolver` in `Solvers/Ml/`, Austausch über Dateien) oder als `ISurrogateModel`, das als Vorwissen für den Gauß-Prozess dient.

### Reproduzierbarkeit, Wiederaufnahme & Voxel-Prüfung (`RunJournal`, `RunInfoWriter`, `VoxelResolutionAdvisor`)
Jeder Lauf protokolliert seinen Zufalls-Seed (`RandomSeed`), Git-Commit/Deploy-Hash, Werkzeugversionen und den Quellcode-Snapshot (`Quellstand/`) im Ergebnisordner. Beim Start prüft `VoxelResolutionAdvisor`, ob `BaseVoxelResolution` im Realraum fein genug für die Oberflächen-Zielzellgröße ist, warnt bei groben Voxeln oder passt die effektive Start-Voxelgröße bei `AutoVoxelResolution = true` automatisch nach unten an (ohne den `--resume`-Fingerprint zu verändern). Nach jeder abgeschlossenen Variante wird ein Eintrag in `Wiederaufnahme/journal.jsonl` geflusht; bei `--resume` startet der Lauf mit demselben Seed von vorn, spielt bereits berechnete Varianten samt ihrer `DurationSeconds` in Sekunden aus dem Journal ab und rechnet nahtlos ab der Abbruchstelle weiter (sofern sich keine Konfigurationsdatei geändert hat).

---

## 5. Datenmodell & Geometrie-Utilities (`Core/Models/`, `Core/Utilities/`)

- **`ModelRecord`**: Repräsentiert eine Variante und trennt strikt:
  - `ActiveParameters`: Vom Optimierer gesetzte Entwurfsparameter (z. B. `Length`, `Width`, optional auch `AngleOfAttackDeg`).
  - `PassiveParameters`: Gemessene Größen aus Geometrie (`Volume`, `FrontalArea`, …), Vernetzer (`MeshCells`, `MeshCells_Tri`, `MeshCells_Quad`, `MeshCells_Prism`, `MeshCells_Hexa`, `MeshCells_Tet`, `MeshCells_Pyramid`, `BoundaryLayerHexShare`, `BoundaryLayerLayers`, `BoundaryLayerThicknessMm`, `BoundaryLayerFirstCellMm`, `BoundaryLayerStage`, `BoundaryLayerMaxTiltDeg`, `BoundaryLayerMinScale`, `BoundaryLayerThinnedShare`, `TunnelBlockagePercent`, `TunnelLengthMm`, `TunnelWidthMm`, `TunnelHeightMm`, `TunnelUpstreamL`, `TunnelDownstreamL`, `MeshFallback`, `MeshFailed`, `CellLimitExceeded`) und Solver (`Drag`, `Lift`, `YPlus_P95`, `Su2_CD_p`, `Su2_CD_v`, `Su2_Iterations`, `Su2_Converged`, `Su2_Osc_*`, `Su2_IterErr_*`).
  - `Fitness`, `SimulationFailed`, `StlPath`, `MeshPath`, `FineVerified`, `FineDeviation`.
- **`GeometryResult` & `MetricScaling`**: Der Geometriegenerator meldet jede Metrik zusammen mit ihrer physikalischen Dimension:
  ```csharp
  result.AddMetric("Volume", volume, MetricScaling.Volume);       // skaliert kubisch zurück (s³)
  result.AddMetric("FrontalArea", area, MetricScaling.Area);      // skaliert quadratisch zurück (s²)
  result.AddMetric("Span", span, MetricScaling.Linear);           // skaliert linear zurück (s¹)
  result.AddMetric("Ratio", ratio, MetricScaling.None);           // dimensionslos (unverändert)
  ```
  Welche Dimension jede Metrik erhalten hat, wird in `effective-config.json` dokumentiert.
- **`MeshMetrics` & `PicoGkMeshMetrics`**: Berechnet Volumen, Oberfläche und Bounding-Box einer Dreiecksliste in einem einzigen Durchgang (PicoGKs eigenes `CalculateProperties` liefert keine Oberfläche). Das Volumen ist vorzeichenbehaftet abfragbar: Ein negatives Volumen zeigt sofort an, dass Dreiecksnormalen nach innen zeigen.
- **`SimulationContext`**: Trennt unveränderliche Vorgaben (`Config`, `Project`) strikt vom veränderlichen Laufzeitzustand (`CurrentBaseParameters`, `CurrentDeviations`, `History`). Algorithmen überschreiben niemals `ProjectConfig` direkt; `ResetRuntimeState()` stellt den Ausgangszustand jederzeit wieder her.
- **`SolverStage`**: Ein Paar aus `IMeshGenerator` und `ISimulationSolver`. Der Controller arbeitet ein `SolverStage[]` sequenziell ab. Teilen sich zwei Stufen **dieselbe Mesher-Instanz** (Referenzgleichheit), wird nur einmal vernetzt. So lässt sich z. B. eine FEM-Stufe mit eigenem Netz neben die CFD-Stufe hängen.
- **`ExecutableResolver`**: Löst Programmpfade (`mpirun`, `SU2_CFD`, `gmsh`) plattformunabhängig auf — fällt ein absoluter Serverpfad (z. B. `/home/.../gmsh`) auf einem anderen Rechner weg, sucht der Resolver automatisch über den Dateinamen im lokalen `PATH` und in Conda-/Standardverzeichnissen.

---

## 6. Solver- & Vernetzer-Anbindung im Detail (`Solvers/Cfd/`)

### 6.1 Vernetzung mit Gmsh (`GmshCfdMesher` für 3D, `Gmsh2DMesher` für 2D)
- **Einheitliche `scaleFactor`-Konsistenz & garantierter realer Windkanal (`TunnelGeometry` + `StlWriter`):** `GmshCfdMesher` skaliert alle konfigurierten mm-Zellgrößen und Kanalabstände einheitlich mit `scaleFactor` (sodass alle Maße in realen mm wirken) und erzeugt den 3D-Windkanal direkt im C#-Code als STL ohne PicoGK-Abhängigkeit. Unterschreitet die Tunnelbox die physikalischen Mindestabstände (`TunnelMinUpstreamFactor = 5.0`, `TunnelMinDownstreamFactor = 15.0`, `TunnelMinLateralFactor = 5.0`) oder überschreitet sie `TunnelMaxBlockagePercent = 0.3`, weitet der Mesher sie automatisch nach außen auf.
- **Zwei-Schritt-Vernetzung für 3D-Grenzschichten (`BoundaryLayerMode = "Prism"` oder `"Hexa"`):**
  1. `Wall_<Variante>.geo` parametrisiert die STL-Oberfläche über `ClassifySurfaces` neu und vernetzt sie sauber — inkl. krümmungsabhängiger Verfeinerung (`MeshSizeFromCurvature = true`, `CurvatureElements = 20`) mit Untergrenze (`CurvatureMinSizeFactor = 0.5`) gegen Zell-Explosionen an Voxel-Splittern.
  2. `Meshing_<Variante>.geo` extrudiert darauf die Grenzschicht (Standard in 3D: `TargetYPlus = 1.5`, `BoundaryLayerGrowthRate = 1.28`, erste Zellhöhe $y_1$ über `BoundaryLayerCalculator`), ergänzt die Nachlauf-Detailbox (mit Z-Mindestpuffer `DetailBoxMinHeightLengthFactor = 0.15` und `DetailBoxLengthFactor = 1.5`) und füllt das restliche Kanalvolumen mit Tetraedern. Für scharfe Voxel-Kanten ist `"Prism"` (Dreiecksprismen ohne Pyramiden-Übergangsschicht) die robuste Wahl. Schlägt die Schichtextrusion an engen Radien fehl, reduziert der Mesher automatisch stufenweise Richtung und Dicke der äußeren Schichten, wobei $y_1$ und Schichtzahl erhalten bleiben.
- **2D-Profilvernetzung (`Gmsh2DMesher`):** Erzeugt aus einer `Shape2D`-Kontur ein ebenes Netz mit großem kreisförmigem Fernfeld (`FarfieldRadiusFactor = 500`), strukturierter Quad-Grenzschicht (`BoundaryLayer`-Feld), Vorder-/Hinterkantenverfeinerung (`EdgeRefinementFactor`), `DetailBox` und Nachlauf-Box (`WakeRefinement`). An scharfen Hinterkanten verhindert ein Knickpunkt-Schutz (`FanPoints`), dass sich Grenzschichtzellen am Profilende selbst durchdringen.

### 6.2 Strömungssimulation mit SU2 (`Su2Solver`, `Su2HistoryAnalyzer` & `Su2SurfaceAnalyzer`)
- **Physikalisches Modell:** SU2 löst die inkompressiblen Reynolds-gemittelten Navier-Stokes-Gleichungen (`SOLVER= INC_RANS`) mit konstanter Dichte und Viskosität ohne Energiegleichung (`SA` oder `SST`), stationär (Euler implizit mit adaptiver CFL-Zahl) oder zeitgenau (`TIME_DOMAIN= YES`, `DUAL_TIME_STEPPING-2ND_ORDER`). Bei frühem Cauchy-Stopp (`CauchyTolerance = "1e-5"`) mit Rest-Drift setzt `AutoRestartOnDrift` (bis `MaxConvergenceRestarts = 3`) automatisch per `RESTART_SOL= YES` fort.
- **Randbedingungen (Kopplung an Gmsh über `Physical Surface` / `Physical Curve`):**
  - `MARKER_FAR = ( Farfield )`: Fernfeld-Randbedingung an der Kanal-Außenhülle (Anströmung in $+x$-Richtung bzw. gedreht um `AngleOfAttackDeg`).
  - `MARKER_HEATFLUX = ( Wall, 0.0 )`: Adiabate Haftbedingung ($\vec{v} = 0$) auf der Bauteiloberfläche.
  - `MARKER_MONITORING = ( Wall )` / `MARKER_PLOTTING = ( Wall )`: Integrationsfläche für alle Beiwerte ($C_D, C_L, C_{SF}, C_{Mx}, C_{My}, C_{Mz}$) und Ausgabefläche für `surface.vtu`.
- **Einheiten:** Das gesamte Framework rechnet in **Millimetern**; die Umrechnung der Referenzfläche (`ReferenceAreaMetric`, $\text{mm}^2 \to \text{m}^2$) und Koordinaten (`MESH_SCALE_CHANGE = 0.001`) für SU2 erfolgt automatisch im Solver.
- **Spaltenauswertung (`ResultMetrics`), Konvergenz & Oberflächenanalyse:**
  - `ResultMetrics` ordnet internen Metriknamen (z. B. `"Lift"`) die Spalten aus SU2s `history.csv` (z. B. `"CL"`) zu. Verglichen wird exakt nach Spaltenname (nur notfalls per Präfix mit deutlicher Warnung, damit `"CM"` nicht mehrdeutig auf `CMx`/`CMy`/`CMz` trifft).
  - Nach jedem Lauf archiviert der Solver `history.csv` und `current_config.cfg` unter `Analyseergebnisse/Verlaeufe/`. `Su2HistoryAnalyzer` prüft über das Endfenster die **Drift zwischen erster und zweiter Fensterhälfte** (`IterativeToleranceFraction`), berechnet die trendbereinigte Schwingungsamplitude (`Su2_Osc_<Metrik>`) und bildet je nach Einstellung (`Last`, `CauchyWindowMean`, `TailMean`) den Ergebniswert.
  - `YPlusAnalyzer` und `Su2SurfaceAnalyzer` werten die erzeugte `surface.vtu` aus und protokollieren neben `YPlus_Median`, `YPlus_P95` und `YPlus_Max` auch die Druck- und Reibungswiderstandsanteile `Su2_CD_p` und `Su2_CD_v` in `record.PassiveParameters`.

---

## 7. Konfigurationssystem unter der Haube (`Core/Configuration/`)

- **Vier-Schichten-Merge (`JsonConfigLoader`):** Führt Konfigurationen auf `JsonNode`-Ebene zusammen:
  1. Code-Defaults (`SimulationConfig.CreateDefault()`, `CreateDefaults()`, Solver-Options-Klassen)
  2. `src/projects/<Projekt>/….json` (eingecheckte Projektkonfiguration)
  3. `config/….local.json` (gitignorierter Notausgang für den Rechner)
  4. `src/projects/<Projekt>/….local.json` (gitignorierter Notausgang für Projekt + Rechner)
  Verschachtelte Objekte und Dictionaries (wie `ResultMetrics` oder `AbsoluteTolerances`) werden **additiv** gemerged; skalare Werte und Arrays werden überschrieben.
- **Tippfehler-Schutz (`ConfigSchema`):** Gleicht vor der Deserialisierung jeden JSON-Schlüssel gegen die C#-Properties des Zieltyps ab (ausgenommen freie Dictionary-Schlüssel wie Parameternamen unter `BaseParameters`). Ein unbekannter Schlüssel bricht sofort ab und schlägt per Levenshtein-Distanz den nächstähnlichen gültigen Namen vor.
- **`ProjectConfigDto`:** Da `System.Text.Json` keine ValueTuples `(float Min, float Max)` in `ProjectConfig.ParameterBounds` direkt aus `{ "Min": ..., "Max": ... }` deserialisiert, dient `ProjectConfigDto` als sauberes Transfer-Objekt.
- **Herkunftsnachweis (`EffectiveConfigWriter`):** Protokolliert für jeden einzelnen Schlüssel, aus welcher Schicht/Datei der wirksame Wert stammt, gibt beim Start den Warnblock für aktive `*.local.json`-Dateien aus und schreibt `Ergebnisse/<Projekt>/effective-config.json`.
- **Projekt-Auflösung (`ProjectPaths` & `ProjectRegistry`):**
  - `ProjectPaths` sucht `src/projects/` in der Reihenfolge: 2. CLI-Argument → Umgebungsvariable `SIM_PROJECTS_DIR` → vom Arbeitsverzeichnis bis zu 6 Ebenen aufwärts. Die JSON-Dateien nutzen bewusst **kein** `CopyToOutputDirectory` in der `.csproj`, damit Änderungen an einer JSON sofort ohne Rebuild wirken.
  - `ProjectRegistry` sucht beim Programmstart per Reflection alle `IProjectDefinition`-Klassen und gleicht sie **in beide Richtungen** gegen die Unterordner in `src/projects/` ab (Definition ohne Ordner oder Ordner ohne Definition führt sofort zu einer klaren Fehlermeldung).

---

## 8. Framework erweitern (How-To für Entwickler)

> **Neues Bauteil / Projekt anlegen (Geometrie, Zielfunktion, Parameter):**  
> Die vollständige Schritt-für-Schritt-Anleitung steht in **[`src/projects/README.md`](../projects/README.md)**. Dafür muss am Code unter `src/OptimierungsAlgoCFD/` keine einzige Zeile geändert werden.

### Neuen Solver oder Vernetzer anbinden
1. `IMeshGenerator` bzw. `ISimulationSolver` implementieren (z. B. unter `Solvers/Fem/`).
2. Eine eigene Options-Klasse anlegen (wird über `c.LoadSolverOptions<T>("calculix")` automatisch aus `solvers/calculix.json` über alle vier Konfigurationsschichten geladen).
3. Im Projekt `CreateStages(ProjectContext c)` überschreiben und die zusätzliche `SolverStage` anhängen:
   ```csharp
   public override SolverStage[] CreateStages(ProjectContext c) => new[]
   {
       new SolverStage(
           new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
           new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2"))),
       new SolverStage(femMesher, femSolver) // zweites Netz + zweiter Solver
   };
   ```

### Neues Optimierungsverfahren hinzufügen
1. `IOptimizationAlgorithm` unter `Core/Algorithms/` implementieren (Zufallsquelle `Random` für deterministische Tests über den Konstruktor injizierbar halten).
2. In [`OptimizationAlgorithmFactory.Create(...)`](Core/Algorithms/OptimizationAlgorithmFactory.cs) den neuen Namen registrieren.
3. Optional `IOptimizationPreflight` implementieren, wenn das Verfahren Voraussetzungen hat (z. B. vollständige `ParameterBounds`): `Preflight` läuft in `Program` vor der Netzkalibrierung und bricht mit einer klaren Meldung ab, bevor die erste teure Rechnung startet. Muss das Verfahren wiederaufnehmbar sein, muss es bei gleichem Seed und gleichen Ergebnissen dieselben Vorschläge machen (siehe `BayesOptimizationAlgorithm`).

---

## 9. Automatisierte Tests & Architektur-Sicherheitsnetze (`tests/`)

```powershell
dotnet test OptimierungsAlgoCFD.sln
```

Alle über 780 Unit-Tests laufen in wenigen Sekunden auf Windows und Linux komplett ohne installierte externe Solver (PicoGK, Gmsh, SU2 werden für Pipeline-Tests über schlanke Mock-/Stub-Implementierungen ersetzt). Neben den Unit-Tests für Algorithmen, Skalierung, GCI-Kalibrierung, Konvergenzanalyse und Journaling sichern fünf spezielle Testklassen die Konsistenz des gesamten Repositories ab:

- **`ProjectLayoutTests`**: Prüft, dass alle Konfigurationsdateien im jeweiligen Projektordner liegen, `config/` keine eingecheckten JSONs enthält und die Code-Default-Programmpfade PATH-relativ bleiben.
- **`TemplateProjectTests`**: Prüft, dass `_Vorlage3D` (3D) und `_Vorlage2D` (2D) vollständig sind, kommentierte JSONs besitzen und ihre Code-Defaults exakt mit den Vorlagen-JSONs übereinstimmen.
- **`HandbookCoverageTests`**: Prüft per Reflection über alle Options-Klassen und über `scripts/sim.ps1`, dass jede einzelne JSON-Einstellung und jeder Skript-Befehl in [`src/projects/BEDIENHANDBUCH.md`](../projects/BEDIENHANDBUCH.md) dokumentiert ist.
- **`ScriptContractTests`**: Prüft `scripts/sim.ps1` und `scripts/sim-runner.sh` statisch auf SSH-Timeouts, Parameterweitergabe (`-Project` → `SIM_PROJECT`) und Schutz vor stillen Fallbacks.
- **`RepositoryHygieneTests`**: Scant alle Textdateien in `src/`, `scripts/` und `config/` auf versehentlich eingecheckte Geheimnisse (Private Keys, API-Tokens, Passwörter).

---

## 10. Bewusste Architekturentscheidungen & Roadmap

1. **Der Türsteher (`ChampionValidator`) prüft die Fitness, nicht eine einzelne Metrik** — dadurch bleibt der Kern frei von Domänenbegriffen wie `"Drag"`.
2. **Fehlgeschlagene Simulationen werden nicht als RSM-Stützstellen verwendet** — sie würden die Antwortfläche verzerren und zählen auch nicht für die DoE-Mindestanzahl. Für den Bayes-Optimierer gilt dieselbe Regel zentral in `SurrogateTrainingSet`; zusätzlich fallen vom Türsteher verworfene Varianten (`Disqualified`) heraus.
3. **Der Kern kennt keine Metriknamen** — auch `RequiredMetrics` wird dem `WorkflowController` nur als `IReadOnlyCollection<string>` übergeben.
4. **Kein stiller Projekt-Fallback in `Program.cs` oder `sim-runner.sh`** — ein Tippfehler beim Projektnamen bricht sofort ab, statt stundenlang das falsche Bauteil zu rechnen.
5. **Der Ordnername ist die einzige Quelle für den Projektnamen** — es gibt kein Feld `"ProjectName"` in `project.json`.

### Ausblick & Nächste Meilensteine
Die historische Refactoring-Dokumentation sowie die aktuellen Ausbaupläne (2D/3D-Auswerteskripte, Bayes-Optimierung mit Gauß-Prozessen, LaTeX-Gesamtdokumentation und FEM-Strukturkopplung mit CalculiX) sind in **[`.plans/README.md`](../../.plans/README.md)** und **[`.plans/ROADMAP.md`](../../.plans/ROADMAP.md)** dokumentiert.
