# Handbuch: Eigener 2D-Geometrie-Kernel (`Planar2D`)

> **Verwandte Dokumentation:**
> - **Neues 2D-/3D-Projekt anlegen:** [`src/projects/README.md`](../../../projects/README.md) (Vorlage: [`src/projects/_Vorlage2D/`](../../../projects/_Vorlage2D/README.md), Beispiel: [`src/projects/Naca2D/`](../../../projects/Naca2D/))
> - **3D-Geometrie mit PicoGK:** [`src/Automatisierung_v2/Kernels/PicoGk/README.md`](../PicoGk/README.md)
> - **Alle 2D-Vernetzungs- & Solver-Einstellungen:** [`src/projects/BEDIENHANDBUCH.md` (Abschnitt 4.5)](../../../projects/BEDIENHANDBUCH.md#45-solversgmshjson-2d-projekte-gmsh2dmesheroptions)

Der `Planar2D`-Kernel (`MyPicoGkProject.Kernels.Planar2D`) ist ein eigens für dieses Framework geschriebener, analytischer 2D-Geometriekern auf Basis von **Clipper2**.

Er wurde bewusst so entworfen, dass er sich beim Programmieren **genauso anfühlt wie PicoGK in 3D** (`Lattice2D` statt `Lattice`, `Shape2D` statt `Voxels`, `BBox2` statt `BBox3`, `BoolAdd`, `BoolSubtract`, `Offset`, `TripleOffset`), dabei aber auf exakten ebenen Vektorkonturen arbeitet — **komplett ohne Voxel-Treppenstufen, ohne OpenGL/Xvfb und in Bruchteilen einer Millisekunde**.

---

## 1. Überblick der Klassen

| Klasse / Struct | Namespace | Analogon in PicoGK (3D) | Aufgabe |
| --- | --- | --- | --- |
| [`Shape2D`](Shape2D.cs) | `MyPicoGkProject.Kernels.Planar2D` | `Voxels` + `Mesh` | Repräsentiert eine 2D-Form (beliebig viele geschlossene Außenkonturen und innere Löcher). Bietet Grundformen, Splines, Boolesche Operationen, Offset-Verrundungen, Knickpunkterkennung sowie JSON- und SVG-Export. |
| [`Lattice2D`](Lattice2D.cs) | `MyPicoGkProject.Kernels.Planar2D` | `Lattice` | Zweidimensionales Balken-/Skelettgitter aus Kreisknoten (`AddCircle`) und tangentialen Konus-/Kapselbalken (`AddBeam`). |
| [`BBox2`](BBox2.cs) | `MyPicoGkProject.Core` | `BBox3` | Achsenparallele 2D-Bounding-Box (`Min`, `Max`, `Size`, `Center`, `Include`, `Contains`). |
| [`MeshMetrics2D`](../../Core/Utilities/MeshMetrics2D.cs) / [`ContourMeasurement2D`](../../Core/Utilities/MeshMetrics2D.cs) | `MyPicoGkProject.Core` | `PicoGkMeshMetrics` / `MeshMeasurement` | Berechnet Nettofläche (Gaußsche Trapezformel, Löcher werden automatisch abgezogen), Gesamtumfang, Sehnenlänge (`Chord` in X), Dicke (`Thickness` in Y) und Punktanzahl. |
| [`DirectGeometryKernel`](../../Core/Interfaces/IGeometryKernel.cs) | `MyPicoGkProject.Core` | `PicoGkKernel` | Führt den 2D-Generator direkt ohne native C++-Bibliothek und ohne Grafikserver (`xvfb-run`) aus. |

---

## 2. Koordinatensystem & Einheiten

- **Einheit:** Alle Koordinaten, Radien und Abstände sind in **Millimetern (mm)** angegeben.
- **Anströmrichtung:** Die Strömung in Gmsh/SU2 läuft standardmäßig in **+X-Richtung** (von links nach rechts; der Anstellwinkel `AngleOfAttackDeg` dreht den Anströmvektor).
  - **Ausdehnung in X (`Bounds.Size.X`):** Sehnenlänge / Körperlänge (`Chord`).
  - **Ausdehnung in Y (`Bounds.Size.Y`):** Profildicke / Querabmessung (`Thickness`).

---

## 3. Formen erzeugen: Die 3 Wege

### Weg A: Aus einem Skelettgitter (`Lattice2D` → `Shape2D`)
Genau wie man in PicoGK Stäbe (`AddBeam`) und Kugeln (`AddSphere`) in ein `Lattice` legt, lassen sich in 2D organische Rahmen, Streben oder Rumpfquerschnitte aus Kreisen und Konusbalken aufbauen:

```csharp
using System.Numerics;
using MyPicoGkProject.Kernels.Planar2D;

var lattice = new Lattice2D();

// 1. Konusbalken von (0, 0) mit Radius 8 mm nach (60, 0) mit Radius 2 mm
//    Berechnet exakt die äußeren Tangenten und schließt beide Enden mit Kreisbögen ab.
lattice.AddBeam(new Vector2(0f, 0f), new Vector2(60f, 0f), r1: 8f, r2: 2f, capSegments: 64);

// 2. Weiteren Strebenbalken oder Kreisknoten hinzufügen (verschmilzt sofort per Union)
lattice.AddBeam(new Vector2(15f, 0f), new Vector2(30f, 20f), r1: 4f, r2: 1.5f);
lattice.AddCircle(new Vector2(30f, 20f), radius: 3f, segments: 64);

// 3. In ein Shape2D überführen und Übergänge mit Radius 2 mm organisch ausrunden
var shape = new Shape2D(lattice)
    .TripleOffset(2.0f);
```

### Weg B: Direkt über Polygone, Kreise oder Catmull-Rom-Splines (`Shape2D`)
Für Tragflügelprofile (z. B. NACA) oder Freiformkurven können Stützpunkte direkt als Polygon oder glatte Catmull-Rom-Spline übergeben werden:

```csharp
// 1. Exakte Punktliste (z. B. aus einer analytischen Profilformel wie in Naca2D)
var shape = new Shape2D()
    .AddPolygon(contourPoints);

// 2. Oder weiche Freiformkurve durch wenige Stützpunkte (Catmull-Rom-Spline)
var controlPoints = new List<Vector2>
{
    new(50f, 0f),    // Hinterkante
    new(25f, -6f),   // Unterseite
    new(0f, 0f),     // Vorderkante (Nase)
    new(25f, 8f)     // Oberseite
};
var splineShape = new Shape2D()
    .AddSpline(controlPoints, samplesPerSegment: 24, closed: true);

// 3. Kreise direkt hinzufügen
splineShape.AddCircle(new Vector2(10f, 0f), radius: 4f);
```

---

## 4. Boolesche Operationen & Offset-Verrundung (Filleting)

Alle Operationen auf `Shape2D` sind **kettenfähig (Fluent API)** und arbeiten über Clipper2 mit hoher numerischer Präzision:

```csharp
// Boolesche Vereinigung (Union), Differenz (Löcher/Abzug) und Schnittmenge (Intersection)
shape.BoolAdd(otherShape)
     .BoolSubtract(coolingHole)
     .BoolIntersect(boundingBoxShape);

// Kontur nach außen (+d) oder innen (-d) versetzen (mit runden Ecken JoinType.Round)
shape.Offset(1.5f);

// Zweifacher Versatz (z. B. erst +2 mm schmelzen, dann -2 mm zurück = schließt Innenwinkel)
shape.DoubleOffset(2.0f, -2.0f);

// Dreifacher Versatz (+r, -2r, +r) analog zu PicoGKs TripleOffset:
// Rundet gleichzeitig alle scharfen Innen- UND Außenkanten mit Radius r ab!
shape.TripleOffset(1.0f);
```

> **Hinweis zu Löchern (`IsHole`):**  
> Zieht man mit `BoolSubtract` eine Form vollständig im Inneren ab (oder entstehen durch verschränkte Streben geschlossene Hohlräume), verwaltet `Shape2D` diese inneren Schleifen automatisch über ihre Umlaufrichtung. Beim Export und beim Flächenmessen werden Löcher vollautomatisch berücksichtigt.

---

## 5. Automatische Knickpunkterkennung (Wichtig für CFD-Grenzschichten!)

Eine besondere Stärke von `Shape2D` ist die eingebaute **Knickpunkterkennung (`DetectCorners` / `CornerAngleDeg`)**:

- Tragflügelprofile haben oft eine glatte Nase, aber eine **spitze Hinterkante** (oder stumpfe Abrisskanten mit zwei ~90°-Ecken).
- Wenn Gmsh an einer scharfen Ecke eine strukturierte Grenzschicht ($y^+ \approx 1$) extrudiert, würden sich die Normalen ohne Sonderbehandlung überschneiden oder einschnüren.
- Beim Speichern mit `shape.SaveContour(path, cornerAngleDeg: 35.0f)` prüft `Shape2D` an jedem Stützpunkt den Richtungsänderungswinkel (Turn-Angle). Überschreitet er `cornerAngleDeg` (Standard: `35°`), wird der Punktindex in `CornerIndices` eingetragen.
- **`Gmsh2DMesher` liest diese `CornerIndices` automatisch aus**, teilt die Spline dort sauber auf und aktiviert an diesen Knickpunkten Gmshs `FanPoints` (Fächer-Stützpunkte in der Grenzschicht) sowie die lokale Kantenverfeinerung (`EdgeRefinementFactor`).

---

## 6. Vermessen & Exportieren im `IGeometryGenerator`

In einem 2D-Projekt (abgeleitet von [`Cfd2DProjectDefinition`](../../Composition/Cfd2DProjectDefinition.cs)) sieht die Rückgabe in `GenerateAndExport` wie folgt aus:

```csharp
// 1. Kontur für Gmsh2DMesher als JSON speichern (inkl. Knickpunkte & Loch-Flags)
string contourPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.json");
shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

// 2. Optional: SVG-Vektorgrafik zur visuellen Kontrolle speichern
//    (Zeichnet die Kontur blau und markiert erkannte Knickpunkte als rote Punkte!)
string svgPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.svg");
shape.SaveSvg(svgPath);

// 3. Geometrie vermessen
ContourMeasurement2D m = shape.Measure();
// Verfügbare Eigenschaften auf 'm':
//   m.Area       -> Nettofläche in mm² (MetricScaling.Area)
//   m.Perimeter  -> Umfang in mm       (MetricScaling.Linear)
//   m.Chord      -> Länge in X in mm   (MetricScaling.Linear)
//   m.Thickness  -> Dicke in Y in mm   (MetricScaling.Linear)
//   m.Bounds     -> BBox2 (Min, Max, Size, Center)
//   m.PointCount -> Anzahl der Konturpunkte

// 4. GeometryResult mit Kind = GeometryKind.Contour2D zurückgeben
var result = new GeometryResult
{
    StlPath = contourPath,          // Pfad zur .json-Konturdatei für Gmsh2DMesher
    Kind = GeometryKind.Contour2D   // Kennzeichnet 2D-Kontur (keine 3D-STL-Glättung)
};

result.AddMetric("Area", m.Area, MetricScaling.Area);
result.AddMetric("Perimeter", m.Perimeter, MetricScaling.Linear);
result.AddMetric("Chord", m.Chord, MetricScaling.Linear);
result.AddMetric("Thickness", m.Thickness, MetricScaling.Linear);
// Für SU2 REF_AREA (in 2D ist die Bezugsfläche die Sehnenlänge × 1 m Tiefe):
result.AddMetric("FrontalArea", m.Chord, MetricScaling.Linear);

return result;
```

---

## 7. Zusammenspiel mit 3D-Projekten (`IHasRepresentativeSection`)

Auch ein 3D-Projekt (wie [`TestprojektNACA`](../../../projects/TestprojektNACA/)) kann den `Planar2D`-Kernel nutzen, indem seine `IProjectDefinition` zusätzlich das Interface [`IHasRepresentativeSection`](../../Core/Interfaces/IHasRepresentativeSection.cs) implementiert und dort ein `Shape2D` seines charakteristischen Querschnitts zurückgibt. Ist in `simulation.json` unter `MeshCalibration` die Option `"Use2DPrecalibration": true` gesetzt, kalibriert das Framework die Netzfeinheit zuerst blitzschnell in 2D und bestätigt danach nur noch das Ergebnis in 3D.
