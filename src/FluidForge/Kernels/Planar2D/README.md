# Guide: Built-in 2D Geometry Kernel (`Planar2D`)

> **Related Documentation:**
> - **Creating a new 2D/3D project:** [`src/projects/README.md`](../../../projects/README.md) (Template: [`src/projects/_Template2D/`](../../../projects/_Template2D/README.md), Example: [`src/projects/Naca2D/`](../../../projects/Naca2D/))
> - **3D Geometry with PicoGK:** [`src/FluidForge/Kernels/PicoGk/README.md`](../PicoGk/README.md)
> - **All 2D Meshing & Solver Settings:** [`docs/config-reference.md`](../../../../docs/config-reference.md) | [`docs/physics-presets.md`](../../../../docs/physics-presets.md)

The `Planar2D` kernel (`FluidForge.Kernels.Planar2D`) is an analytical 2D geometry kernel written specifically for this framework on top of **Clipper2**.

It is intentionally designed to feel **just like PicoGK in 3D** (`Lattice2D` instead of `Lattice`, `Shape2D` instead of `Voxels`, `BBox2` instead of `BBox3`, `BoolAdd`, `BoolSubtract`, `Offset`, `TripleOffset`), while operating on exact planar vector contours — **completely free of voxel staircase artifacts, without OpenGL/Xvfb, and executing in fractions of a millisecond**.

---

## 1. Class Overview

| Class / Struct | Namespace | PicoGK (3D) Equivalent | Purpose |
| --- | --- | --- | --- |
| [`Shape2D`](Shape2D.cs) | `FluidForge.Kernels.Planar2D` | `Voxels` + `Mesh` | Represents a 2D shape (arbitrary closed outer contours and inner holes). Provides primitives, splines, boolean operations, offset fillets, corner detection, and JSON/SVG export. |
| [`Lattice2D`](Lattice2D.cs) | `FluidForge.Kernels.Planar2D` | `Lattice` | Two-dimensional beam/skeleton lattice made of circular nodes (`AddCircle`) and tangent tapered/capsule beams (`AddBeam`). |
| [`BBox2`](BBox2.cs) | `FluidForge.Core` | `BBox3` | Axis-aligned 2D bounding box (`Min`, `Max`, `Size`, `Center`, `Include`, `Contains`). |
| [`MeshMetrics2D`](../../Core/Utilities/MeshMetrics2D.cs) / [`ContourMeasurement2D`](../../Core/Utilities/MeshMetrics2D.cs) | `FluidForge.Core` | `PicoGkMeshMetrics` / `MeshMeasurement` | Computes net area (shoelace formula, holes subtracted automatically), total perimeter, chord length (`Chord` in X), thickness (`Thickness` in Y), and point count. |
| [`DirectGeometryKernel`](../../Core/Interfaces/IGeometryKernel.cs) | `FluidForge.Core` | `PicoGkKernel` | Executes the 2D generator directly without a native C++ library or display server (`xvfb-run`). |

---

## 2. Coordinate System & Units

- **Unit:** All coordinates, radii, and distances are in **millimeters (mm)**.
- **Flow Direction:** By default, flow in Gmsh/SU2 runs in the **+X direction** (left to right; `AngleOfAttackDeg` rotates the freestream vector).
  - **Extent in X (`Bounds.Size.X`):** Chord / body length (`Chord`).
  - **Extent in Y (`Bounds.Size.Y`):** Profile thickness / cross-flow extent (`Thickness`).

---

## 3. Creating Shapes: 3 Approaches

### Approach A: From a Skeleton Lattice (`Lattice2D` → `Shape2D`)
Just like placing beams (`AddBeam`) and spheres (`AddSphere`) into a PicoGK `Lattice`, organic frames, struts, or fuselage cross-sections can be built in 2D from circles and tapered beams:

```csharp
using System.Numerics;
using FluidForge.Kernels.Planar2D;

var lattice = new Lattice2D();

// 1. Tapered beam from (0, 0) with radius 8 mm to (60, 0) with radius 2 mm.
//    Computes exact outer tangents and closes both ends with circular arcs.
lattice.AddBeam(new Vector2(0f, 0f), new Vector2(60f, 0f), r1: 8f, r2: 2f, capSegments: 64);

// 2. Add another strut beam or circular node (immediately merged via union)
lattice.AddBeam(new Vector2(15f, 0f), new Vector2(30f, 20f), r1: 4f, r2: 1.5f);
lattice.AddCircle(new Vector2(30f, 20f), radius: 3f, segments: 64);

// 3. Convert to Shape2D and organically round transitions with a 2 mm radius
var shape = new Shape2D(lattice)
    .TripleOffset(2.0f);
```

### Approach B: Directly via Polygons, Circles, or Catmull-Rom Splines (`Shape2D`)
For airfoils (e.g., NACA) or freeform curves, points can be passed directly as a polygon or smooth Catmull-Rom spline:

```csharp
// 1. Exact point list (e.g., from an analytical airfoil formula as in Naca2D)
var shape = new Shape2D()
    .AddPolygon(contourPoints);

// 2. Or a smooth freeform curve through control points (Catmull-Rom spline)
var controlPoints = new List<Vector2>
{
    new(50f, 0f),    // Trailing edge
    new(25f, -6f),   // Lower surface
    new(0f, 0f),     // Leading edge (nose)
    new(25f, 8f)     // Upper surface
};
var splineShape = new Shape2D()
    .AddSpline(controlPoints, samplesPerSegment: 24, closed: true);

// 3. Add circles directly
splineShape.AddCircle(new Vector2(10f, 0f), radius: 4f);
```

---

## 4. Boolean Operations & Offset Filleting

All operations on `Shape2D` are **chainable (Fluent API)** and use Clipper2 with high numerical precision:

```csharp
// Boolean union, difference (holes/cutouts), and intersection
shape.BoolAdd(otherShape)
     .BoolSubtract(coolingHole)
     .BoolIntersect(boundingBoxShape);

// Offset contour outward (+d) or inward (-d) (with rounded corners JoinType.Round)
shape.Offset(1.5f);

// Double offset (e.g., expand +2 mm then shrink -2 mm = closes inner corners)
shape.DoubleOffset(2.0f, -2.0f);

// Triple offset (+r, -2r, +r) analogous to PicoGK's TripleOffset:
// Simultaneously rounds both sharp inner AND outer corners with radius r!
shape.TripleOffset(1.0f);
```

> **Note on Holes (`IsHole`):**  
> When subtracting a shape completely inside another with `BoolSubtract` (or when intersecting struts form enclosed cavities), `Shape2D` automatically tracks these inner loops via their winding direction. Export and area measurements account for holes automatically.

---

## 5. Automatic Corner Detection (Critical for CFD Boundary Layers!)

A key strength of `Shape2D` is its built-in **corner detection (`DetectCorners` / `CornerAngleDeg`)**:

- Airfoils typically have a smooth nose but a **sharp trailing edge** (or blunt trailing edges with two ~90° corners).
- When Gmsh extrudes a structured boundary layer ($y^+ \approx 1$) around a sharp corner, normals would intersect or pinch without special handling.
- When saving via `shape.SaveContour(path, cornerAngleDeg: 35.0f)`, `Shape2D` checks the directional turn angle at every vertex. If it exceeds `cornerAngleDeg` (default: `35°`), the vertex index is recorded in `CornerIndices`.
- **`Gmsh2DMesher` reads these `CornerIndices` automatically**, splits the spline cleanly at those vertices, and enables Gmsh's `FanPoints` (fanning points in the boundary layer) as well as local edge refinement (`EdgeRefinementFactor`).

---

## 6. Measuring & Exporting in `IGeometryGenerator`

In a 2D project (derived from [`Cfd2DProjectDefinition`](../../Composition/Cfd2DProjectDefinition.cs)), `GenerateAndExport` returns results as follows:

```csharp
// 1. Save contour for Gmsh2DMesher as JSON (including corner indices & hole flags)
string contourPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.json");
shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

// 2. Optional: Save SVG vector graphic for visual inspection
//    (Draws the contour in blue and highlights detected corners as red dots!)
string svgPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.svg");
shape.SaveSvg(svgPath);

// 3. Measure geometry
ContourMeasurement2D m = shape.Measure();
// Available properties on 'm':
//   m.Area       -> Net area in mm² (MetricScaling.Area)
//   m.Perimeter  -> Perimeter in mm  (MetricScaling.Linear)
//   m.Chord      -> Length in X in mm (MetricScaling.Linear)
//   m.Thickness  -> Thickness in Y in mm (MetricScaling.Linear)
//   m.Bounds     -> BBox2 (Min, Max, Size, Center)
//   m.PointCount -> Number of contour points

// 4. Return GeometryResult with Kind = GeometryKind.Contour2D
var result = new GeometryResult
{
    StlPath = contourPath,          // Path to .json contour file for Gmsh2DMesher
    Kind = GeometryKind.Contour2D   // Marks 2D contour (skips 3D STL smoothing)
};

result.AddMetric("Area", m.Area, MetricScaling.Area);
result.AddMetric("Perimeter", m.Perimeter, MetricScaling.Linear);
result.AddMetric("Chord", m.Chord, MetricScaling.Linear);
result.AddMetric("Thickness", m.Thickness, MetricScaling.Linear);
// For SU2 REF_AREA (in 2D, the reference area is chord length × 1 m span):
result.AddMetric("FrontalArea", m.Chord, MetricScaling.Linear);

return result;
```

---

## 7. Interaction with 3D Projects (`IHasRepresentativeSection`)

A 3D project can also leverage the `Planar2D` kernel by implementing [`IHasRepresentativeSection`](../../Core/Interfaces/IHasRepresentativeSection.cs) on its `IProjectDefinition` and returning a `Shape2D` of its characteristic cross-section. When `"Use2DPrecalibration": true` is set under `MeshCalibration` in `simulation.json`, the framework calibrates mesh fineness rapidly in 2D first and then confirms the result in 3D.
