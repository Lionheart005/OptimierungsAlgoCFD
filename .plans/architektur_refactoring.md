# Architektur-Refactoring: Projektbasiertes Plugin-System

> **Stand: 18.09.2026** — Branch `Refactoring`, letzter Commit `8141071 TODO-13`.
> **Block A ist fertig** (TODO-1 bis TODO-5 + TODO-13 + TODO-18): `Core/` enthält kein
> projektspezifisches Wissen mehr, der Bouncer ist Framework-Bestandteil und prüft die
> Fitness, Mesher hängen pro Solver-Stufe.
> **Offen:** Block B (JSON-Konfiguration), Block C (PicoGK austauschbar) und der Rest von
> Block D. Die TODO-Liste am Ende ist die Arbeitsgrundlage zum Fertigstellen.

## Ziel

Das Framework soll so umgebaut werden, dass **verschiedene Projekte** (z.B. AUV-Manta, Drohnen-Propeller, Wärmetauscher) jeweils eigene Geometrie-Erzeugung, Fitness-Funktion, Solver-Konfiguration und Parameter mitbringen können — **ohne das Hauptprogramm zu verändern**. Gleichzeitig soll der Code testbar (Unit Tests) und nach Clean-Architecture-Prinzipien aufgebaut sein, ohne unnötig aufgebläht zu werden.

---

## Design-Entscheidungen (verbindlich)

Diese Punkte sind entschieden und ersetzen die ursprünglichen "Offenen Fragen":

| # | Entscheidung | Konsequenz |
|---|---|---|
| **1** | **Mesher austauschbar pro Solver.** Externe Programme können Netz-Ansprüche haben, die Gmsh nicht erfüllt. Es muss aber auch möglich sein, alles über Gmsh laufen zu lassen. | Der Controller braucht eine Solver↔Mesher-Zuordnung, kein einzelnes `IMeshGenerator`-Feld. |
| **2** | **PicoGK austauschbar.** Andere Geometrie-Kernel müssen möglich sein. `Library.Go()` ist aber Voraussetzung für den Start von PicoGK. | Der Start-Mechanismus muss vom `IGeometryGenerator` kommen, nicht hart in `Program.cs` stehen. Kein Framework-Kern-Code darf `using PicoGK` haben. |
| **3** | **Konfiguration zweigeteilt.** Reine Vorgabedaten, Zahlen und Pfade zu externen Programmen → **JSON-Datei**. Logik (Fitness-Berechnung, PicoGK-Geometrieerzeugung) → **C#-Code**. | `SimulationConfig`, `ProjectConfig` und die Solver-/Mesher-Parameter werden aus JSON geladen. Fitness/Geometrie bleiben Klassen. |
| **4** | **RubberBandScaler und StlSmoother bleiben im Framework**, müssen aber **ein- und ausschaltbar** sein. | Zwei Schalter in der Konfiguration; Aufrufer müssen den Aus-Fall sauber behandeln. |
| **5** | **Monorepo.** | Eine Solution, `src/` + `tests/`. ✅ umgesetzt. |
| **6** | **Der Bouncer ist NICHT mehr projektspezifisch**, sondern fester Framework-Bestandteil. Er lässt mit dem Gewinner-Modell einer Generation **die komplette Schleife nochmal ablaufen** zur Überprüfung — ohne projektspezifische Anpassung. | `MantaModelValidator` muss durch einen generischen `ChampionValidator` im Kern ersetzt werden, der nicht auf `"Drag"` prüft, sondern auf die **Fitness**. |

---

## Verifikationsstand (18.09.2026)

```
dotnet build Automatisierung.sln   →  Build succeeded. 0 Warnings, 0 Errors
dotnet test  Automatisierung.sln   →  Passed: 4, Failed: 0, Skipped: 0
```
(Sollstand nach Block A unverändert: 0/0/4 — es sind noch keine Tests dazugekommen, siehe TODO-19.)

Der Code kompiliert und die vorhandenen Tests laufen. **Die Pipeline wurde nicht end-to-end
ausgeführt** (braucht Linux + SU2 + Gmsh + MPI), Laufzeitfehler sind also nicht ausgeschlossen.

