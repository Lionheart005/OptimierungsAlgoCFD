using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

using FluidForge.Core;
using FluidForge.Kernels.PicoGk;

namespace FluidForge.Projects.MyProject
{
    /// <summary>
    /// Project geometry generator. This template builds the simplest possible body —
    /// a box of <c>Length x Width x Height</c> — and reports four metrics so the full pipeline
    /// (PicoGK -> STL -> Gmsh -> SU2 -> Fitness) can be smoke-tested before adding your own shape.
    ///
    /// <para>
    /// <b>Edit here.</b> Replace <see cref="BuildVoxelModel"/> with your own geometry.
    /// For a real-world example, see <c>src/projects/MantaAuv/MantaGeometryGenerator.cs</c>,
    /// which builds a <c>Lattice</c> fuselage with a fin-ray wing fan blended via two <c>Offset</c> steps.
    /// </para>
    ///
    /// <para>
    /// The framework communicates with the geometry exclusively via <see cref="GeometryResult"/> —
    /// an STL file and named metrics. Any name passed to <see cref="GeometryResult.AddMetric"/>
    /// is available to the fitness formula, <c>ReferenceAreaMetric</c> in su2.json, and as a column
    /// in <c>results.csv</c>.
    /// </para>
    /// </summary>
    public sealed class MyProjectGeometryGenerator : IGeometryGenerator
    {
        /// <inheritdoc/>
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            // Parameters come from project.json (or CreateDefaults) and are already scaled to
            // PicoGK's working size — the controller scales reported metrics back afterward.
            float length = Read(parameters, "Length");
            float width = Read(parameters, "Width");
            float height = Read(parameters, "Height");

            Voxels voxels = BuildVoxelModel(length, width, height);

            Mesh exportModel = new Mesh(voxels);

            // Output STLs are written under results/<Project>/.
            string modelPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
            exportModel.SaveToStlFile(modelPath);

            // Smoothing is a toggleable framework step: if UseStlSmoothing is false in simulation.json,
            // the raw voxel mesh is kept. A failed smoothing pass should not abort the variant.
            // When smoothing succeeds, metrics are measured directly from the smoothed STL so
            // Volume, SurfaceArea, FrontalArea, and Span match the actual CFD surface.
            MeshMeasurement? smoothedMeasurement = null;
            if (config.UseStlSmoothing)
            {
                try
                {
                    StlSmoother.SmoothStl(
                        modelPath, modelPath,
                        config.VoxelSmoothingIterations,
                        config.VoxelSmoothingPremeltingSteps);

                    IReadOnlyList<StlTriangle> smoothedTriangles = StlWriter.ReadTriangles(modelPath);
                    if (smoothedTriangles.Count > 0)
                    {
                        smoothedMeasurement = MeshMetrics.Measure(smoothedTriangles);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"       -> {OutputConventions.WarningTag} Smoothing failed: {ex.Message}");
                }
            }

            MeshMeasurement measurement = smoothedMeasurement ?? PicoGkMeshMetrics.Measure(exportModel);

            // A negative signed volume indicates inward-pointing triangle normals, which causes
            // Gmsh to fail or produce invalid volume meshes.
            if (measurement.SignedVolume < 0f)
                Console.WriteLine($"       -> {OutputConventions.WarningTag} Mesh is inverted (negative signed volume).");

            var result = new GeometryResult { StlPath = modelPath };

            // ==============================================================
            //  METRICS -- interface to the rest of the framework
            // ==============================================================
            //
            // The second argument to AddMetric is the physical DIMENSION, which tells
            // RubberBandScaler how to scale back from PicoGK working size:
            //
            //   Volume  cubic (s^3)   Area  quadratic (s^2)   Linear  linear (s^1)   None  unscaled
            //
            // A wrong scaling dimension will NOT throw an error — the value will simply be
            // off by s^1, s^2, or s^3. Effective scaling dimensions are recorded in
            // results/<Project>/effective-config.json.
            result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
            result.AddMetric("SurfaceArea", measurement.SurfaceArea, MetricScaling.Area);

            // FrontalArea is the cross-sectional area perpendicular to X and is used by
            // solvers/su2.json ("ReferenceAreaMetric") to compute REF_AREA.
            result.AddMetric("FrontalArea", measurement.GetFrontalArea(config.UseProjectedFrontalArea), MetricScaling.Area);

            // Span: extent perpendicular to the flow (a length, hence Linear).
            result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);

            // Ratios are dimensionless and must NOT be scaled (MetricScaling.None).
            result.AddMetric("LengthToWidth", length / Math.Max(width, 0.001f), MetricScaling.None);

            return result;
        }

        /// <summary>
        /// Placeholder body: a box centered at the origin with length along X (flow direction).
        /// <b>Replace this method with your own geometry.</b>
        ///
        /// <para>
        /// To round edges or blend parts organically, call <c>Offset(r)</c> followed by
        /// <c>Offset(-r)</c> on the <see cref="Voxels"/> instance.
        /// </para>
        /// </summary>
        private static Voxels BuildVoxelModel(float length, float width, float height)
        {
            Mesh box = Utils.mshCreateCube(new Vector3(length, width, height), Vector3.Zero);
            return new Voxels(box);
        }

        /// <summary>
        /// Reads a required parameter and fails fast if missing (no silent fallback).
        /// </summary>
        /// <exception cref="InvalidOperationException">The parameter is missing in configuration.</exception>
        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing parameter '{name}'. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters, MaxDeviations, and ParameterBounds in "
                + "project.json -- and to the same three dictionaries in CreateDefaults().");
        }
    }
}
