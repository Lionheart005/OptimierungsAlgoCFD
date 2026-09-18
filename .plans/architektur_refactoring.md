# Architektur-Refactoring: Projektbasiertes Plugin-System

## Ziel

Das Framework soll so umgebaut werden, dass **verschiedene Projekte** (z.B. AUV-Manta, Drohnen-Propeller, Wärmetauscher) jeweils eigene Geometrie-Erzeugung, Fitness-Funktion, Solver-Konfiguration und Parameter mitbringen können — **ohne das Hauptprogramm zu verändern**. Gleichzeitig soll der Code testbar (Unit Tests) und nach Clean-Architecture-Prinzipien aufgebaut sein, ohne unnötig aufgebläht zu werden.

---

## Status Quo: Probleme

### Harte Kopplungen (die wir aufbrechen müssen)

| Problem | Wo genau | Warum kritisch |
|---|---|---|
| **Geometrie ist Manta-Ray-hardcoded** | `PicoGkGenerator.cs` L29-137: 30 Fin-Rays, 3 Sensorblisters, Torpedo-Rumpf | Neues Projekt = kompletter Rewrite dieser Datei |
| **Fitness-Formel ist hardcoded** | `FitnessCalculator.cs` (static class): `Volume * SensorDistance / Drag³` | Jedes Projekt braucht eine andere Zielfunktion |
| **SU2-Konfiguration ist hardcoded** | `FluidDynamicsAnalyzer.cs` L109-168: Spalart-Allmaras, Wasser ρ=1025 | Kein anderer Solver oder Fluid möglich |
| **Gmsh-Geo ist hardcoded** | `GmshConverter.cs` L21-103: Boolean-Subtraktion, SU2-Format, Boundary-Marker | Kein FEM-Netz, kein anderes Format möglich |
| **AUV-Parameter im Konstruktor** | `SimulationData.cs` L104-122: Length, Width, MainRadius, WingRadius, TailTaper | Neues Projekt = Konstruktor umschreiben |
| **WorkflowController kennt konkrete Klassen** | `WorkflowController.cs` L20-22: `new PicoGkGenerator()`, `new GmshConverter()`, `new FluidDynamicsAnalyzer()` | Kein Austausch möglich |
| **FitnessCalculator ist `static`** | `EvolutionaryAlgorithm.cs` L74, `RsmOptimizationAlgorithm.cs` L45 | Nicht mockbar, nicht austauschbar |
| **Validation ("Bouncer") nimmt `Drag` an** | `WorkflowController.cs` L159: `candidate.PassiveParameters["Drag"]` | Funktioniert nur für CFD-Drag |
| **Windkanal in der Geometrie-Klasse** | `PicoGkGenerator.cs` L13-27: 600×300×300 mm Box | Gehört zum CFD-Setup, nicht zur Geometrie |
| **File-System-Effekte im Konstruktor** | `SimulationData.cs` L98-101: `Directory.CreateDirectory(...)` | Unit Tests erzeugen Ordner auf der Platte |

---

## Kern-Idee: Projektordner-Architektur