**Funktionaler Vergleich gegen `main`:** Geometrie-Erzeugung, Fitness-Formel, Gmsh-Geo-Skript,
SU2-Config und Drag-Auslesen wurden 1:1 übernommen — kein Funktionsverlust festgestellt.

**Bewusste Verhaltensänderungen gegenüber `main`** (alles andere rechnet identisch):
1. Der Viewer-Zweig (`showInViewer`) ist entfallen (wurde in `main` immer mit `false` aufgerufen).
2. Der `TailTaper`-Skalierungsbug ist behoben — `TailTaper` wird nicht mehr mitskaliert.
3. **Bouncer prüft Fitness statt Drag** (Entscheidung 6, TODO-3). Bei gleicher Toleranz
   (0.20) ist er dadurch **strenger**: die Fitness reagiert über `drag^DragBalanceFactor`
   und das Volumen empfindlicher auf den Jitter als der Drag allein. Der Wert
   `SimulationConfig.BouncerTolerance` muss beim ersten echten Lauf nachjustiert werden.
4. **RSM ignoriert fehlgeschlagene Simulationen** (TODO-2, vom Nutzer beauftragt): sie sind
   keine IDW-Stützstellen mehr und zählen nicht für die DoE-Mindestanzahl; stattdessen läuft
   eine Variante mehr. Ohne fehlgeschlagene Simulationen identisch zu vorher.
5. **CSV-Export**: Spaltennamen sind jetzt die Vereinigung über alle Records (vorher nur
   `History[0]`), zusätzlich gibt es die Spalte `SimulationFailed` (0/1) am Zeilenende.

---

## Ist-Struktur (existiert so im Branch)

```
AutomatisierungCleanVersion/
├── Automatisierung.sln
├── .gitignore                            ← liegt jetzt im Root (TODO-18 ✅)
├── .plans/architektur_refactoring.md
├── src/Automatisierung_v2/
│   ├── Automatisierung_v2.csproj
│   ├── Program.cs                        ← Composition Root, switch über Projektname
│   ├── Core/
│   │   ├── Interfaces/   IGeometryGenerator, IFitnessCalculator, ISimulationSolver,
│   │   │                 IMeshGenerator, IModelValidator, IOptimizationAlgorithm
│   │   ├── Models/       GeometryResult (+ MetricScaling), ModelRecord, ProjectConfig,
│   │   │                 SimulationConfig, SimulationContext, SolverStage
│   │   ├── Pipeline/     WorkflowController.cs, ChampionValidator.cs
│   │   ├── Algorithms/   EvolutionaryAlgorithm.cs, RsmOptimizationAlgorithm.cs
│   │   └── Utilities/    RubberBandScaler.cs, StlSmoother.cs
│   ├── Projects/MantaAuv/
│   │   ├── MantaGeometryGenerator.cs
│   │   ├── MantaFitnessCalculator.cs
│   │   └── MantaProjectConfig.cs
│   └── Solvers/Cfd/
│       ├── GmshCfdMesher.cs
│       ├── Su2Solver.cs
│       └── Su2ConfigGenerator.cs
└── tests/AutomatisierungCleanVersion.Tests/
    ├── AutomatisierungCleanVersion.Tests.csproj   (xUnit + Moq)
    ├── RubberBandScalerTests.cs          (1 Test)
    ├── EvolutionaryAlgorithmTests.cs     (1 Test)
    └── MantaFitnessCalculatorTests.cs    (2 Tests)
```

**Fehlt gegenüber der Zielstruktur:** die JSON-Konfigurationsdateien, `Solvers/Fem/`,
`WorkflowControllerTests.cs` (und die übrigen Tests aus TODO-19).

---

## Phasenstatus

### Phase 1: Interfaces definieren & Datenmodell aufräumen — ✅ fertig
- [x] Alle 6 Interfaces angelegt
- [x] `GeometryResult`, `ProjectConfig`, `SimulationConfig` angelegt
- [x] `SimulationData` → `SimulationContext`, AUV-Parameter raus
- [x] `Directory.CreateDirectory` aus dem Konstruktor → `EnsureDirectories()`

