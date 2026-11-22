using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

using FluidForge.Core;
using FluidForge.Kernels.PicoGk;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Manta-Ray AUV geometry generator.
    /// Generates the bionic manta geometry with a fin-ray membrane wing structure.
    /// </summary>
    public class MantaGeometryGenerator : IGeometryGenerator
    {
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            // 1. Read design parameters with fallbacks
            float length = parameters.TryGetValue("Length", out var l) ? l : 50f;
            float width = parameters.TryGetValue("Width", out var w) ? w : 40f;
            float mainRad = parameters.TryGetValue("MainRadius", out var mr) ? mr : 8f;
            float wingRad = parameters.TryGetValue("WingRadius", out var wr) ? wr : 4f;
            float tailTaper = parameters.TryGetValue("TailTaper", out var tt) ? tt : 0.2f;

            // 2. Define skeleton key points
            Vector3 nose = new Vector3(0, 0, 0);
            Vector3 tail = new Vector3(length, 0, 0);

            // Sweep wings slightly aft for improved hydrodynamics
            float wingRootX = length * 0.35f;
            float wingTipX = wingRootX + (length * 0.15f);

            Vector3 wingLeft = new Vector3(wingTipX, width / 2.0f, 0);
            Vector3 wingRight = new Vector3(wingTipX, -width / 2.0f, 0);

            // 3. Position sensor pods
            float sensorRadius = 3.0f;
            Vector3 s1 = new Vector3(length * 0.15f, 0, -mainRad * 0.8f);
            Vector3 s2 = new Vector3(wingLeft.X, wingLeft.Y, -wingRad * 0.8f);
            Vector3 s3 = new Vector3(wingRight.X, wingRight.Y, -wingRad * 0.8f);

            // 4. Build bionic manta lattice
            Lattice mantaLat = new Lattice();

            // Main fuselage (torpedo body)
            mantaLat.AddBeam(nose, tail, mainRad, mainRad * tailTaper, true);

            // --- INTEGRAL WING STRUCTURE (MEMBRANE FAN) ---
            int finRays = 30;
            float wingRootFront = length * 0.15f;
            float wingRootBack = length * 0.75f;
            float finRootThickness = mainRad * 0.4f;
            float finTipThickness = wingRad * 0.3f;

            for (int i = 0; i <= finRays; i++)
            {
                float t = i / (float)finRays;
                float currentRootX = wingRootFront + t * (wingRootBack - wingRootFront);
                Vector3 rootPos = new Vector3(currentRootX, 0, 0);
                mantaLat.AddBeam(rootPos, wingLeft, finRootThickness, finTipThickness, true);
                mantaLat.AddBeam(rootPos, wingRight, finRootThickness, finTipThickness, true);
            }

            // --- SENSOR INTEGRATION ---
            Vector3 microStep = new Vector3(0.01f, 0, 0);
            mantaLat.AddBeam(s1, s1 + microStep, sensorRadius, sensorRadius, true);
            mantaLat.AddBeam(s2, s2 + microStep, sensorRadius, sensorRadius, true);
            mantaLat.AddBeam(s3, s3 + microStep, sensorRadius, sensorRadius, true);

            // 5. Voxelize and blend organically
            Voxels voxModel = new Voxels(mantaLat);
            float blendRadius = mainRad * 0.6f;
            voxModel.Offset(blendRadius);
            voxModel.Offset(-blendRadius);

            voxModel.CalculateProperties(out float modelVolume, out BBox3 boundingBox);

            float sizeY = boundingBox.vecMax.Y - boundingBox.vecMin.Y;
            float sizeZ = boundingBox.vecMax.Z - boundingBox.vecMin.Z;
            float frontalBoxArea = sizeY * sizeZ;

            // 6. Compute SensorDistance (triangle area formed by the three sensor pods)
            float sensorDistanceScore = Vector3.Cross(s2 - s1, s3 - s1).Length() / 2.0f;

            // 7. Export STL (headless — no viewer)
            Mesh exportModel = new Mesh(voxModel);
            string modelName = $"Model_Gen{iteration}_Var{variant}.stl";
            string fullModelPath = Path.Combine(outputDirectory, modelName);
            exportModel.SaveToStlFile(fullModelPath);

            float measuredVolume = modelVolume;
            float? measuredFrontalArea = null;

            if (config.UseStlSmoothing)
            {
                try
                {
                    StlSmoother.SmoothStl(
                        fullModelPath, fullModelPath,
                        config.VoxelSmoothingIterations,
                        config.VoxelSmoothingPremeltingSteps);

                    IReadOnlyList<StlTriangle> smoothedTriangles = StlWriter.ReadTriangles(fullModelPath);
                    if (smoothedTriangles.Count > 0)
                    {
                        MeshMeasurement smoothedMeasurement = MeshMetrics.Measure(smoothedTriangles);
                        measuredVolume = smoothedMeasurement.Volume;
                        measuredFrontalArea = smoothedMeasurement.GetFrontalArea(config.UseProjectedFrontalArea);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"       -> {OutputConventions.WarningTag} Smoothing failed: {ex.Message}");
                }
            }

            float frontalArea = measuredFrontalArea ?? (config.UseProjectedFrontalArea
                ? PicoGkMeshMetrics.FrontalProjectedArea(exportModel)
                : frontalBoxArea);

            var result = new GeometryResult { StlPath = fullModelPath };
            result.AddMetric("Volume", measuredVolume, MetricScaling.Volume);
            result.AddMetric("FrontalArea", frontalArea, MetricScaling.Area);
            // SensorDistance is half a cross product, i.e., an area — not a length.
            result.AddMetric("SensorDistance", sensorDistanceScore, MetricScaling.Area);
            return result;
        }
    }
}
