# Architektur-Refactoring: Projektbasiertes Plugin-System (v2)

## Ziel

Das Framework soll so umgebaut werden, dass **verschiedene Projekte** (z.B. AUV-Manta, Drohnen-Propeller, Wärmetauscher) jeweils eigene Geometrie-Erzeugung, Fitness-Funktion, Solver-Konfiguration und Parameter mitbringen können — **ohne das Hauptprogramm zu verändern**. Gleichzeitig soll der Code testbar (Unit Tests) und nach Clean-Architecture-Prinzipien aufgebaut sein, ohne unnötig aufgebläht zu werden.

---

## Entschiedene Design-Fragen

| Frage | Entscheidung |
|---|---|
| Solver-Mesh-Zuordnung | Jeder Solver bringt seinen Mesher mit (via `IMeshGenerator`), aber es ist auch möglich, einen zentralen Gmsh-Mesher für alles zu nutzen |
| PicoGK `Library.Go()` | PicoGK wird austauschbar — andere Geometrie-Kernel sollen möglich sein. `Library.Go()` wird nur aufgerufen, wenn PicoGK tatsächlich verwendet wird |
| Konfigurationsformat | **JSON** für Vorgabedaten (Pfade, Zahlen, Parameter, Bounds). **C#-Code** für Logik (Fitness-Berechnung, Geometrie-Erzeugung) |
| RubberBandScaler & StlSmoother | Framework-Bestandteil, aber **ein-/ausschaltbar** per Config |
| Monorepo vs. Multi-Projekt | **Monorepo** — alles in einer `.csproj` |
| Bouncer / Validierung | **Fester Framework-Bestandteil** — nicht projektspezifisch. Re-run der kompletten Pipeline mit dem Gewinner zur Verifikation |

---

## Status Quo: Probleme

| Problem | Wo genau | Warum kritisch |
|---|---|---|
| Geometrie ist Manta-Ray-hardcoded | `PicoGkGenerator.cs` L29-137 | Neues Projekt = kompletter Rewrite |
| Fitness-Formel ist hardcoded | `FitnessCalculator.cs` (static class) | Jedes Projekt braucht eine andere Zielfunktion |
| SU2-Konfiguration ist hardcoded | `FluidDynamicsAnalyzer.cs` L109-168 | Kein anderer Solver möglich |
| Gmsh-Geo ist hardcoded | `GmshConverter.cs` L21-103 | Kein FEM-Netz, kein anderes Format möglich |
| AUV-Parameter im Konstruktor | `SimulationData.cs` L104-122 | Neues Projekt = Konstruktor umschreiben |
| WorkflowController kennt konkrete Klassen | `WorkflowController.cs` L20-22 | Kein Austausch möglich |
| FitnessCalculator ist `static` | EA L74, RSM L45 | Nicht mockbar, nicht austauschbar |
| PicoGK `Library.Go()` umschließt alles | `Program.cs` L29 | Andere Geometrie-Kernel unmöglich |
| Windkanal in der Geometrie-Klasse | `PicoGkGenerator.cs` L13-27 | Gehört zum CFD-Setup |
| File-System-Effekte im Konstruktor | `SimulationData.cs` L98-101 | Unit Tests erzeugen Ordner |

---

## Ordnerstruktur