```
AutomatisierungCleanVersion/
│
├── Core/                          ← Framework-Kern (ändert sich NIE pro Projekt)
│   ├── Interfaces/                ← Verträge / Abstraktionen
│   │   ├── IGeometryGenerator.cs
│   │   ├── IFitnessCalculator.cs
│   │   ├── ISimulationSolver.cs
│   │   ├── IMeshGenerator.cs
│   │   ├── IModelValidator.cs
│   │   └── IOptimizationAlgorithm.cs  (existiert schon, wird leicht angepasst)
│   │
│   ├── Models/                    ← Datenklassen (Projekt-unabhängig)
│   │   ├── ModelRecord.cs
│   │   ├── SimulationConfig.cs    (ehemals GlobalConfig, OHNE AUV-Parameter)
│   │   ├── SimulationContext.cs   (ehemals SimulationData, OHNE Projekt-Logik)
│   │   └── GeometryResult.cs     (ersetzt das feste Tuple)
│   │
│   ├── Pipeline/                  ← Orchestrierung
│   │   └── WorkflowController.cs  (arbeitet NUR mit Interfaces)
│   │
│   ├── Algorithms/                ← Optimierungsalgorithmen (Projekt-unabhängig)
│   │   ├── EvolutionaryAlgorithm.cs
│   │   └── RsmOptimizationAlgorithm.cs
│   │
│   └── Utilities/                 ← Allgemeine Helfer
│       ├── StlSmoother.cs
│       └── RubberBandScaler.cs
│
├── Projects/                      ← HIER lebt die Projektspezifik
│   ├── MantaAuv/                  ← Das aktuelle AUV-Projekt
│   │   ├── MantaGeometryGenerator.cs     (= heutiger PicoGkGenerator)
│   │   ├── MantaFitnessCalculator.cs     (= heutiger FitnessCalculator)
│   │   ├── MantaProjectConfig.cs         (Parameter, Bounds, Deviations)
│   │   └── MantaModelValidator.cs        (= heutiger Bouncer mit Drag-Check)
│   │
│   └── NeuesProjekt/              ← Zukünftiges Projekt (Beispiel)
│       ├── XyzGeometryGenerator.cs
│       ├── XyzFitnessCalculator.cs
│       ├── XyzProjectConfig.cs
│       └── XyzModelValidator.cs
│
├── Solvers/                       ← Austauschbare Solver-Implementierungen
│   ├── Cfd/
│   │   ├── Su2Solver.cs           (= heutiger FluidDynamicsAnalyzer)
│   │   ├── Su2ConfigGenerator.cs  (extrahiert aus FluidDynamicsAnalyzer)
│   │   └── GmshCfdMesher.cs      (= heutiger GmshConverter, CFD-Variante)
│   │
│   └── Fem/                       ← Zukünftig: FEM-Pipeline
│       ├── CalculixSolver.cs
│       └── GmshFemMesher.cs
│
├── Tests/                         ← Unit Tests (eigenes Projekt, separates .csproj)
│   ├── FitnessCalculatorTests.cs
│   ├── EvolutionaryAlgorithmTests.cs
│   ├── RubberBandScalerTests.cs
│   └── WorkflowControllerTests.cs
│
├── Program.cs                     ← Einstiegspunkt: Projekt auswählen & verdrahten
└── Automatisierung_v2.csproj
```

---

## Die 6 neuen Interfaces

### 1. `IGeometryGenerator`

```csharp
public interface IGeometryGenerator
{
    /// Erzeugt die 3D-Geometrie und gibt eine STL + Metriken zurück.
    GeometryResult GenerateAndExport(
        int iteration, int variant,
        Dictionary<string, float> parameters,
        string outputDirectory,
        float voxelSmoothingIterations,
        int smoothingPremeltingSteps);
}
```

**Was ändert sich?**
- `PicoGkGenerator` wird zu `MantaGeometryGenerator` und implementiert dieses Interface
- Das feste Return-Tuple `(StlPath, Volume, FrontalArea, SensorDistance)` wird zu `GeometryResult` mit flexiblem `Dictionary<string, float> Metrics` — damit jedes Projekt seine eigenen Metriken (SensorDistance, Oberfläche, Wandstärke, etc.) zurückgeben kann
- `GenerateStaticTunnel()` wird **raus** aus dem Geometry-Generator → wandert in den CFD-Mesher

### 2. `IFitnessCalculator`

```csharp
public interface IFitnessCalculator
{
    void CalculateFitness(ModelRecord record, SimulationConfig config);
}
```

**Was ändert sich?**
- `FitnessCalculator` wird von `static class` zur normalen Klasse `MantaFitnessCalculator : IFitnessCalculator`
- Optimierer bekommen ein `IFitnessCalculator` per Konstruktor injiziert statt static-Aufruf
- Jedes Projekt definiert seine eigene Formel

