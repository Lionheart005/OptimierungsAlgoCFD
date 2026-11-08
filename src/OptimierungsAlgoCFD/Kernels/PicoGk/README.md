# Handbuch: 3D-Voxel-Modellierung mit PicoGK (`Kernels/PicoGk`)

> **Offizielle PicoGK-Dokumentation & weiterführende Links:**
> - **Offizielle Website & Dokumentation:** [https://picogk.org/](https://picogk.org/)
> - **PicoGK C#-Repository (LEAP 71):** [https://github.com/leap71/PicoGK](https://github.com/leap71/PicoGK)
> - **PicoGK Native C++-Runtime:** [https://github.com/leap71/PicoGKRuntime](https://github.com/leap71/PicoGKRuntime)
> - **Lehrbuch *„Coding for Engineers"* (Lin Kayser / LEAP 71):** [https://leap71.com/coding-for-engineers/](https://leap71.com/coding-for-engineers/)
>
> **Verwandte Dokumentation in diesem Repository:**
> - **Neues 3D-/2D-Projekt anlegen:** [`src/projects/README.md`](../../../projects/README.md) (Vorlage: [`src/projects/_Vorlage3D/`](../../../projects/_Vorlage3D/README.md), Praxisbeispiel: [`src/projects/MantaAuv/`](../../../projects/MantaAuv/))
> - **Eigener 2D-Geometrie-Kernel (`Planar2D`):** [`src/OptimierungsAlgoCFD/Kernels/Planar2D/README.md`](../Planar2D/README.md)
> - **Voxel- & Glättungseinstellungen (`simulation.json`):** [`src/projects/BEDIENHANDBUCH.md` (Abschnitt 4.2)](../../../projects/BEDIENHANDBUCH.md#42-simulationjson--wie-der-lauf-abläuft)

Dieses Dokument erklärt, wie in diesem Framework dreidimensionale Bauteile mit **PicoGK** in C# erzeugt, organisch verschliffen, vermessen, geglättet und für die CFD-Simulation exportiert werden.

---

## 1. Warum Voxel statt klassischem CAD (B-Rep)?

In klassischen CAD-Kerneln (NURBS / B-Rep) scheitern automatisierte Optimierungsschleifen häufig daran, dass sich Flächen bei extremen Parameterkombinationen selbst durchdringen oder Verrundungen (Fillets) mit einer Fehlermeldung abbrechen.

**PicoGK** arbeitet stattdessen mit **Voxeln** (dreidimensionalen Volumenelementen / Signed Distance Fields auf Basis von OpenVDB):
- **Unzerstörbare Boolesche Operationen:** Hunderte Streben, Rumpfsegmente und Flügel können sich beliebig durchdringen — die Vereinigung (`BoolAdd`), Differenz (`BoolSubtract`) oder Schnittmenge (`BoolIntersect`) gelingt **immer** fehlerfrei.
- **Organisches Verschmelzen (`Offset`):** Durch Aufblasen um $+r$ und anschließendes Schrumpfen um $-r$ entstehen vollautomatisch weiche, hydrodynamisch günstige Übergänge (Fillets) zwischen allen sich treffenden Bauteilen.

---

## 2. Wie PicoGK im Framework eingebunden ist

Als Entwickler eines Bauteilprojekts musst du dich **nicht** um `Library.Go(...)`, Viewer-Fenster oder OpenGL-Kontexte kümmern:
1. Deine Projektklasse erbt von [`CfdProjectDefinition`](../../Composition/CfdProjectDefinition.cs).
2. Diese nutzt automatisch [`PicoGkKernel`](PicoGkKernel.cs), der `Library.Go(voxelResolution, ..., bEndAppWithTask: true)` mit der aus `simulation.json` geladenen Voxelgröße (`BaseVoxelResolution`, Standard `0.5` mm) startet.
3. Auf dem Linux-Server sorgt `scripts/sim-runner.sh` automatisch für das virtuelle Display (`xvfb-run -a`) und die native Bibliothek (`libpicogk.so` / `libpicogk.26.2.so`).

---

## 3. Die 4 Grundbausteine beim Modellieren in PicoGK

Der typische Aufbau in deinem `*GeometryGenerator.cs` folgt immer der Kette **`Lattice` (oder Grund-`Mesh`) → `Voxels` → `Mesh` → `.stl`**:

### Baustein 1: `Lattice` (Skelett aus Balken und Kugeln)
Ein `Lattice` beschreibt ein Drahtgitter aus Kugeln und konischen Stäben (Beams), das anschließend in ein Voxelvolumen umgewandelt wird:

```csharp
using System.Numerics;
using PicoGK;

Lattice lat = new Lattice();

// 1. Konischer oder zylindrischer Balken von Punkt A (Radius r1) nach Punkt B (Radius r2):
//    Der letzte Parameter (true = runde Endkappen / Kugelkappen, false = flache Enden)
Vector3 nose = new Vector3(0f, 0f, 0f);
Vector3 tail = new Vector3(50f, 0f, 0f);
lat.AddBeam(nose, tail, 8.0f, 2.0f, true);

// 2. Einzelne Kugel hinzufügen:
lat.AddSphere(new Vector3(10f, 0f, -6f), 3.0f);
```

> **Tipp aus [`MantaGeometryGenerator.cs`](../../../projects/MantaAuv/MantaGeometryGenerator.cs):**  
> Flügel oder Membranflächen lassen sich elegant als Fächer (Fin-Ray-Struktur) aus vielen eng nebeneinanderliegenden `AddBeam`-Aufrufen erzeugen, die anschließend im Voxelraum über `Offset` zu einer geschlossenen glatten Fläche verschmolzen werden!

### Baustein 2: `Mesh` & `Utils` (Dreiecksnetze & Grundkörper)
Neben `Lattice` können auch direkt Dreiecksnetze erzeugt oder geladen und in Voxel umgewandelt werden:

```csharp
// Quader mit Kantenlängen (length, width, height), zentriert im Ursprung:
Mesh boxMesh = Utils.mshCreateCube(new Vector3(length, width, height), Vector3.Zero);
```

### Baustein 3: `Voxels` (Boolesche Algebra & Organische Verrundung)
Sobald ein `Lattice` oder `Mesh` in `Voxels` überführt ist, stehen alle Volumenoperationen zur Verfügung:

```csharp
// Aus Lattice oder Mesh ein Voxel-Volumen erzeugen:
Voxels voxBody = new Voxels(lat);
Voxels voxCutout = new Voxels(boxMesh);

// Boolesche Operationen:
voxBody.BoolAdd(otherVoxels);       // Vereinigung (Union)
voxBody.BoolSubtract(voxCutout);    // Abzug (Difference)
voxBody.BoolIntersect(boundVoxels); // Schnittmenge (Intersection)

// Organisches Verschmelzen / Kantenverrundung (Filleting):
// 1. Nach außen wachsen lassen (+blendRadius) -> füllt Kehlen und Spalte auf
// 2. Wieder um denselben Betrag schrumpfen (-blendRadius) -> Originalmaße bleiben erhalten, Innenkanten sind rund!
float blendRadius = 4.0f;
voxBody.Offset(blendRadius);
voxBody.Offset(-blendRadius);

// Oder in einem Aufruf:
// voxBody.DoubleOffset(blendRadius, -blendRadius);
// voxBody.TripleOffset(blendRadius); // (+r, -2r, +r) verrundet Innen- UND Außenkanten
```

### Baustein 4: Export als `Mesh` & Oberflächenglättung (`StlSmoother`)
Aus dem fertigen `Voxels`-Objekt zieht PicoGK per Marching Cubes ein Dreiecksnetz (`Mesh`). Da Voxeloberflächen feine Treppenstufen aufweisen, glättet der Framework-Baustein [`StlSmoother`](../../Core/Utilities/StlSmoother.cs) das exportierte STL-Netz anschließend formerhaltend (Taubin-/Laplace-Glättung):

```csharp
Mesh exportModel = new Mesh(voxBody);
string stlPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
exportModel.SaveToStlFile(stlPath);

if (config.UseStlSmoothing)
{
    StlSmoother.SmoothStl(
        stlPath, stlPath,
        config.VoxelSmoothingIterations,
        config.VoxelSmoothingPremeltingSteps);
}
```

---

## 4. Geometrie vermessen & Metriken zurückgeben

Für die Zielfunktion (`IFitnessCalculator`) und für die Bezugsfläche in SU2 (`ReferenceAreaMetric` in `solvers/su2.json`) stehen zwei Messwege bereit:

### Option A: `PicoGkMeshMetrics.Measure(exportModel)` (Empfohlen für neue Projekte)
[`PicoGkMeshMetrics.Measure`](PicoGkMeshMetrics.cs) vermisst das erzeugte `Mesh` in einem Durchgang und liefert zusätzlich die **Oberfläche (`SurfaceArea`)**, die PicoGKs eigenes `CalculateProperties` nicht berechnet:

```csharp
using OptimierungsAlgoCFD.Kernels.PicoGk;

MeshMeasurement m = PicoGkMeshMetrics.Measure(exportModel);
// m.Volume          -> Volumen in mm³ (Betrag)
// m.SignedVolume    -> Vorzeichenbehaftetes Volumen (< 0 warnt vor invertierten Normalen)
// m.SurfaceArea     -> Gesamte Bauteiloberfläche in mm²
// m.FrontalBoxArea  -> Stirnfläche der Bounding-Box quer zur X-Anströmung (Size.Y × Size.Z) in mm²
// m.Bounds          -> BBox (Min, Max, Size)

var result = new GeometryResult { StlPath = stlPath };
result.AddMetric("Volume", m.Volume, MetricScaling.Volume);
result.AddMetric("SurfaceArea", m.SurfaceArea, MetricScaling.Area);
result.AddMetric("FrontalArea", m.FrontalBoxArea, MetricScaling.Area);
result.AddMetric("Span", m.Bounds.Size.Y, MetricScaling.Linear);
```

### Option B: `voxBody.CalculateProperties(out float volume, out BBox3 bbox)`
Direkte Messung auf dem Voxelfeld (wird u. a. in `MantaAuv` genutzt, um bitgenau kompatibel zu historischen Läufen zu bleiben):

```csharp
voxBody.CalculateProperties(out float modelVolume, out BBox3 boundingBox);
float frontalArea = (boundingBox.vecMax.Y - boundingBox.vecMin.Y)
                  * (boundingBox.vecMax.Z - boundingBox.vecMin.Z);
```

---

## 5. Wichtige Regeln für `RubberBandScaler` & Voxelauflösung

1. **Immer die übergebenen `parameters` verwenden:**  
   Ist `UseRubberBandScaler = true`, skaliert der `WorkflowController` alle in `DimensionalParameters` (`project.json`) gelisteten Längenparameter vor dem Aufruf von `GenerateAndExport` auf `TargetPicoGkSize` (Standard `50` mm) herunter, damit jedes Modell unabhängig von seiner absoluten Größe mit derselben relativen Voxel-Feinheit aufgelöst wird.
2. **Jede Metrik mit dem passenden `MetricScaling` anmelden:**  
   Nach `GenerateAndExport` rechnet der Controller alle gemeldeten Metriken automatisch auf die Originalgröße zurück:
   - `MetricScaling.Volume` ($\times s^3$) für Volumina (`Volume`),
   - `MetricScaling.Area` ($\times s^2$) für Flächen (`FrontalArea`, `SurfaceArea`, Kreuzprodukte),
   - `MetricScaling.Linear` ($\times s^1$) für Längen (`Span`, `Chord`),
   - `MetricScaling.None` ($\times 1$) für dimensionslose Verhältnisse oder Winkel.
3. **Mindestwandstärke beachten:**  
   Bei einer Voxelgröße von `BaseVoxelResolution = 0.5` mm sollten tragende Strukturen und Flügelkanten in der skalierten Arbeitsgröße mindestens ~`1.0` bis `1.5` mm dick sein (mindestens 2–3 Voxel), damit dünne Häute beim Voxeln nicht aufreißen.