```
AutomatisierungCleanVersion/
│
├── Core/                          ← Framework-Kern (ändert sich NIE pro Projekt)
│   ├── Interfaces/
│   │   ├── IGeometryGenerator.cs       Geometrie-Erzeugung (PicoGK, OpenCascade, ...)
│   │   ├── IFitnessCalculator.cs       Fitness-Bewertung
│   │   ├── ISimulationSolver.cs        Simulations-Solver (CFD, FEM, ...)
│   │   ├── IMeshGenerator.cs           Vernetzung (Gmsh, Netgen, ...)
│   │   └── IOptimizationAlgorithm.cs   Optimierung (existiert schon, wird angepasst)
│   │
│   ├── Models/
│   │   ├── ModelRecord.cs              Daten eines einzelnen Modell-Variants
│   │   ├── GeometryResult.cs           Rückgabe der Geometrie-Erzeugung (flexibel)
│   │   ├── SimulationContext.cs        Laufzeit-Zustand: History, WorkingDir, Config-Zugriff
│   │   └── ProjectConfig.cs            Projekt-Parameter, Bounds, Targets (aus JSON geladen)
│   │
│   ├── Pipeline/
│   │   └── WorkflowController.cs       Orchestrierung (nur Interfaces, inkl. Bouncer)
│   │
│   ├── Algorithms/
│   │   ├── EvolutionaryAlgorithm.cs    (projektunabhängig)
│   │   └── RsmOptimizationAlgorithm.cs (projektunabhängig)
│   │
│   └── Utilities/
│       ├── StlSmoother.cs             Ein-/ausschaltbar per Config
│       └── RubberBandScaler.cs        Ein-/ausschaltbar per Config
│
├── Projects/                      ← Projektspezifischer Code
│   └── MantaAuv/
│       ├── MantaGeometryGenerator.cs     : IGeometryGenerator (PicoGK Manta-Lattice)
│       ├── MantaFitnessCalculator.cs     : IFitnessCalculator (Vol*SensorDist/Drag³)
│       └── project.json                  Parameter, Bounds, Deviations, Pfade, Targets
│
├── Solvers/                       ← Austauschbare Solver + Mesher
│   ├── Cfd/
│   │   ├── Su2Solver.cs              : ISimulationSolver
│   │   └── GmshCfdMesher.cs          : IMeshGenerator (inkl. Windkanal-Erzeugung)
│   │
│   └── Fem/                       ← Zukünftig
│       ├── CalculixSolver.cs         : ISimulationSolver
│       └── GmshFemMesher.cs          : IMeshGenerator (Solid-Mesh)
│
├── Config/
│   └── framework.json             Allgemeine Framework-Settings (Voxel-Auflösung, Iterationen, Smoothing on/off, ...)
│
├── Tests/
│   ├── FitnessCalculatorTests.cs
│   ├── EvolutionaryAlgorithmTests.cs
│   ├── RubberBandScalerTests.cs
│   └── WorkflowControllerTests.cs
│
├── Program.cs                     Einstiegspunkt: JSON laden, Projekt verdrahten
└── Automatisierung_v2.csproj      Monorepo — alles in einer Solution
```

---

## Die 5 Interfaces

### 1. `IGeometryGenerator`

```csharp
public interface IGeometryGenerator
{
    /// Erzeugt 3D-Geometrie und gibt STL + flexible Metriken zurück.
    GeometryResult GenerateAndExport(
        int iteration, int variant,
        Dictionary<string, float> parameters,
        string outputDirectory);

    /// Optionaler Lifecycle-Hook: Wird einmal beim Start aufgerufen.
    /// PicoGK-Projekte können hier Library.Go() vorbereiten.
    void Initialize(SimulationContext context);

    /// Optionaler Lifecycle-Hook: Wird am Ende aufgerufen.
    void Shutdown();
}
```

**Warum so?**
- `Initialize()` / `Shutdown()` lösen das `Library.Go()`-Problem: Ein PicoGK-basierter Generator kann in `Initialize()` die PicoGK-Runtime starten. Ein Generator basierend auf OpenCascade, BREP-Kernel oder reiner STL-Manipulation braucht das nicht.
- `GeometryResult` enthält ein flexibles `Dictionary<string, float> Metrics` statt des festen Tuples — jedes Projekt gibt zurück was es braucht.

### 2. `IFitnessCalculator`

```csharp
public interface IFitnessCalculator
{
    void CalculateFitness(ModelRecord record, ProjectConfig projectConfig);
}
```

- Von `static class` zu instanziierter Klasse mit Interface
- Wird per Konstruktor in die Algorithmen injiziert
- Jedes Projekt implementiert seine eigene Formel als C#-Klasse