### 3. `ISimulationSolver`

```csharp
public interface ISimulationSolver
{
    string Name { get; }

    /// Führt eine Simulation auf dem Mesh aus und schreibt Ergebnisse in den Record.
    void Solve(
        string meshPath,
        ModelRecord record,
        SimulationConfig config,
        string workingDirectory);
}
```

**Was ändert sich?**
- `FluidDynamicsAnalyzer` wird zu `Su2Solver : ISimulationSolver`
- Return-Wert ist nicht mehr nur ein einzelner `float drag`, sondern der Solver schreibt seine Ergebnisse direkt in `record.PassiveParameters` (z.B. `"Drag"`, `"Lift"`, `"Pressure"`, oder bei FEM: `"MaxStress"`, `"Displacement"`)
- Der `WorkflowController` kann eine **Liste** von Solvern durchlaufen: erst CFD, dann FEM — oder nur eines davon

### 4. `IMeshGenerator`

```csharp
public interface IMeshGenerator
{
    string GenerateMesh(
        string modelStlPath,
        int iteration, int variant,
        SimulationConfig config,
        float scaleFactor,
        string workingDirectory);
}
```

**Was ändert sich?**
- `GmshConverter` wird zu `GmshCfdMesher : IMeshGenerator`
- Der Windkanal (`GenerateStaticTunnel`) wird Teil des CFD-Meshers — er gehört zur Simulationsdomäne, nicht zur Geometrie
- Ein zukünftiger `GmshFemMesher` kann das Solid-Volumen vernetzen statt den Fluidraum
- Mesh-Format (SU2, OpenFOAM, Abaqus .inp) wird pro Solver konfigurierbar

### 5. `IModelValidator`

```csharp
public interface IModelValidator
{
    /// Prüft, ob ein Champion physikalisch stabil ist.
    /// Gibt den validierten (oder disqualifizierten) Champion zurück.
    ModelRecord ValidateChampion(
        List<ModelRecord> candidates,
        SimulationConfig config,
        // Callback für Re-Simulation:
        Func<Dictionary<string, float>, ModelRecord> resimulate);
}
```

**Was ändert sich?**
- Der "Bouncer" wird aus dem `WorkflowController` extrahiert
- `MantaModelValidator` prüft Drag-Stabilität (wie bisher)
- Ein anderes Projekt könnte Stress-Stabilität oder gar nichts prüfen
- Der `WorkflowController` kennt den Validierungsmetrik-Namen nicht mehr

### 6. `IOptimizationAlgorithm` (Anpassung)

```csharp
public interface IOptimizationAlgorithm
{
    string Name { get; }
    Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants);
    ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context);
    void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord);
}
```

**Was ändert sich?**
- `SimulationData` → `SimulationContext` (entkoppelt von konkreten AUV-Parametern)
- `IFitnessCalculator` wird per Konstruktor in die Algorithmen injiziert

---

## Datenmodell-Änderungen

### `GeometryResult` (NEU)

```csharp
public class GeometryResult
{
    public string StlPath { get; set; } = "";
    public Dictionary<string, float> Metrics { get; set; } = new();
    // z.B. {"Volume": 4200, "FrontalArea": 850, "SensorDistance": 120}
    // oder {"Volume": 3000, "SurfaceArea": 1200, "WallThickness": 2.5}
}
```

Ersetzt das starre Tuple `(string StlPath, float Volume, float FrontalArea, float SensorDistance)`. Jedes Projekt gibt die Metriken zurück, die es braucht.

### `SimulationContext` (ehemals `SimulationData`)

