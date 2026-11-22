# Guide: 3D Voxel Modeling with PicoGK (`Kernels/PicoGk`)

> **Official PicoGK Documentation & Resources:**
> - **Official Website & Documentation:** [https://picogk.org/](https://picogk.org/)
> - **PicoGK C# Repository (LEAP 71):** [https://github.com/leap71/PicoGK](https://github.com/leap71/PicoGK)
> - **PicoGK Native C++ Runtime:** [https://github.com/leap71/PicoGKRuntime](https://github.com/leap71/PicoGKRuntime)
> - **Textbook *"Coding for Engineers"* (Lin Kayser / LEAP 71):** [https://leap71.com/coding-for-engineers/](https://leap71.com/coding-for-engineers/)
>
> **Related Repository Documentation:**
> - **Creating a new 3D/2D project:** [`src/projects/README.md`](../../../projects/README.md) (Template: [`src/projects/_Template3D/`](../../../projects/_Template3D/README.md), Example: [`src/projects/MantaAuv/`](../../../projects/MantaAuv/))
> - **Built-in 2D Geometry Kernel (`Planar2D`):** [`src/FluidForge/Kernels/Planar2D/README.md`](../Planar2D/README.md)
> - **Voxel & Smoothing Settings (`simulation.json`):** [`src/projects/BEDIENHANDBUCH.md`](../../../projects/BEDIENHANDBUCH.md)

This document explains how three-dimensional parts are generated, organically blended, measured, smoothed, and exported for CFD simulation using **PicoGK** in C#.

---

## 1. Why Voxels Instead of Classical CAD (B-Rep)?

In classical CAD kernels (NURBS / B-Rep), automated optimization loops frequently fail because surfaces self-intersect under extreme parameter combinations or fillets abort with topology errors.

**PicoGK** instead operates on **voxels** (three-dimensional volume elements / Signed Distance Fields based on OpenVDB):
- **Unbreakable Boolean Operations:** Hundreds of struts, hull segments, and wings can arbitrarily intersect — union (`BoolAdd`), difference (`BoolSubtract`), or intersection (`BoolIntersect`) **always** succeed cleanly.
- **Organic Blending (`Offset`):** Expanding by $+r$ and subsequently shrinking by $-r$ automatically produces smooth, hydrodynamically favorable fillets between all intersecting components.

---

## 2. How PicoGK Is Integrated into the Framework

As a project developer, you do **not** need to manage `Library.Go(...)`, viewer windows, or OpenGL contexts:
1. Your project class inherits from [`CfdProjectDefinition`](../../Composition/CfdProjectDefinition.cs).
2. It automatically uses [`PicoGkKernel`](PicoGkKernel.cs), which launches `Library.Go(voxelResolution, ..., bEndAppWithTask: true)` with the voxel size (`BaseVoxelResolution`, default `0.5` mm) loaded from `simulation.json`. Beforehand, `VoxelResolutionAdvisor` checks the real-space voxel size against the surface target cell size and automatically refines it when `AutoVoxelResolution = true` (down to a minimum of `0.1` mm).
3. On Linux servers, `scripts/sim-runner.sh` automatically provides the virtual display (`xvfb-run -a`) and the native library (`libpicogk.so` / `libpicogk.26.2.so`).

---

## 3. The 4 Building Blocks of Modeling in PicoGK

The typical pipeline in your `*GeometryGenerator.cs` follows **`Lattice` (or primitive `Mesh`) → `Voxels` → `Mesh` → `.stl`**:

### Building Block 1: `Lattice` (Skeleton of Beams and Spheres)
A `Lattice` describes a wireframe of spheres and tapered beams that is subsequently converted into a voxel volume:

```csharp
using System.Numerics;
using PicoGK;

Lattice lat = new Lattice();

// 1. Tapered or cylindrical beam from point A (radius r1) to point B (radius r2):
//    Last parameter (true = rounded spherical caps, false = flat ends)
Vector3 nose = new Vector3(0f, 0f, 0f);
Vector3 tail = new Vector3(50f, 0f, 0f);
lat.AddBeam(nose, tail, 8.0f, 2.0f, true);

// 2. Add a single sphere:
lat.AddSphere(new Vector3(10f, 0f, -6f), 3.0f);
```

> **Tip from [`MantaGeometryGenerator.cs`](../../../projects/MantaAuv/MantaGeometryGenerator.cs):**  
> Wings or membrane surfaces can be constructed cleanly as a fan (fin-ray structure) of closely spaced `AddBeam` calls, which are then fused into a closed smooth surface in voxel space via `Offset`!

### Building Block 2: `Mesh` & `Utils` (Triangle Meshes & Primitives)
In addition to `Lattice`, triangle meshes can be created or loaded directly and converted to voxels:

```csharp
// Box with side lengths (length, width, height), centered at the origin:
Mesh boxMesh = Utils.mshCreateCube(new Vector3(length, width, height), Vector3.Zero);
```

### Building Block 3: `Voxels` (Boolean Algebra & Organic Filleting)
Once a `Lattice` or `Mesh` is converted into `Voxels`, all volumetric operations are available:

```csharp
// Create a voxel volume from a Lattice or Mesh:
Voxels voxBody = new Voxels(lat);
Voxels voxCutout = new Voxels(boxMesh);

// Boolean operations:
voxBody.BoolAdd(otherVoxels);       // Union
voxBody.BoolSubtract(voxCutout);    // Difference
voxBody.BoolIntersect(boundVoxels); // Intersection

// Organic blending / filleting:
// 1. Grow outward (+blendRadius) -> fills creases and gaps
// 2. Shrink back by the same amount (-blendRadius) -> preserves outer dimensions, rounds inner corners!
float blendRadius = 4.0f;
voxBody.Offset(blendRadius);
voxBody.Offset(-blendRadius);

// Or in a single call:
// voxBody.DoubleOffset(blendRadius, -blendRadius);
// voxBody.TripleOffset(blendRadius); // (+r, -2r, +r) rounds BOTH inner and outer edges
```

### Building Block 4: Export as `Mesh` & Surface Smoothing (`StlSmoother`)
PicoGK extracts a triangle `Mesh` from the finished `Voxels` object via Marching Cubes. Since voxel surfaces exhibit fine staircase steps, the framework component [`StlSmoother`](../../Core/Utilities/StlSmoother.cs) applies feature-preserving (Taubin/Laplace) smoothing to the exported STL mesh:

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

## 4. Measuring Geometry & Returning Metrics

Two measurement paths are available for the fitness function (`IFitnessCalculator`) and for the reference area in SU2 (`ReferenceAreaMetric` in `solvers/su2.json`):

### Option A: `PicoGkMeshMetrics.Measure(exportModel)` (Recommended for new projects)
[`PicoGkMeshMetrics.Measure`](PicoGkMeshMetrics.cs) measures the generated `Mesh` in a single pass and also computes **`SurfaceArea`**, which PicoGK's built-in `CalculateProperties` does not provide:

```csharp
using FluidForge.Kernels.PicoGk;

MeshMeasurement m = PicoGkMeshMetrics.Measure(exportModel);
// m.Volume          -> Volume in mm³ (magnitude)
// m.SignedVolume    -> Signed volume (< 0 warns of inverted normals)
// m.SurfaceArea     -> Total part surface area in mm²
// m.FrontalBoxArea  -> Bounding-box frontal area perpendicular to X flow (Size.Y × Size.Z) in mm²
// m.Bounds          -> BBox (Min, Max, Size)

var result = new GeometryResult { StlPath = stlPath };
result.AddMetric("Volume", m.Volume, MetricScaling.Volume);
result.AddMetric("SurfaceArea", m.SurfaceArea, MetricScaling.Area);
result.AddMetric("FrontalArea", m.FrontalBoxArea, MetricScaling.Area);
result.AddMetric("Span", m.Bounds.Size.Y, MetricScaling.Linear);
```

### Option B: `voxBody.CalculateProperties(out float volume, out BBox3 bbox)`
Direct measurement on the voxel field (used in `MantaAuv` to remain bit-compatible with historical runs):

```csharp
voxBody.CalculateProperties(out float modelVolume, out BBox3 boundingBox);
float frontalArea = (boundingBox.vecMax.Y - boundingBox.vecMin.Y)
                  * (boundingBox.vecMax.Z - boundingBox.vecMin.Z);
```

---

## 5. Important Rules for `RubberBandScaler` & Voxel Resolution

1. **Always use the passed `parameters`:**  
   When `UseRubberBandScaler = true`, `WorkflowController` scales all length parameters listed in `DimensionalParameters` (`project.json`) down to `TargetPicoGkSize` (default `50` mm) before calling `GenerateAndExport`, so every model is resolved with the same relative voxel fineness regardless of its absolute size.
2. **Register every metric with its matching `MetricScaling`:**  
   After `GenerateAndExport`, the controller automatically scales all reported metrics back to original size:
   - `MetricScaling.Volume` ($\times s^3$) for volumes (`Volume`),
   - `MetricScaling.Area` ($\times s^2$) for areas (`FrontalArea`, `SurfaceArea`, cross products),
   - `MetricScaling.Linear` ($\times s^1$) for lengths (`Span`, `Chord`),
   - `MetricScaling.None` ($\times 1$) for dimensionless ratios or angles.
3. **Observe minimum wall thickness & startup voxel checks:**  
   At a voxel size of `BaseVoxelResolution = 0.5` mm, structural members and wing edges in the scaled working size should be at least ~`1.0` to `1.5` mm thick (at least 2–3 voxels) so thin skins do not tear during voxelization. `VoxelResolutionAdvisor` checks at startup that the real-space voxel is no coarser than 80% of the surface target cell size; with `"AutoVoxelResolution": true`, the framework automatically refines the effective voxel size at startup (without changing the `--resume` fingerprint).
