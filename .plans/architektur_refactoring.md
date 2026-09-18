# Architektur-Refactoring: Projektbasiertes Plugin-System

> **Stand: 18.09.2026** — Branch `Refactoring`, letzter Commit `1e11b08 TODO-12`.
> **Block A ist fertig** (TODO-1 bis TODO-5 + TODO-13 + TODO-18): `Core/` enthält kein
> projektspezifisches Wissen mehr, der Bouncer ist Framework-Bestandteil und prüft die
> Fitness, Mesher hängen pro Solver-Stufe.
> **Block B ist fertig** (TODO-6 bis TODO-9 + TODO-14): alle Zahlen und Pfade kommen aus
> `config/*.json`, Scaler und Smoother sind abschaltbar.
> **Block C ist fertig** (TODO-10 bis TODO-12): `Program.cs` hat kein `using PicoGK` mehr,
> der Kernel-Start läuft über `IGeometryKernel`, die Algorithmuswahl über die Projekt-JSON,
> `Solvers/` schreibt den Windkanal selbst als STL und kennt keinen festen Metriknamen mehr.
> **Offen:** nur noch Block D (TODO-15, 16, 17, 19, 20, 21).

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
dotnet test  Automatisierung.sln   →  Passed: 63, Failed: 0, Skipped: 0
```
**Aktueller Sollstand: 0 Fehler, 0 Warnungen, 63/63 Tests.**
Block A hat keine Tests gebraucht (4/4), Block B hat 15 dazugebracht: Loader (6),
Projekt-JSON (3), Solver-Optionen (4), Scaler-Schalter (2). TODO-10 hat 10 dazugebracht
(Algorithmus-Fabrik, inkl. Groß-/Kleinschreibung, Fallback und Manta-Vorgabe).
TODO-11 hat 25 dazugebracht (Tunnel-Geometrie 13, STL-Writer 5, Mesher-Tunnel 7),
TODO-12 noch 9 (Referenzflächen-Metrik); die fünf neuen JSON-Schlüssel sind zusätzlich
in `SolverOptionsTests` mit abgedeckt.
Die JSON-Tests vergleichen jede mitgelieferte Datei gegen die Code-Vorgaben — dort
würde ein Zahlendreher auffallen.

**Zusätzlich einmalig gegengeprüft (TODO-11):** Das vom neuen `StlWriter` geschriebene
`Static_Windtunnel.stl` wurde gegen die Datei verglichen, die
`PicoGK.Utils.mshCreateCube(new Vector3(600,300,300), Vector3.Zero).SaveToStlFile(...)`
erzeugt. Beide sind 684 Byte groß und ab Offset 84 (also alle Normalen und Ecken)
**byteweise identisch** — nur der 80-Byte-Kopftext unterscheidet sich, den liest kein
Netzgenerator aus. Der `TunnelGeometryTests.Box_Reproduces_The_PicoGk_Cube_Triangle_For_Triangle`
hält dieselben 12 Dreiecke dauerhaft fest.

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
6. **Text der erzeugten SU2-cfg** (TODO-8): die Zahlen werden jetzt über
   `float.ToString(InvariantCulture)` formatiert, dadurch steht dort `1025` statt `1025.0`,
   `0.001001` statt `1.001e-3` und `( 0.5, 1.2, 1, 50 )` statt `( 0.5, 1.2, 1.0, 50.0 )`.
   SU2 liest beides identisch — die Physik ist unverändert.
7. **Kopfzeile von `Static_Windtunnel.stl`** (TODO-11): steht jetzt auf
   `Automatisierung STL UNITS=mm` statt `PicoGK UNITS=mm`. Der 80-Byte-Kopf eines
   binären STL ist reiner Kommentar; Geometrie und Dreiecksreihenfolge sind
   byteweise unverändert.

---

## Ist-Struktur (existiert so im Branch)

```
AutomatisierungCleanVersion/
├── Automatisierung.sln
├── .gitignore                            ← liegt jetzt im Root (TODO-18 ✅)
├── .plans/architektur_refactoring.md
├── config/                               ← alle Zahlen und Pfade (TODO-6..8 ✅)
│   ├── simulation.json                   ← Framework-Konfiguration
│   ├── projects/MantaAuv.json            ← Projektvorgaben
│   └── solvers/su2.json, gmsh.json       ← Solver-/Mesher-Vorgaben
│   (jeweils *.local.json daneben möglich — überlagert, gitignored)
├── src/Automatisierung_v2/
│   ├── Automatisierung_v2.csproj
│   ├── Program.cs                        ← Composition Root, switch über Projektname
│   ├── Core/
│   │   ├── Interfaces/   IGeometryGenerator, IGeometryKernel, IFitnessCalculator,
│   │   │                 ISimulationSolver, IMeshGenerator, IModelValidator,
│   │   │                 IOptimizationAlgorithm
│   │   ├── Models/       GeometryResult (+ MetricScaling), ModelRecord, ProjectConfig,
│   │   │                 SimulationConfig, SimulationContext, SolverStage
│   │   ├── Configuration/ JsonConfigLoader.cs, ProjectConfigDto.cs
│   │   ├── Pipeline/     WorkflowController.cs, ChampionValidator.cs
│   │   ├── Algorithms/   EvolutionaryAlgorithm.cs, RsmOptimizationAlgorithm.cs,
│   │   │                 OptimizationAlgorithmFactory.cs
│   │   └── Utilities/    RubberBandScaler.cs, StlSmoother.cs, DirectGeometryKernel.cs,
│   │                     StlWriter.cs (+ StlTriangle, TODO-11 ✅)
│   ├── Kernels/PicoGk/
│   │   └── PicoGkKernel.cs               ← einzige Datei außerhalb von Projects/
│   │                                       mit `using PicoGK` (TODO-10 ✅)
│   ├── Projects/MantaAuv/
│   │   ├── MantaGeometryGenerator.cs     ← hat als einzige Projektdatei `using PicoGK`
│   │   ├── MantaFitnessCalculator.cs
│   │   └── MantaProjectConfig.cs         ← nur noch Fallback-Vorgaben
│   └── Solvers/Cfd/
│       ├── GmshCfdMesher.cs, GmshMesherOptions.cs   ← PicoGK-frei (TODO-11 ✅)
│       ├── TunnelGeometry.cs             ← Windkanal als Dreiecksliste (TODO-11 ✅)
│       ├── Su2Solver.cs, Su2SolverOptions.cs
│       └── Su2ConfigGenerator.cs
└── tests/AutomatisierungCleanVersion.Tests/
    ├── AutomatisierungCleanVersion.Tests.csproj   (xUnit + Moq)
    ├── RubberBandScalerTests.cs          (3 Tests)
    ├── EvolutionaryAlgorithmTests.cs     (1 Test)
    ├── MantaFitnessCalculatorTests.cs    (2 Tests)
    ├── JsonConfigLoaderTests.cs          (6 Tests)
    ├── ProjectConfigJsonTests.cs         (3 Tests)
    ├── SolverOptionsTests.cs             (4 Tests)
    ├── OptimizationAlgorithmFactoryTests.cs  (10 Tests)
    ├── TunnelGeometryTests.cs            (13 Tests)
    ├── StlWriterTests.cs                 (5 Tests)
    ├── GmshCfdMesherTunnelTests.cs       (7 Tests)
    └── Su2ReferenceAreaTests.cs          (9 Tests)