```csharp
public class SimulationContext
{
    public SimulationConfig Config { get; set; }       // Allgemeine Einstellungen
    public ProjectConfig Project { get; set; }         // Projekt-spezifisch (Parameter, Bounds)
    public string WorkingDirectory { get; set; }
    public List<ModelRecord> History { get; set; } = new();

    public void ExportToCsv() { ... }  // Bleibt, aber Directory-Erstellung wird rausgezogen
}
```

### `ProjectConfig` (NEU — ersetzt den hardcodierten Konstruktor)

```csharp
public class ProjectConfig
{
    public string ProjectName { get; set; } = "";
    public Dictionary<string, float> BaseParameters { get; set; } = new();
    public Dictionary<string, float> MaxDeviations { get; set; } = new();
    public Dictionary<string, (float Min, float Max)> ParameterBounds { get; set; } = new();
    public Dictionary<string, float> OptimizationTargets { get; set; } = new();
}
```

Jedes Projekt füllt diese Klasse — entweder im Code (`MantaProjectConfig.cs`) oder perspektivisch aus einer JSON-Datei.

---

## Pipeline-Änderung im WorkflowController

### Vorher (fest verdrahtet):

```
Geometrie (PicoGK) → Gmsh (CFD-Mesh) → SU2 (CFD) → Fitness
```

### Nachher (flexibel):

```csharp
// WorkflowController arbeitet nur noch mit Interfaces:
public class WorkflowController
{
    private readonly IGeometryGenerator _geometry;
    private readonly IMeshGenerator _mesher;
    private readonly ISimulationSolver[] _solvers;   // ← Mehrere Solver möglich!
    private readonly IFitnessCalculator _fitness;
    private readonly IModelValidator _validator;
    private readonly IOptimizationAlgorithm _optimizer;

    public WorkflowController(
        IGeometryGenerator geometry,
        IMeshGenerator mesher,
        ISimulationSolver[] solvers,      // CFD, FEM, oder beides
        IFitnessCalculator fitness,
        IModelValidator validator,
        IOptimizationAlgorithm optimizer,
        SimulationContext context) { ... }
}
```

**Damit kann der inner Loop so aussehen:**

```
1. Parameter generieren (Optimizer)
2. Skalieren (RubberBandScaler)
3. Geometrie erzeugen (IGeometryGenerator)  → GeometryResult
4. Mesh generieren (IMeshGenerator)
5. FÜR JEDEN Solver in _solvers:            ← NEU: Solver-Kette
     solver.Solve(meshPath, record, ...)
6. Fitness berechnen (IFitnessCalculator)
7. Speichern
```

Ein reines CFD-Projekt hat `_solvers = [su2Solver]`.
Ein CFD+FEM-Projekt hat `_solvers = [su2Solver, calculixSolver]` — wobei jeder seine eigenen PassiveParameters schreibt.

> **Offene Frage:** Braucht ein FEM-Solver ein anderes Mesh als der CFD-Solver? Falls ja, brauchen wir eine `IMeshGenerator[]` analog zu `ISimulationSolver[]`, oder eine Zuordnung Solver↔Mesher. Für den Anfang schlage ich vor, dass jeder `ISimulationSolver` bei Bedarf seinen eigenen Mesher mitbringen kann (z.B. als Property oder über eine Factory).

---

## Program.cs: Projektauswahl & Verdrahtung

```csharp
static void Main(string[] args)
{
    // 1. Projekt aus args oder Konvention auswählen
    string projectName = args.Length > 0 ? args[0] : "MantaAuv";

    // 2. Projekt-spezifische Komponenten zusammenbauen
    var (geometry, fitness, validator, projectConfig) = projectName switch
    {
        "MantaAuv" => (
            new MantaGeometryGenerator() as IGeometryGenerator,
            new MantaFitnessCalculator() as IFitnessCalculator,
            new MantaModelValidator() as IModelValidator,
            MantaProjectConfig.Create()
        ),
        _ => throw new ArgumentException($"Unbekanntes Projekt: {projectName}")
    };

    // 3. Framework-Kern (identisch für alle Projekte)
    var config = SimulationConfig.CreateDefault();
    var context = new SimulationContext(config, projectConfig);
    var mesher = new GmshCfdMesher();
    var solver = new Su2Solver();
    var optimizer = new RsmOptimizationAlgorithm(fitness);

    var controller = new WorkflowController(
        geometry, mesher, new[] { solver }, fitness, validator, optimizer, context);

    Library.Go(config.BaseVoxelResolution, controller.RunOptimization);
}
```