### Phase 2: Bestehende Klassen auf Interfaces umstellen — ✅ fertig
- [x] `PicoGkGenerator` → `MantaGeometryGenerator : IGeometryGenerator`
- [x] `FitnessCalculator` (static) → `MantaFitnessCalculator : IFitnessCalculator`
- [x] `FluidDynamicsAnalyzer` → `Su2Solver : ISimulationSolver` + `Su2ConfigGenerator`
- [x] `GmshConverter` → `GmshCfdMesher : IMeshGenerator`, Windkanal wandert mit
- [x] `ValidateChampion()` → eigene Klasse
- [x] Dateien nach `Projects/MantaAuv/` und `Solvers/Cfd/` verschoben
- [x] `RubberBandScaler`-Bug behoben (nur dimensionale Parameter werden skaliert)

### Phase 3: WorkflowController entkoppeln — ✅ fertig
- [x] Konstruktor nimmt nur Interfaces
- [x] `new PicoGkGenerator()` etc. entfernt
- [x] Solver-Kette läuft, jetzt als `SolverStage[]` (Mesher + Solver pro Stufe)
- [x] Bouncer-Logik hinter `IModelValidator` + Resimulations-Callback
- [x] Hardcodierte Parameter-Keys raus (TODO-1, TODO-2)
- [x] Doppelter Pipeline-Block zusammengeführt (`RunPipeline`, TODO-5)

### Phase 4: Program.cs als Composition Root — ⚠️ teilweise
- [x] Projektauswahl via CLI-Argument, `switch` über Projektnamen
- [x] Verdrahtung der konkreten Implementierungen
- [x] Ordnerstruktur angelegt
- [ ] **Keine JSON-Konfiguration** (Entscheidung 3) — siehe TODO-6 bis TODO-9
- [ ] Algorithmuswahl nur auskommentiert statt konfigurierbar — siehe TODO-10

### Phase 5: Tests & FEM-Vorbereitung — ⚠️ angefangen
- [x] Test-Projekt mit xUnit + Moq angelegt und in der Solution
- [x] 4 Tests: Scaler (1), EA-Fitness-Delegation (1), Manta-Fitness (2)
- [x] Mesher-Zuordnung pro Solver über `SolverStage` (Entscheidung 1) — TODO-4
- [ ] Kein `WorkflowControllerTests.cs`, kein RSM-Test, kein Validator-Test, kein Smoother-Test

---

## TODOs

Reihenfolge = empfohlene Abarbeitung. Die Blöcke A–C bauen aufeinander auf,
Block D ist unabhängig und kann jederzeit dazwischen erledigt werden.

### Block A — Die Framework-Kern-Entkopplung fertigstellen ✅ ERLEDIGT

Nach diesem Block enthält `Core/` kein projektspezifisches Wissen mehr.
Abgearbeitet in der Reihenfolge 1 → 2 → 5 → 4 → 3 (TODO-5 vorgezogen, damit
TODO-3 und TODO-4 nicht doppelt gepflegt werden mussten).

- [x] **TODO-1 — Metrik-Rückskalierung entkoppeln** *(erledigt, Commit `103ee90`)*
  Umgesetzt wie beschrieben: `enum MetricScaling { None, Linear, Area, Volume }`,
  `GeometryResult.AddMetric(name, value, scaling)` / `ScalingFor(name)`,
  neue `RubberBandScaler.RestoreLength()`. Der Controller wendet in
  `ApplyGeometryMetrics(...)` nur noch an, was das Projekt deklariert hat; fehlt eine
  Deklaration, wird der Wert unverändert übernommen (`None`).
  Manta deklariert `Volume`→Volume, `FrontalArea`→Area, **`SensorDistance`→Area**
  (Kreuzprodukt/2) — die Fitness bleibt damit identisch.