### 3. `ISimulationSolver`

```csharp
public interface ISimulationSolver
{
    string Name { get; }

    /// Der Solver, dem dieses Mesh zugeordnet ist.
    IMeshGenerator Mesher { get; }

    /// Führt Simulation aus und schreibt Ergebnisse in record.PassiveParameters.
    void Solve(
        string meshPath,
        ModelRecord record,
        SimulationContext context);
}
```

**Warum `Mesher` als Property?**
- Jeder Solver weiß, welches Mesh-Format er braucht. SU2 braucht ein CFD-Fluid-Mesh (Gmsh mit Boolean-Subtraktion). CalculiX braucht ein Solid-FEM-Mesh. Beide können Gmsh nutzen, aber mit unterschiedlicher Konfiguration.
- Wenn ein Projekt nur CFD braucht, hat es einen Solver mit einem Mesher.
- Wenn ein Projekt CFD+FEM braucht, hat es zwei Solver, jeder mit seinem eigenen Mesher (oder beide teilen denselben Gmsh-Mesher mit verschiedenen Einstellungen).
- Wenn ein Solver gar keinen Mesher braucht (z.B. analytische Berechnung), kann `Mesher` null sein.

### 4. `IMeshGenerator`

```csharp
public interface IMeshGenerator
{
    string GenerateMesh(
        string modelStlPath,
        int iteration, int variant,
        SimulationContext context,
        float scaleFactor);
}
```

- `GmshConverter` wird zu `GmshCfdMesher : IMeshGenerator`
- Der **Windkanal** (`GenerateStaticTunnel`) wandert hierhin — er gehört zur CFD-Simulationsdomäne, nicht zur Geometrie-Erzeugung
- Ein `GmshFemMesher` würde das Solid-Volumen vernetzen statt den Fluidraum
- Ein komplett anderer Mesher (z.B. Netgen, TetGen) könnte ebenfalls implementiert werden

### 5. `IOptimizationAlgorithm` (Anpassung)

```csharp
public interface IOptimizationAlgorithm
{
    string Name { get; }
    Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants);
    ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context);
    void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord);
}
```

- `SimulationData` → `SimulationContext`
- `IFitnessCalculator` wird per Konstruktor in EA und RSM injiziert

---

## Datenmodelle

### `GeometryResult` (NEU)

```csharp
public class GeometryResult
{
    public string StlPath { get; set; } = "";
    public Dictionary<string, float> Metrics { get; set; } = new();
    // Manta: {"Volume": 4200, "FrontalArea": 850, "SensorDistance": 120}
    // Wärmetauscher: {"Volume": 3000, "SurfaceArea": 1200, "ChannelDiameter": 2.5}
}
```

### `ProjectConfig` (aus JSON geladen)

```csharp
public class ProjectConfig
{
    public string ProjectName { get; set; } = "";

    // Parameter-Definition
    public Dictionary<string, float> BaseParameters { get; set; } = new();
    public Dictionary<string, float> MaxDeviations { get; set; } = new();
    public Dictionary<string, (float Min, float Max)> ParameterBounds { get; set; } = new();

    // Welche Parameter sind Längen (in mm) → werden vom RubberBandScaler skaliert
    public HashSet<string> DimensionalParameters { get; set; } = new();

    // Projekt-spezifische Zielwerte für die Fitness-Berechnung
    public Dictionary<string, float> OptimizationTargets { get; set; } = new();

    // Pfade zu externen Programmen
    public Dictionary<string, string> ExternalPrograms { get; set; } = new();
    // z.B. {"mpirun": "/usr/bin/mpirun", "su2": "/opt/SU2/bin/SU2_CFD", "gmsh": "gmsh"}
}
```

Beispiel `Projects/MantaAuv/project.json`:

```json
{
  "projectName": "MantaAuv",
  "baseParameters": {
    "Length": 50.0,
    "Width": 40.0,
    "MainRadius": 4.0,
    "WingRadius": 3.0,
    "TailTaper": 1.0
  },
  "maxDeviations": {
    "Length": 5.0,
    "Width": 4.0,
    "MainRadius": 1.0,
    "WingRadius": 1.0,
    "TailTaper": 0.1
  },
  "parameterBounds": {
    "Length":     { "min": 30.0,  "max": 100.0 },
    "Width":      { "min": 20.0,  "max": 80.0  },
    "MainRadius": { "min": 4.0,   "max": 15.0  },
    "WingRadius": { "min": 2.0,   "max": 10.0  },
    "TailTaper":  { "min": 0.1,   "max": 1.5   }
  },
  "dimensionalParameters": ["Length", "Width", "MainRadius", "WingRadius"],
  "optimizationTargets": {
    "MinimumAllowedVolume": 2000,
    "MaximumAllowedVolume": 85000,
    "DragBalanceFactor": 3,
    "BouncerTolerance": 0.20
  },
  "externalPrograms": {
    "mpirun": "/home/lpleissner/miniconda/bin/mpirun",
    "su2":    "/home/lpleissner/software/bin/SU2_CFD",
    "gmsh":   "gmsh"
  }
}
```

### `framework.json` (allgemeine Framework-Settings)

```json
{
  "maxIterations": 10,
  "variantsPerIteration": 10,
  "baseVoxelResolution": 0.5,
  "targetPicoGkSize": 50.0,

  "enableStlSmoothing": true,
  "stlSmoothingIterations": 40,
  "stlSmoothingPremeltingSteps": 2,

  "enableRubberBandScaling": true,

  "cfd": {
    "machNumber": 0.1,
    "su2MaxIterations": 500,
    "cauchyElements": 150,
    "cauchyTolerance": "1e-4",
    "mpiCores": 12,
    "gmshCores": 12
  },

  "evolutionary": {
    "minimalDeviationExploration": 1.0,
    "minimalDeviationExploitation": 0.05,
    "roleFineTuner": 0.20,
    "roleCautious": 0.40,
    "roleNormal": 0.60
  },

  "rsm": {
    "virtualSimulations": 50000,
    "idwPower": 3.0,
    "exploitationRatio": 0.7,
    "exploitationSigma": 0.1
  }
}
```

### `SimulationContext` (ehemals `SimulationData`)

```csharp
public class SimulationContext
{
    public FrameworkConfig Framework { get; set; }     // Aus framework.json
    public ProjectConfig Project { get; set; }         // Aus project.json
    public string WorkingDirectory { get; set; }
    public List<ModelRecord> History { get; set; } = new();

    // Kein Directory.CreateDirectory im Konstruktor!
    // Das macht Program.cs explizit beim Setup.

    public void ExportToCsv() { ... }  // Bleibt dynamisch wie bisher
}
```

---

## Der Bouncer: Fester Framework-Bestandteil

Der Bouncer ist **kein Interface** und **nicht projektspezifisch**. Er ist eine generische Validierung, die im `WorkflowController` fest eingebaut ist:

### Funktionsweise

1. Der Optimizer wählt den Champion der aktuellen Iteration
2. Der Bouncer nimmt die **exakten Parameter** des Champions
3. Er ändert **einen zufälligen Parameter** minimal (±0.01)
4. Er durchläuft die **komplette Pipeline** erneut:
   - `IGeometryGenerator.GenerateAndExport(...)` 
   - RubberBandScaler (falls aktiv)
   - StlSmoother (falls aktiv)
   - Für jeden `ISimulationSolver`: `solver.Mesher.GenerateMesh(...)` → `solver.Solve(...)`
   - `IFitnessCalculator.CalculateFitness(...)`
5. **Vergleich:** Weicht die Fitness des Re-Runs um mehr als `BouncerTolerance` (z.B. 20%) vom Original ab → **Disqualifikation**
6. Nächster Kandidat wird geprüft

### Warum generisch?