> **Perspektivisch** könnte man das `switch` durch Reflection oder eine JSON-Konfiguration ersetzen — aber für 2-5 Projekte ist ein simpler switch ausreichend und nicht über-engineered.

---

## Testbarkeit

### Was wird durch die Interfaces testbar?

| Komponente | Wie testen? |
|---|---|
| `EvolutionaryAlgorithm` | Mock-`IFitnessCalculator` injizieren, seeded `Random`, ohne PicoGK/SU2 |
| `RsmOptimizationAlgorithm` | Mock-`IFitnessCalculator`, Surrogatmodell mit bekannten Datenpunkten prüfen |
| `MantaFitnessCalculator` | Direkt mit konstruiertem `ModelRecord` testen, keine I/O |
| `WorkflowController` | Alle Interfaces mocken → testet Orchestrierungs-Logik ohne Solver/Geometrie |
| `RubberBandScaler` | Rein mathematisch, schon heute testbar |
| `StlSmoother` | Mit kleiner Test-STL-Datei, prüfen dass Bounding Box erhalten bleibt |
| `MantaModelValidator` | Mock-Resimulation-Callback, prüft Disqualifizierung |

### Test-Projekt-Setup

```
AutomatisierungCleanVersion.Tests/        ← Separates .csproj
├── AutomatisierungCleanVersion.Tests.csproj
│   (referenziert Automatisierung_v2.csproj, xUnit, Moq oder NSubstitute)
├── FitnessCalculatorTests.cs
├── EvolutionaryAlgorithmTests.cs
├── RubberBandScalerTests.cs
└── WorkflowControllerTests.cs
```

---

## RubberBandScaler: Bekannter Bug

Der aktuelle Scaler skaliert **alle** Parameter-Werte blind — auch dimensionslose wie `TailTaper`. Das verfälscht das Ergebnis:

```
TailTaper = 1.0  →  * ShrinkFactor  →  0.02  (Unsinn!)
```

**Lösung:** Der Scaler bekommt eine explizite Liste, welche Parameter Längen-Dimensionen sind und skaliert werden sollen:

```csharp
public RubberBandScaler(
    Dictionary<string, float> realParameters,
    HashSet<string> dimensionalParameters,  // z.B. {"Length", "Width", "MainRadius", "WingRadius"}
    float targetSize)
```

Diese Info kommt aus der `ProjectConfig`, weil nur das Projekt weiß, welche Parameter Millimeter sind.

---

## Umsetzungs-Reihenfolge (5 Phasen)

### Phase 1: Interfaces definieren & Datenmodell aufräumen
- `IGeometryGenerator`, `IFitnessCalculator`, `ISimulationSolver`, `IMeshGenerator`, `IModelValidator` anlegen
- `GeometryResult`, `ProjectConfig`, `SimulationConfig` anlegen
- `SimulationData` → `SimulationContext` umbauen (AUV-Parameter raus)
- **Nichts geht kaputt** — die alten Klassen existieren noch

### Phase 2: Bestehende Klassen auf Interfaces umstellen
- `PicoGkGenerator` → `MantaGeometryGenerator : IGeometryGenerator`
- `FitnessCalculator` → `MantaFitnessCalculator : IFitnessCalculator`
- `FluidDynamicsAnalyzer` → `Su2Solver : ISimulationSolver`
- `GmshConverter` → `GmshCfdMesher : IMeshGenerator`
- `ValidateChampion()` → `MantaModelValidator : IModelValidator`
- Alles nach `Projects/MantaAuv/` und `Solvers/Cfd/` verschieben