- [x] **TODO-2 — Hardcodierten `"Drag"`-Fallback aus dem Controller entfernen** *(Commit `04a00d6`)*
  Neues `ModelRecord.SimulationFailed`; der Controller setzt im `catch` nur noch dieses Flag.
  `MantaFitnessCalculator` wertet es aus (Fitness `0.0001`), die alte `drag == float.MaxValue`-
  Prüfung bleibt als Fallback stehen.
  **Zusätzlich im selben Commit** (Folgen des entfallenen `Drag`-Eintrags, mit dem Nutzer abgestimmt):
  - `SimulationContext.ExportToCsv()` bildet die Spalten jetzt als Vereinigung über **alle**
    Records statt nur über `History[0]` — sonst fehlt eine Metrik im ganzen Export, sobald
    sie im ersten Record fehlt. Neue Spalte `SimulationFailed` (0/1) am Zeilenende.
  - `RsmOptimizationAlgorithm`: fehlgeschlagene Modelle sind keine IDW-Stützstellen mehr
    und zählen nicht für die DoE-Mindestanzahl (`usableSamples`); es läuft stattdessen eine
    Variante mehr. Bewusste Verhaltensänderung, siehe Verifikationsstand oben.

- [x] **TODO-3 — Generischen `ChampionValidator` bauen (Entscheidung 6)** *(Commit `d2d2c16`)*
  `Core/Pipeline/ChampionValidator : IModelValidator` vergleicht die **Fitness** des
  Re-Simulats mit der des Kandidaten. `IFitnessCalculator` wird per Konstruktor injiziert,
  eine `Random`-Instanz optional (für TODO-19). Der Resimulations-Callback fährt über
  `RunPipeline` die ganze Schleife. Toleranz und Jitter kommen aus
  `SimulationConfig.BouncerTolerance` / `.BouncerJitter`; `BouncerTolerance` ist aus
  `MantaProjectConfig.OptimizationTargets` entfernt. `MantaModelValidator.cs` gelöscht.
  ⚠ Der Bouncer ist bei gleicher Toleranz strenger als vorher — siehe Verifikationsstand.

- [x] **TODO-4 — Mesher pro Solver zuordnen (Entscheidung 1)** *(Commit `f5e60ee`)*
  `record SolverStage(IMeshGenerator Mesher, ISimulationSolver Solver)`; der Controller
  nimmt `SolverStage[]` statt `IMeshGenerator` + `ISimulationSolver[]`. Stufen, die sich
  eine Mesher-**Instanz** teilen, vernetzen nur einmal (Cache über Referenzgleichheit).
  `ModelRecord.MeshPath` führt weiterhin genau einen Pfad — den der ersten Stufe;
  wer mehrere Netze protokollieren will, braucht dort eine Liste (offener Punkt).

- [x] **TODO-5 — Doppelten Pipeline-Block zusammenführen** *(Commit `b31048c`)*
  Private `RunPipeline(parameters, iteration, variant)` liefert den fertigen `ModelRecord`;
  `RunOptimization` und `ResimulateForValidation` nutzen sie beide. Der Validierungslauf
  setzt dadurch jetzt ebenfalls `ScaleFactor` und hat ein `try/catch` um Vernetzung und
  Solver-Kette (Abbruch → `SimulationFailed` statt durchgereichter Exception).
  Die Variantennummer 99 für Validierungsläufe steht als Konstante `ValidationVariantNumber`.

### Block B — Konfiguration nach JSON (Entscheidung 3)

- [ ] **TODO-6 — `SimulationConfig` aus JSON laden**
  `Core/Models/SimulationConfig.cs` ist komplett hardcodiert, inklusive der Linux-Pfade
  zu mpirun/SU2/Gmsh (`:10-12`). Genau das sollte laut Entscheidung 3 in eine JSON-Datei.
  **Lösung:** `config/simulation.json` + Loader (`System.Text.Json`).
  `CreateDefault()` bleibt als Fallback, wenn keine Datei da ist.
  Die Pfade müssen pro Maschine überschreibbar sein (Windows-Entwicklung vs. Linux-Server)
  — z.B. `simulation.local.json`, das die Basisdatei überlagert und in `.gitignore` steht.

- [ ] **TODO-7 — `MantaProjectConfig` nach JSON**
  `Projects/MantaAuv/MantaProjectConfig.cs` enthält nur Zahlen (BaseParameters,
  MaxDeviations, ParameterBounds, OptimizationTargets, DimensionalParameters) — also
  reine Vorgabedaten. **Lösung:** `Projects/MantaAuv/project.json`.
  *Stolperstein:* `ParameterBounds` ist ein `Dictionary<string, (float Min, float Max)>`;
  ValueTuples serialisiert `System.Text.Json` nicht. Ein kleines DTO
  (`{ "Length": { "min": 30, "max": 100 } }`) einführen und beim Laden umwandeln.
  Die **Geometrie- und Fitness-Klassen bleiben C#** — nur die Zahlen wandern.