Der Bouncer vergleicht die **Fitness** — nicht einen spezifischen PassiveParameter wie `"Drag"`. Da die Fitness bereits die projektspezifische Bewertung enthält (über `IFitnessCalculator`), ist der Bouncer automatisch für jedes Projekt korrekt:

```csharp
// Im WorkflowController (pseudocode):
private ModelRecord ValidateChampion(List<ModelRecord> candidates, ...)
{
    foreach (var candidate in candidates.OrderByDescending(r => r.Fitness))
    {
        if (candidate.Fitness < 0.1f) break;

        // 1. Gene leicht jittern
        var testParams = JitterOneParameter(candidate.ActiveParameters);

        // 2. KOMPLETTE Pipeline nochmal durchlaufen
        var rerunRecord = RunSingleVariant(testParams, iteration, validationVariant: 99);

        // 3. Fitness vergleichen (NICHT Drag oder irgendein spezifischer Wert)
        float originalFitness = candidate.Fitness;
        float rerunFitness = rerunRecord.Fitness;
        float deviation = Math.Abs(rerunFitness - originalFitness) / Math.Abs(originalFitness);

        if (deviation <= bouncerTolerance)
            return candidate;  // ✅ Stabil
        else
            candidate.Fitness = 0.0001f;  // ❌ Disqualifiziert
    }
    return candidates[0];  // Bester der Reste
}
```

Der Bouncer ruft dieselbe `RunSingleVariant()`-Methode auf, die auch der normale Iterations-Loop nutzt. Kein duplizierter Code, keine projektspezifischen Annahmen.

---

## PicoGK-Lifecycle: Austauschbarer Geometrie-Kernel

### Problem

`Library.Go(resolution, callback)` ist ein blockierender Aufruf, der:
1. Die PicoGK-Runtime (OpenGL, Voxel-Engine) startet
2. Den Callback ausführt
3. Die Runtime herunterfährt

Aktuell umschließt `Library.Go()` den **gesamten** Optimierungslauf. Ein Projekt ohne PicoGK kann so nicht laufen.

### Lösung

`IGeometryGenerator` hat `Initialize()` und `Shutdown()` Hooks. Der `Program.cs` Einstiegspunkt unterscheidet:

```csharp
// Program.cs
var generator = projectName switch {
    "MantaAuv" => new MantaGeometryGenerator(),
    "HeatExchanger" => new ParametricCadGenerator(),  // kein PicoGK
    _ => throw new ArgumentException(...)
};

// Der Generator entscheidet selbst, wie er startet:
generator.Initialize(context);
// → MantaGeometryGenerator ruft intern Library.Go() auf
// → ParametricCadGenerator macht gar nichts oder startet OpenCascade
```

**Für PicoGK-basierte Generatoren** gibt es eine abstrakte Basisklasse:

```csharp
public abstract class PicoGkGeneratorBase : IGeometryGenerator
{
    public void Initialize(SimulationContext context)
    {
        // Library.Go() ruft den WorkflowController als Callback auf
        Library.Go(context.Framework.BaseVoxelResolution, () => RunWithPicoGk(context));
    }

    protected abstract void RunWithPicoGk(SimulationContext context);

    public abstract GeometryResult GenerateAndExport(...);
    public virtual void Shutdown() { }
}
```

So bleibt PicoGK nutzbar, aber Projekte ohne PicoGK können direkt `controller.RunOptimization()` aufrufen.

> **Achtung:** Das `Library.Go()`-Pattern ist etwas sperrig, weil es den Control-Flow invertiert (Callback statt direkter Aufruf). Das muss beim Umbau sorgfältig gelöst werden. Die `Initialize()`-Methode des Generators könnte statt selbst `Library.Go()` aufzurufen auch dem `Program.cs` signalisieren, **dass** PicoGK benötigt wird, und `Program.cs` entscheidet dann, ob es `Library.Go()` um den Workflow wickelt.

---

## RubberBandScaler & StlSmoother: Ein-/Ausschaltbar

Beide Utilities bleiben im Framework (`Core/Utilities/`), werden aber über `framework.json` gesteuert:

```csharp
// Im WorkflowController:
if (context.Framework.EnableRubberBandScaling)
{
    var scaler = new RubberBandScaler(
        activeParams,
        context.Project.DimensionalParameters,  // ← Nur Längen skalieren!
        context.Framework.TargetPicoGkSize);
    scaledParams = scaler.ShrunkParameters;
    record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;
}
else
{
    scaledParams = activeParams;
    record.PassiveParameters["ScaleFactor"] = 1.0f;
}

// Nach Geometrie-Erzeugung:
if (context.Framework.EnableStlSmoothing)
{
    StlSmoother.SmoothStl(
        stlPath, stlPath,
        context.Framework.StlSmoothingIterations,
        context.Framework.StlSmoothingPremeltingSteps);
}
```

### RubberBandScaler-Fix

Der Scaler nutzt jetzt `DimensionalParameters` aus der `ProjectConfig`, um nur echte mm-Längen zu skalieren:

```csharp
public RubberBandScaler(
    Dictionary<string, float> realParameters,
    HashSet<string> dimensionalParameters,  // {"Length", "Width", "MainRadius", "WingRadius"}
    float targetSize)
{
    // Nur dimensionale Parameter für die Max-Berechnung heranziehen
    float maxDimension = realParameters
        .Where(kvp => dimensionalParameters.Contains(kvp.Key))
        .Max(kvp => kvp.Value);

    ShrinkFactor = targetSize / maxDimension;

    // Alle kopieren, aber nur Längen skalieren
    ShrunkParameters = realParameters.ToDictionary(
        kvp => kvp.Key,
        kvp => dimensionalParameters.Contains(kvp.Key)
            ? kvp.Value * ShrinkFactor
            : kvp.Value);  // TailTaper bleibt 1.0!
}
```

---

## Pipeline im WorkflowController (finale Version)

```csharp
public class WorkflowController
{
    private readonly IGeometryGenerator _geometry;
    private readonly ISimulationSolver[] _solvers;      // Jeder Solver hat seinen Mesher
    private readonly IFitnessCalculator _fitness;
    private readonly IOptimizationAlgorithm _optimizer;
    private readonly SimulationContext _context;

    // Keine konkreten Klassen mehr — alles über Interfaces!
}
```

### Inner Loop (Pseudocode):

```
RunSingleVariant(params, iteration, variant):
    1. RubberBandScaler anwenden (falls aktiv)
    2. IGeometryGenerator.GenerateAndExport(...)     → GeometryResult
    3. Metriken aus GeometryResult in record.PassiveParameters übertragen
    4. Metriken mit Scaler zurückrechnen (Volume, Area) falls skaliert
    5. StlSmoother anwenden (falls aktiv)
    6. FÜR JEDEN Solver in _solvers:
         a. solver.Mesher.GenerateMesh(stlPath, ...)  → meshPath
         b. solver.Solve(meshPath, record, ...)       → schreibt in record.PassiveParameters
    7. return record
```

### Outer Loop:

```
RunOptimization():
    FOR iter = 1 to MaxIterations:
        FOR var = 1 to VariantsPerIteration:
            params = _optimizer.GenerateParameters(...)
            record = RunSingleVariant(params, iter, var)
            _fitness.CalculateFitness(record, ...)
            History.Add(record)
            ExportToCsv()

        champion = _optimizer.EvaluateAndSelectBest(records, ...)

        // BOUNCER: Kompletter Re-Run mit jitter (Framework-generisch!)
        validatedChampion = ValidateChampion(records, iter)

        _optimizer.PrepareNextIteration(...)
```

### Multi-Solver Beispiel

**Nur CFD (aktuelles Projekt):**
```csharp
var solvers = new ISimulationSolver[] {
    new Su2Solver(mesher: new GmshCfdMesher())
};
```