### Phase 3: WorkflowController entkoppeln
- Konstruktor nimmt nur noch Interfaces
- `new PicoGkGenerator()`, `new GmshConverter()`, `new FluidDynamicsAnalyzer()` raus
- Bouncer-Logik durch `IModelValidator` ersetzen
- Hardcodierte Parameter-Keys (`"Drag"`, `"Volume"`, `"SensorDistance"`) aus dem Controller entfernen

### Phase 4: Program.cs als Composition Root
- Projektauswahl via CLI-Argument
- Verdrahtung der konkreten Implementierungen
- Ordnerstruktur anlegen

### Phase 5: Tests & FEM-Vorbereitung
- Test-Projekt mit xUnit anlegen
- Erste Tests für FitnessCalculator, Scaler, Algorithmen
- `ISimulationSolver[]` Array im Controller vorbereiten für Mehrfach-Solver

---

## Offene Fragen

> [!IMPORTANT]
> **1. Solver-Mesh-Zuordnung:** Wenn ein FEM-Solver ein anderes Mesh braucht als der CFD-Solver — soll jeder Solver seinen eigenen Mesher mitbringen, oder soll der WorkflowController ein Mesh-pro-Solver-Mapping haben?
>
> **2. PicoGK-Abhängigkeit:** Soll `Library.Go()` weiterhin den gesamten Programmablauf umschließen? Wenn ja, sind Projekte ohne PicoGK-Geometrie (z.B. reine Mesh-Optimierung) schwierig. Wollen wir das erstmal so lassen?
>
> **3. Konfigurationsformat:** Sollen die Projektparameter vorerst im C#-Code bleiben (z.B. `MantaProjectConfig.Create()`) oder direkt als JSON/YAML-Dateien im Projektordner? Code ist einfacher, JSON ist flexibler für Nicht-Programmierer.
>
> **4. RubberBandScaler Scope:** Soll der Scaler Teil des Frameworks bleiben (in `Core/Utilities/`), oder soll jedes Projekt seine eigene Skalierungs-Strategie mitbringen können (`IModelScaler`)?
>
> **5. Monorepo vs. Multi-Projekt:** Soll alles in einer `.csproj` bleiben (einfacher), oder sollen `Core`, `Tests` und Projekte separate `.csproj`-Dateien bekommen (sauberer Dependency-Graph)?

---

## Was sich NICHT ändert

- **Optimierungsalgorithmen** (EA, RSM) bleiben im Kern — sie sind schon heute projektunabhängig
- **StlSmoother** bleibt ein Utility — funktioniert für jede STL
- **ModelRecord** bleibt das zentrale Datenmodell — nur mit flexibleren `PassiveParameters`
- **CSV-Export** bleibt in `SimulationContext` — ist schon dynamisch
- **Die gesamte Optimierungsschleife** im WorkflowController bleibt strukturell gleich — nur die konkreten Aufrufe gehen durch Interfaces

---

## Zusammenfassung

```
VORHER:  Program → WorkflowController → [PicoGkGenerator, GmshConverter, SU2, FitnessCalc]
                                          ↑ alles hardcoded, alles Manta-Ray

NACHHER: Program → wählt Projekt → verdrahtet Interfaces → WorkflowController
                                                              ↓
                                                    [IGeometryGenerator]  ← Projekt liefert
                                                    [IMeshGenerator]      ← Solver liefert
                                                    [ISimulationSolver]   ← Solver liefert
                                                    [IFitnessCalculator]  ← Projekt liefert
                                                    [IModelValidator]     ← Projekt liefert
```

**Ergebnis:** Neues Projekt anlegen = 3-4 Dateien in `Projects/MeinProjekt/` schreiben. Framework-Code bleibt unberührt.