- [ ] **TODO-8 — Solver- und Mesher-Zahlen nach JSON**
  Hardcodierte Werte, die laut Entscheidung 3 Vorgabedaten sind:
  - `Solvers/Cfd/Su2ConfigGenerator.cs:31-33` — Dichte 1025.0, `MU_CONSTANT` 1.001e-3,
    Turbulenzmodell `SA`, Schallgeschwindigkeit 343.2 (`:17`), `REF_LENGTH` 0.01 (`:41`),
    CFL-Parameter (`:58-60`).
  - `Solvers/Cfd/GmshCfdMesher.cs:27-28` — Windkanal-Abmessungen (600×300×300 mm).
  - `Solvers/Cfd/GmshCfdMesher.cs:88-92` — Grenzschicht-Feld (SizeMin 1.2, SizeMax 120.0,
    DistMin 3.0, DistMax 40.0).
  **Lösung:** `Su2SolverOptions` / `GmshMesherOptions` als Klassen, aus JSON geladen,
  per Konstruktor injiziert.

- [ ] **TODO-9 — Schalter für Scaler und Smoother (Entscheidung 4)**
  Beide laufen aktuell immer:
  - `RubberBandScaler` wird in `WorkflowController.cs:76` und `:179` bedingungslos erzeugt.
  - `StlSmoother.SmoothStl(...)` wird in `MantaGeometryGenerator.cs:97` bedingungslos gerufen.
  **Lösung:** `UseRubberBandScaler` und `UseStlSmoothing` in `SimulationConfig` (aus JSON).
  - Scaler aus → `ShrinkFactor = 1.0`, Parameter unverändert durchreichen, Restore-Methoden
    werden zu Identität. Am saubersten über eine Neutral-Instanz, dann braucht der
    Controller kein `if` an vier Stellen.
  - Smoother aus → Aufruf überspringen. Der Schalter muss den `IGeometryGenerator`
    erreichen; passt gut zu TODO-14 (Signatur aufräumen).

### Block C — PicoGK austauschbar machen (Entscheidung 2)

- [ ] **TODO-10 — `Library.Go()` hinter die Geometrie-Abstraktion**
  `Program.cs:62` ruft `Library.Go(...)` direkt — auch ein Projekt ohne PicoGK müsste da durch.
  **Lösung:** `IGeometryGenerator` (oder ein separates `IGeometryKernel`) bekommt eine
  Methode wie `void RunHosted(float voxelResolution, Action body)`. Der PicoGK-Kernel
  implementiert sie als `Library.Go(res, body)`, ein anderer Kernel ruft `body()` direkt auf.
  `Program.cs` ruft nur noch `kernel.RunHosted(...)`.
  Beim gleichen Durchgang: die Algorithmuswahl (`Program.cs:43-44`, aktuell auskommentiert)
  über die JSON-Config statt über Auskommentieren steuerbar machen.

- [ ] **TODO-11 — PicoGK-Abhängigkeit aus `Solvers/` entfernen**
  `Solvers/Cfd/GmshCfdMesher.cs:6,33` nutzt `using PicoGK` und `Utils.mshCreateCube`,
  nur um den Windkanal-Quader als STL zu schreiben. Damit hängt die CFD-Schicht am
  Geometrie-Kernel. **Lösung:** den Quader direkt als ASCII- oder Binär-STL schreiben
  (12 Dreiecke, ~40 Zeilen Code) oder den Tunnel in Gmsh selbst per `Box{...}` erzeugen.
  Danach hat außer `Projects/MantaAuv/` keine Datei mehr `using PicoGK`.

- [ ] **TODO-12 — Referenzflächen-Metrik im Solver konfigurierbar**
  `Solvers/Cfd/Su2Solver.cs:24-26` liest fest `PassiveParameters["FrontalArea"]`.
  Ein Projekt, das die Metrik anders nennt, bekommt still `refArea = 1.0` — der Drag-Wert
  wäre dann um Größenordnungen falsch, ohne Fehlermeldung.
  **Lösung:** Metrikname in `Su2SolverOptions` (TODO-8), und wenn der Key fehlt:
  Warnung ausgeben statt still weiterzurechnen.