```

**Fehlt gegenüber der Zielstruktur:** `Solvers/Fem/`, `WorkflowControllerTests.cs`
(und die übrigen Tests aus TODO-19).

**Aufrufkonvention:** `<Projektname> [Konfigurationsverzeichnis]`, z.B.
`dotnet run -- MantaAuv` oder `dotnet run -- MantaAuv /opt/auv/config`.
Ohne zweites Argument wird `config/` gesucht — erst im Arbeitsverzeichnis, dann bis zu
sechs Ebenen darüber, weil `dotnet run` im Projektordner startet.

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

### Phase 4: Program.cs als Composition Root — ✅ fertig
- [x] Projektauswahl via CLI-Argument, `switch` über Projektnamen
- [x] Verdrahtung der konkreten Implementierungen
- [x] Ordnerstruktur angelegt
- [x] JSON-Konfiguration (Entscheidung 3) — TODO-6 bis TODO-9
- [x] Algorithmuswahl über `config/projects/<Projekt>.json` — TODO-10
- [x] `Library.Go(...)` hinter `IGeometryKernel` — TODO-10

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

### Block B — Konfiguration nach JSON (Entscheidung 3) ✅ ERLEDIGT

Entscheidung zur Ablage (vom Nutzer bestätigt): **alle** JSON-Dateien liegen unter
`config/` im Repo-Root, nicht neben dem Projektcode. Ein Fundort, eine Suchlogik,
eine Overlay-Regel (`*.local.json`, gitignored). Das Verzeichnis ist per zweitem
CLI-Argument überschreibbar.

- [x] **TODO-6 — `SimulationConfig` aus JSON laden** *(Commit `156f390`)*
  `Core/Configuration/JsonConfigLoader.cs`: Standardwerte → `simulation.json` →
  `simulation.local.json`. Zusammengeführt wird auf `JsonNode`-Ebene, jede Stufe
  überschreibt nur die Schlüssel, die sie nennt. Fehlende Datei = kein Fehler,
  ungültiges JSON bricht mit Dateiname ab. Kommentare und nachgestellte Kommata erlaubt.
  `CreateDefault()` bleibt der Fallback.

- [x] **TODO-7 — Projektzahlen nach JSON** *(Commit `e565dd4`)*
  Liegt als `config/projects/MantaAuv.json` (nicht in `Projects/MantaAuv/`, s.o.).
  Der ValueTuple-Stolperstein ist über `ProjectConfigDto` + `ParameterBoundsDto` gelöst:
  `"Length": { "Min": 30, "Max": 100 }`. `MantaProjectConfig.Create()` bleibt als
  Code-Vorgabe, die Datei überlagert sie. Geometrie und Fitness bleiben C#.

- [x] **TODO-8 — Solver- und Mesher-Zahlen nach JSON** *(Commit `4599692`)*
  `Su2SolverOptions` (`config/solvers/su2.json`) und `GmshMesherOptions`
  (`config/solvers/gmsh.json`), beide per Konstruktor injiziert, geladen über
  `JsonConfigLoader.LoadSolverOptions<T>`. Zahlen unverändert; die erzeugte SU2-cfg
  formatiert sie jetzt über `InvariantCulture` und sieht daher minimal anders aus
  (siehe Verifikationsstand, Punkt 6).

- [x] **TODO-9 — Schalter für Scaler und Smoother (Entscheidung 4)** *(Commit `3fd9140`)*
  `UseRubberBandScaler` und `UseStlSmoothing` in `SimulationConfig`, Standard beide `true`.
  Scaler aus → `RubberBandScaler.Create(false, ...)` liefert eine Neutral-Instanz
  (ShrinkFactor 1.0, Parameter unverändert, Restore-Methoden = Identität); der Controller
  braucht kein `if`. Smoother aus → der Generator überspringt den Aufruf.
  **Zusammen mit TODO-14 umgesetzt**, weil der Smoother-Schalter den `IGeometryGenerator`
  erreichen musste.

### Block C — PicoGK austauschbar machen (Entscheidung 2) ✅ ERLEDIGT

- [x] **TODO-10 — `Library.Go()` hinter die Geometrie-Abstraktion** *(Commit `9c5b7b3`)*
  **Entscheidung (vom Nutzer, 18.09.2026):** ein **separates `IGeometryKernel`**, nicht
  eine Methode auf `IGeometryGenerator` — der Kernel ist projektunabhängig, mehrere
  Projekte teilen sich PicoGK, und ein Projekt ohne Voxel-Kernel muss keine
  Hosting-Methode mitschleppen.
  - `Core/Interfaces/IGeometryKernel`: `Name` + `void RunHosted(float voxelResolution, Action body)`.
  - `Kernels/PicoGk/PicoGkKernel`: ruft `Library.Go`. `Library.Go` erwartet einen
    `ThreadStart`; die Umwandlung aus dem neutralen `Action` steckt in dieser Klasse,
    damit der Kern nichts davon weiß.
  - `Core/Utilities/DirectGeometryKernel`: ruft `body()` direkt — für Kernel ohne eigene
    Laufzeitumgebung und für Tests, die den Controller ohne PicoGK durchlaufen lassen.
  - `Program.cs` hat kein `using PicoGK` mehr; der `switch` wählt pro Projekt Generator
    **und** Kernel.

  **Algorithmuswahl:** neues `ProjectConfig.OptimizationAlgorithm` (+ `ProjectConfigDto`),
  gesetzt in `config/projects/MantaAuv.json`. **Ablageort vom Nutzer entschieden:**
  Projekt-JSON statt `simulation.json`, damit verschiedene Projekte dauerhaft
  verschiedene Verfahren fahren können. Aufgelöst über
  `Core/Algorithms/OptimizationAlgorithmFactory.Create(name, fitness)`:
  `"Rsm"` | `"Evolution"`, Groß-/Kleinschreibung und Leerzeichen egal.
  **Fehlerfall vom Nutzer entschieden:** unbekannter Name → Warnung auf der Konsole und
  Rückfall auf `"Rsm"`, kein Abbruch. Leerer Name = nicht konfiguriert, Rückfall ohne
  Warnung. Vorgabe ist `"Rsm"` — das bisher fest verdrahtete Verfahren, der Lauf rechnet
  also unverändert.

  **Tests: 19 → 29.** `OptimizationAlgorithmFactoryTests` (10 Fälle: Schreibweisen,
  beide Verfahren, Fallback bei Tippfehler/leer/null, Manta-Vorgabe `"Rsm"`);
  `ProjectConfigJsonTests` prüft den neuen Schlüssel mit.

- [x] **TODO-11 — PicoGK-Abhängigkeit aus `Solvers/` entfernen** *(Commit `0d045f9`)*
  `Solvers/Cfd/GmshCfdMesher.cs` nutzte `using PicoGK` und `Utils.mshCreateCube`
  (in `EnsureTunnel`), nur um den Windkanal als STL zu schreiben.

  **Entscheidung (vom Nutzer, 18.09.2026):** eigener STL-Writer im Framework,
  **nicht** der Umweg über Gmsh `Box{...}` — die Netz-Topologie des Farfields soll
  sich nicht ändern.

  **Umgesetzt:**
  - `Core/Utilities/StlWriter.cs`: `StlTriangle` (drei Ecken, Normale aus der
    Eckenreihenfolge) und `StlWriter.WriteBinary(path, triangles, header)`.
    Der 80-Byte-Kopf darf **nicht** mit `solid` beginnen, sonst halten Leser die Datei
    für ein ASCII-STL — deshalb `"Automatisierung STL UNITS=mm"`.
  - `Solvers/Cfd/TunnelGeometry.cs`: `CreateBox(size, center)` und
    `CreateCylinder(diameter, length, center, segments)`. Der Zylinder liegt mit der
    Achse auf X (= Strömungsrichtung), Mantel + zwei Deckelfächer, `4 × segments`
    Dreiecke, Normalen nach außen. Unsinnige Zahlen (Segmente < 3, Durchmesser/Länge
    ≤ 0) werfen `ArgumentOutOfRangeException` statt eine kaputte Hülle zu liefern.
  - `GmshMesherOptions` (→ `config/solvers/gmsh.json`): `TunnelShape`
    (`"Box"` | `"Cylinder"`, Standard `"Box"`), `TunnelDiameter` (300), `TunnelLength`
    (600), `TunnelSegments` (64). `TunnelSizeX/Y/Z` und `TunnelCenterX/Y/Z` bleiben;
    `TunnelCenter*` gilt für beide Formen.
  - **Fehlerfall wie bei der Algorithmuswahl (TODO-10):** unbekannter `TunnelShape` →
    Warnung auf der Konsole und Rückfall auf `"Box"`, kein Abbruch. Leerer Name =
    nicht konfiguriert, Rückfall ohne Warnung. Groß-/Kleinschreibung und Leerzeichen egal.
  - **Der Standardfall ist nachweislich exakt der bisherige Quader:** die geschriebene
    Datei ist ab Offset 84 byteweise identisch mit der von PicoGK erzeugten — gleiche
    12 Dreiecke, gleiche Reihenfolge, gleiche Orientierung (siehe Verifikationsstand).
    `TunnelGeometryTests` hält diese 12 Dreiecke als Literal fest.
  - Der Zylinder ist eine **neue Fähigkeit**, kein Ersatz — er ändert das Verhalten nur,
    wenn er in der JSON aktiv gewählt wird.

  **Tests: 29 → 54.** `TunnelGeometryTests` (13: PicoGK-Vergleich, Bounding Box,
  Außennormalen, Dichtigkeit über die Kantenbilanz für Quader und Zylinder,
  abgelehnte Zahlen), `StlWriterTests` (5: Dateilayout, Kopfzeile, Round-Trip,
  entartetes Dreieck), `GmshCfdMesherTunnelTests` (7: Standardquader, Zylinderwahl,
  Fallback bei Tippfehler, Cache).

- [x] **TODO-12 — Referenzflächen-Metrik im Solver konfigurierbar** *(Commit `1e11b08`)*
  `Su2Solver` las fest `PassiveParameters["FrontalArea"]`. Ein Projekt, das die Metrik anders
  nennt, bekam still die Ersatzfläche — der CD-Wert wäre dann um Größenordnungen falsch
  gewesen, ohne Fehlermeldung.

  **Umgesetzt:**
  - `Su2SolverOptions.ReferenceAreaMetric` (→ `config/solvers/su2.json`), Vorgabe
    `"FrontalArea"` — also unverändert für Manta. Führende/folgende Leerzeichen egal.
  - `Su2Solver.ResolveReferenceArea(record)` löst den Namen auf und rechnet mm² → m².
    Die Methode ist öffentlich, damit sie ohne MPI und SU2 testbar ist.
  - **Fehlt die Metrik** (oder ist kein Name konfiguriert), wird weitergerechnet, aber
    **nicht still**: `[WARNUNG]` mit Grund, Ersatzwert, der Liste der tatsächlich
    vorhandenen Metriken und dem Hinweis auf den JSON-Schlüssel. Die vorhandenen Metriken
    mitzudrucken ist der eigentliche Nutzen — ein Tippfehler im Namen ist so sofort sichtbar.
  - **Der Ersatzwert bleibt exakt der alte** (1 mm² → 1e-6 m²), damit sich im Fehlerfall
    nur die Meldung ändert, nicht die Zahl. *(Anmerkung: die frühere Planzeile sprach von
    `refArea = 1.0`; tatsächlich kamen durch die mm²-Umrechnung immer 1e-6 m² heraus.)*
  - Der Umrechnungsfaktor mm² → m² bleibt fest verdrahtet: dass Geometrie-Metriken in mm
    vorliegen, ist eine Konvention des ganzen Frameworks (PicoGK, STL, Gmsh-Skalierung),
    keine Eigenheit von SU2.

  **Tests: 54 → 63.** `Su2ReferenceAreaTests` (9): Vorgabe und Umrechnung, eigener
  Metrikname, fehlende Metrik → Warnung + Ersatzwert, leerer/nicht gesetzter Name,
  Leerzeichen im Namen, Record ganz ohne Metriken, und dass die aufgelöste Fläche
  unverändert als `REF_AREA` in der erzeugten SU2-cfg landet. `SolverOptionsTests`
  deckt den neuen JSON-Schlüssel mit ab.

### Block D — Korrektheit, Aufräumen, Tests

Unabhängig von A–C, jederzeit erledigbar.

- [x] **TODO-13 — `_fitness` im Controller ist toter Code** *(Commit `8141071`)*
  Ersatzlos gestrichen, samt Konstruktor-Parameter. Nach TODO-3 braucht der Controller
  den `IFitnessCalculator` nicht: die Bewertung läuft über
  `IOptimizationAlgorithm.EvaluateAndSelectBest`, den Re-Sim-Record bewertet der
  `ChampionValidator` selbst.

- [x] **TODO-14 — `IGeometryGenerator`-Signatur aufräumen** *(Commit `3fd9140`, mit TODO-9)*
  `GenerateAndExport(...)` nimmt jetzt `SimulationConfig` statt
  `(float voxelSmoothingIterations, int smoothingPremeltingSteps)`. Der falsche
  `float`-Typ und der `(int)`-Cast im Generator sind weg, und die Signatur passt zu
  `IMeshGenerator` und `ISimulationSolver`, die die Konfiguration ebenfalls als Ganzes nehmen.

- [ ] **TODO-15 — "ABSOLUTER CHAMPION" ist der letzte, nicht der beste**
  `Core/Pipeline/WorkflowController.cs:157` überschreibt `previousWinner` in jeder Iteration;
  `FinishOptimization(previousWinner)` (`:171`) zeigt daher den Champion der **letzten**
  Iteration. Beim EA ist das meist, aber nicht zwingend der beste — beim RSM mit seiner
  zufälligen DoE-Phase deutlich öfter nicht.
  *(Der Fehler steckt schon in `main`, ist also kein Refactoring-Regress.)*
  **Lösung:** besten Record über `_context.History` per Fitness bestimmen.

- [ ] **TODO-16 — Inkonsistenter Dictionary-Zugriff auf `OptimizationTargets`**
  `Projects/MantaAuv/MantaFitnessCalculator.cs:30-32` greift mit dem Indexer zu
  (`KeyNotFoundException`, wenn ein Projekt den Key vergisst); der frühere
  `MantaModelValidator` benutzte an derselben Stelle `ContainsKey` mit Default.
  *Anmerkung (18.09.2026): Die zweite Fundstelle ist mit TODO-3 entfallen — der
  `MantaModelValidator` ist gelöscht. Übrig bleibt die eigentliche Frage:* bei fehlendem
  Pflicht-Key früh und mit klarer Meldung abbrechen statt mitten im Lauf mit
  `KeyNotFoundException`. Betrifft jetzt nur noch den Fitness-Rechner.

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
  **Voraussetzung:** `Random` in `EvolutionaryAlgorithm.cs:14` und
  `RsmOptimizationAlgorithm.cs:15` ist ungeseedet — für reproduzierbare Tests einen
  optionalen Seed bzw. eine injizierbare `Random`-Instanz vorsehen. Der
  `ChampionValidator` hat das seit TODO-3 schon.
  Für Controller-Tests ohne PicoGK gibt es seit TODO-10 den `DirectGeometryKernel`.

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

         + JSON-Konfiguration für alle Zahlen und Pfade   ✅ Block B
         + Scaler und Smoother abschaltbar                ✅ TODO-9

         + Algorithmuswahl + Kernel-Start aus der Config  ✅ TODO-10
           [IGeometryKernel]     ✅ PicoGkKernel / DirectGeometryKernel
         + PicoGK raus aus Solvers/, Tunnel-Form wählbar  ✅ TODO-11
           [StlWriter + TunnelGeometry]  Box (wie bisher) | Cylinder
         + Referenzflächen-Metrik konfigurierbar          ✅ TODO-12

ZIEL (offen):
         + Korrektheit und Tests                          (Block D: TODO-15, 16, 17, 19, 20, 21)
```

**Ergebnis nach Abschluss:** Neues Projekt anlegen = 2 C#-Dateien (Geometrie + Fitness)
plus eine `project.json` in `Projects/MeinProjekt/`. Framework-Code bleibt unberührt.