**CFD + FEM (zukünftig):**
```csharp
var solvers = new ISimulationSolver[] {
    new Su2Solver(mesher: new GmshCfdMesher()),
    new CalculixSolver(mesher: new GmshFemMesher())
};
// SU2 schreibt "Drag", "Lift" in PassiveParameters
// CalculiX schreibt "MaxStress", "Displacement" in PassiveParameters
// Fitness-Formel nutzt beides: z.B. Volume / (Drag * MaxStress)
```

**Nur FEM:**
```csharp
var solvers = new ISimulationSolver[] {
    new CalculixSolver(mesher: new GmshFemMesher())
};
```

---

## Program.cs: Composition Root

```csharp
static void Main(string[] args)
{
    // 1. Projektname aus CLI
    string projectName = args.Length > 0 ? args[0] : "MantaAuv";

    // 2. JSON-Konfigurationen laden
    var frameworkConfig = FrameworkConfig.LoadFromJson("Config/framework.json");
    var projectConfig = ProjectConfig.LoadFromJson($"Projects/{projectName}/project.json");

    // 3. Arbeitsverzeichnis vorbereiten
    string workDir = Path.Combine(Directory.GetCurrentDirectory(), "Ergebnisse");
    Directory.CreateDirectory(workDir);
    Directory.CreateDirectory(Path.Combine(workDir, "FehlerLogs"));
    Directory.CreateDirectory(Path.Combine(workDir, "Analyseergebnisse"));

    var context = new SimulationContext(frameworkConfig, projectConfig, workDir);

    // 4. Projekt-spezifische Logik zusammenbauen (C#-Code, keine JSON)
    var (geometry, fitness) = projectName switch
    {
        "MantaAuv" => (
            new MantaGeometryGenerator() as IGeometryGenerator,
            new MantaFitnessCalculator() as IFitnessCalculator
        ),
        _ => throw new ArgumentException($"Unbekanntes Projekt: {projectName}")
    };

    // 5. Solver-Pipeline zusammenbauen
    var cfdMesher = new GmshCfdMesher();
    var cfdSolver = new Su2Solver(cfdMesher);
    var solvers = new ISimulationSolver[] { cfdSolver };

    // 6. Optimierer wählen
    IOptimizationAlgorithm optimizer = new RsmOptimizationAlgorithm(fitness);

    // 7. Controller verdrahten
    var controller = new WorkflowController(
        geometry, solvers, fitness, optimizer, context);

    // 8. Starten — je nach Geometrie-Kernel
    geometry.Initialize(context);
    // MantaGeometryGenerator → startet Library.Go() und ruft controller.RunOptimization
    // Anderer Generator → ruft direkt controller.RunOptimization()
}
```

---

## Testbarkeit

| Komponente | Wie testen? |
|---|---|
| `MantaFitnessCalculator` | `ModelRecord` konstruieren, Fitness berechnen, Ergebnis prüfen. Kein I/O. |
| `EvolutionaryAlgorithm` | Mock-`IFitnessCalculator` injizieren, seeded `Random`, ohne PicoGK/SU2 |
| `RsmOptimizationAlgorithm` | Mock-`IFitnessCalculator`, Surrogatmodell mit bekannten Datenpunkten prüfen |
| `WorkflowController` | Alle Interfaces mocken → testet Orchestrierungs-Logik und Bouncer ohne reale Solver |
| `RubberBandScaler` | Rein mathematisch: prüfen dass `TailTaper` nicht skaliert wird, Volumen korrekt zurückgerechnet |
| `StlSmoother` | Kleine Test-STL-Datei, prüfen dass Bounding Box erhalten bleibt |
| `GmshCfdMesher` | Geo-Script-Generierung testen (String-Output prüfen), Process-Aufruf mocken |

Test-Setup: xUnit + Moq (oder NSubstitute), alles in einem `Tests/`-Ordner im Monorepo.

---

## Umsetzungs-Reihenfolge (5 Phasen)