### Block D — Korrektheit, Aufräumen, Tests

Unabhängig von A–C, jederzeit erledigbar.

- [x] **TODO-13 — `_fitness` im Controller ist toter Code** *(Commit `8141071`)*
  Ersatzlos gestrichen, samt Konstruktor-Parameter. Nach TODO-3 braucht der Controller
  den `IFitnessCalculator` nicht: die Bewertung läuft über
  `IOptimizationAlgorithm.EvaluateAndSelectBest`, den Re-Sim-Record bewertet der
  `ChampionValidator` selbst.

- [ ] **TODO-14 — `IGeometryGenerator`-Signatur aufräumen**
  `Core/Interfaces/IGeometryGenerator.cs:14` deklariert `float voxelSmoothingIterations`,
  obwohl der Wert in `SimulationConfig` ein `int` ist und der Generator ihn mit
  `(int)` zurückcastet (`MantaGeometryGenerator.cs:97`). Außerdem sind zwei
  STL-Glättungsparameter in einem allgemeinen Geometrie-Interface fehl am Platz.
  **Lösung:** stattdessen `SimulationConfig` (oder ein `GeometryOptions`-Objekt) übergeben.
  Passt zusammen mit dem Smoother-Schalter aus TODO-9.

- [ ] **TODO-15 — "ABSOLUTER CHAMPION" ist der letzte, nicht der beste**
  `Core/Pipeline/WorkflowController.cs:157` überschreibt `previousWinner` in jeder Iteration;
  `FinishOptimization(previousWinner)` (`:171`) zeigt daher den Champion der **letzten**
  Iteration. Beim EA ist das meist, aber nicht zwingend der beste — beim RSM mit seiner
  zufälligen DoE-Phase deutlich öfter nicht.
  *(Der Fehler steckt schon in `main`, ist also kein Refactoring-Regress.)*
  **Lösung:** besten Record über `_context.History` per Fitness bestimmen.

- [ ] **TODO-16 — Inkonsistenter Dictionary-Zugriff auf `OptimizationTargets`**
  `Projects/MantaAuv/MantaFitnessCalculator.cs:30-32` greift mit dem Indexer zu
  (`KeyNotFoundException`, wenn ein Projekt den Key vergisst), `MantaModelValidator.cs:30-32`
  benutzt `ContainsKey` mit Default. Einheitlich machen — bei fehlendem Pflicht-Key lieber
  früh und mit klarer Meldung abbrechen als mitten im Lauf.

- [ ] **TODO-17 — Laufzeitzustand aus der `ProjectConfig` herausziehen**
  `Core/Algorithms/EvolutionaryAlgorithm.cs:95-123` schreibt während des Laufs in
  `context.Project.BaseParameters` und `context.Project.MaxDeviations` — die Konfiguration
  wird also mutiert. Sobald sie aus JSON kommt (TODO-7), vermischt das geladene Vorgabe
  mit Laufzeitzustand, und ein zweiter Lauf im selben Prozess startet mit den Werten des
  ersten. **Lösung:** veränderliche Werte in den `SimulationContext` (z.B.
  `CurrentBaseParameters`, `CurrentDeviations`), initial aus der `ProjectConfig` kopiert.

- [x] **TODO-18 — `.gitignore` liegt im falschen Verzeichnis** *(Commit `4c8f103`)*
  `.gitignore` per `git mv` ins Repo-Root verschoben, 141 Dateien unter
  `tests/AutomatisierungCleanVersion.Tests/bin|obj` mit `git rm -r --cached` aus der
  Versionskontrolle genommen (Dateien bleiben auf der Platte). `git status` ist wieder leer.
  Anmerkung für Block B: die geplante `simulation.local.json` muss noch in die `.gitignore`.

