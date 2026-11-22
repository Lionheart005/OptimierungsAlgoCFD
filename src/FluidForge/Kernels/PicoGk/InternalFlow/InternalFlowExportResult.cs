using System;
using System.Collections.Generic;
using System.Numerics;
using FluidForge.Core;

namespace FluidForge.Kernels.PicoGk.InternalFlow
{
    /// <summary>
    /// Output of <see cref="MultiPatchFluidExporter.Export"/> containing combined, per-patch,
    /// and multi-solid STL paths along with measured areas, triangle counts, and fluid seed point.
    /// </summary>
    public sealed record InternalFlowExportResult(
        string CombinedStlPath,
        string MultiSolidStlPath,
        Dictionary<string, string> PatchPaths,
        Dictionary<string, float> PatchAreas,
        Dictionary<string, int> PatchTriangleCounts,
        Vector3 FluidSeedPoint,
        MeshMeasurement Measurement)
    {
        /// <summary>
        /// Converts this export result into a pure-Core <see cref="GeometryResult"/> for the
        /// optimization pipeline.
        /// </summary>
        public GeometryResult ToGeometryResult(string meshUnit = "mm")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(meshUnit);

            var result = new GeometryResult
            {
                Kind = GeometryKind.SurfaceStl,
                StlPath = CombinedStlPath,
                FluidSeedPoint = FluidSeedPoint,
                MeshUnit = meshUnit.Trim()
            };

            foreach (var kvp in PatchPaths)
            {
                result.AddSurfacePatch(kvp.Key, kvp.Value);
            }

            result.AddMetric("Volume", Measurement.Volume, MetricScaling.Volume);
            result.AddMetric("SurfaceArea", Measurement.SurfaceArea, MetricScaling.Area);

            foreach (var kvp in PatchAreas)
            {
                result.AddMetric($"PatchArea_{kvp.Key}", kvp.Value, MetricScaling.Area);
            }

            return result;
        }
    }
}