### Phase 1: Interfaces & Datenmodelle anlegen
- `Core/Interfaces/`: alle 5 Interfaces anlegen
- `Core/Models/`: `GeometryResult`, `ProjectConfig`, `FrameworkConfig`, `SimulationContext`
- JSON-Schema für `project.json` und `framework.json`
- **Nichts geht kaputt** — bestehender Code läuft weiter

### Phase 2: Bestehende Klassen auf Interfaces umstellen
- `PicoGkGenerator` → `Projects/MantaAuv/MantaGeometryGenerator : IGeometryGenerator`
- `FitnessCalculator` → `Projects/MantaAuv/MantaFitnessCalculator : IFitnessCalculator`
- `FluidDynamicsAnalyzer` → `Solvers/Cfd/Su2Solver : ISimulationSolver`
- `GmshConverter` → `Solvers/Cfd/GmshCfdMesher : IMeshGenerator`
- `SimulationData` → `Core/Models/SimulationContext`
- AUV-Parameter nach `Projects/MantaAuv/project.json`
- Windkanal-Erzeugung von `PicoGkGenerator` nach `GmshCfdMesher`

### Phase 3: WorkflowController entkoppeln
- Konstruktor nimmt nur noch Interfaces
- `RunSingleVariant()` als wiederverwendbare Methode extrahieren
- Bouncer nutzt `RunSingleVariant()` + Fitness-Vergleich (generisch)
- RubberBandScaler und StlSmoother per Config ein-/ausschaltbar
- Hardcodierte Parameter-Keys raus aus dem Controller

### Phase 4: Program.cs als Composition Root
- JSON-Laden implementieren
- Projektauswahl via CLI-Argument
- `Library.Go()`-Lifecycle in den Generator verschieben
- Ordnerstruktur anlegen und Dateien verschieben

### Phase 5: Tests
- Test-Projekt anlegen (xUnit)
- Erste Tests: FitnessCalculator, RubberBandScaler, EA-Algorithmus
- Mock-basierte Tests: WorkflowController, Bouncer

---

## Was sich NICHT ändert

- **Optimierungsalgorithmen** (EA, RSM) bleiben im Kern — schon heute projektunabhängig (nur `IFitnessCalculator`-Injection kommt dazu)
- **StlSmoother** bleibt ein Utility — funktioniert für jede STL (wird nur ein-/ausschaltbar)
- **ModelRecord** bleibt das zentrale Datenmodell mit flexiblen `PassiveParameters`
- **CSV-Export** bleibt dynamisch wie bisher
- **Die Optimierungsschleife** bleibt strukturell gleich — nur die konkreten Aufrufe gehen durch Interfaces
- **Alles bleibt in einer `.csproj`** (Monorepo)

---

## Zusammenfassung

```
VORHER:
  Program → Library.Go() → WorkflowController
    → new PicoGkGenerator()    ← hardcoded Manta
    → new GmshConverter()      ← hardcoded SU2-Mesh
    → new FluidDynamicsAnalyzer() ← hardcoded SU2
    → static FitnessCalculator ← hardcoded Formel
    → Bouncer prüft "Drag"    ← hardcoded CFD

NACHHER:
  Program
    → lädt framework.json + project.json
    → switch(projektName) → verdrahtet C#-Logik-Klassen
    → WorkflowController(
        IGeometryGenerator,        ← Projekt liefert (C#-Code)
        ISimulationSolver[],       ← Solver liefern (jeweils mit eigenem IMeshGenerator)
        IFitnessCalculator,        ← Projekt liefert (C#-Code)
        IOptimizationAlgorithm,    ← Framework-Kern
        SimulationContext)         ← aus JSON geladen
    → Bouncer = fester Framework-Bestandteil, vergleicht Fitness generisch
```

**Neues Projekt anlegen =**
1. `Projects/MeinProjekt/project.json` schreiben (Parameter, Bounds, Pfade)
2. `MeinProjektGeometryGenerator.cs` schreiben (C#-Logik)
3. `MeinProjektFitnessCalculator.cs` schreiben (C#-Logik)
4. In `Program.cs` einen Case zum Switch hinzufügen (1 Zeile)