- [ ] **TODO-19 — Tests nachziehen**
  Vorhanden: 4 Tests (Scaler 1, EA-Delegation 1, Manta-Fitness 2). Es fehlen:
  - `WorkflowControllerTests.cs` — alle Interfaces mocken, Orchestrierung prüfen:
    Solver-Kette läuft in Reihenfolge, Metriken landen im Record, Fehler im Mesher
    bricht den Lauf nicht ab. (War in Phase 5 explizit vorgesehen.)
  - `ChampionValidatorTests.cs` — nach TODO-3: Disqualifikation bei zu großer
    Fitness-Abweichung, Fallback wenn kein Kandidat stabil ist.
  - `RsmOptimizationAlgorithmTests.cs` — IDW-Surrogat mit bekannten Stützstellen.
  - `StlSmootherTests.cs` — kleine Test-STL, Bounding Box bleibt erhalten.
  **Voraussetzung:** `Random` in `EvolutionaryAlgorithm.cs:14`, `RsmOptimizationAlgorithm.cs:15`
  und `MantaModelValidator.cs:28` ist ungeseedet — für reproduzierbare Tests einen
  optionalen Seed bzw. eine injizierbare `Random`-Instanz vorsehen.

- [ ] **TODO-20 — Namespaces an die Ordnerstruktur angleichen** *(optional, kosmetisch)*
  Alle Dateien liegen in `namespace MyPicoGkProject`, obwohl die Ordner `Core`,
  `Projects`, `Solvers` trennen. Der Compiler erzwingt dadurch keine Schichtentrennung —
  ein Projekt-Namespace könnte munter Kern-Interna benutzen.
  Mit `MyPicoGkProject.Core`, `.Projects.MantaAuv`, `.Solvers.Cfd` wird der Bruch sichtbar.
  Nur anfassen, wenn Blöcke A–C durch sind; sonst produziert es unnötige Merge-Konflikte.

- [ ] **TODO-21 — End-to-End-Lauf gegen `main` verifizieren**
  Der Refactoring-Branch ist nur statisch geprüft (Build + Unit Tests). Vor dem Merge
  einen echten Lauf auf dem Linux-Server fahren (klein: `MaxIterations=1`,
  `VariantsPerIteration=2`) und die `Simulation_Results.csv` gegen einen `main`-Lauf mit
  denselben Startparametern vergleichen. Volumen, FrontalArea und Drag müssen in derselben
  Größenordnung liegen. **Erwartete Abweichung:** `TailTaper` wird jetzt korrekt *nicht*
  mehr skaliert (Bugfix), die Geometrie ist also bewusst eine andere — das ist die einzige
  Differenz, die auftreten darf.

---

## Was sich NICHT ändert

- **Optimierungsalgorithmen** (EA, RSM) bleiben im Kern — sie sind projektunabhängig
- **StlSmoother** bleibt ein Utility im Framework (nur abschaltbar, TODO-9)
- **RubberBandScaler** bleibt im Framework (nur abschaltbar, TODO-9)
- **ModelRecord** bleibt das zentrale Datenmodell
- **CSV-Export** bleibt in `SimulationContext` — ist schon dynamisch
- **Die Struktur der Optimierungsschleife** im WorkflowController bleibt gleich
- **Fitness-Formel und Geometrie-Erzeugung bleiben C#-Code** (Entscheidung 3)

---

## Zusammenfassung

```
VORHER:  Program → WorkflowController → [PicoGkGenerator, GmshConverter, SU2, FitnessCalc]
                                          ↑ alles hardcoded, alles Manta-Ray

JETZT:   Program → wählt Projekt → verdrahtet Interfaces → WorkflowController
                                                              ↓
                                                    [IGeometryGenerator]  ✅ Projekt liefert
                                                    [SolverStage[]]       ✅ Mesher+Solver pro Stufe
                                                    [IFitnessCalculator]  ✅ Projekt liefert
                                                    [IModelValidator]     ✅ ChampionValidator im Kern

ZIEL (offen):
         + JSON-Konfiguration für alle Zahlen und Pfade   (Block B)
         + Scaler und Smoother abschaltbar                (TODO-9)
         + PicoGK hinter einer Kernel-Abstraktion         (Block C)
```

**Ergebnis nach Abschluss:** Neues Projekt anlegen = 2 C#-Dateien (Geometrie + Fitness)
plus eine `project.json` in `Projects/MeinProjekt/`. Framework-Code bleibt unberührt.
