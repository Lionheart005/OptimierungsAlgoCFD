using System;
using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk.InternalFlow;

namespace FluidForge.Projects.PipeBend90
{
    /// <summary>
    /// Parametric 90-degree pipe bend fluid-volume generator using
    /// <see cref="ShapeKernelPipeBuilder.BuildPipeBend90"/> and <see cref="MultiPatchFluidExporter"/>.
    /// </summary>
    public sealed class PipeBend90GeometryGenerator : IGeometryGenerator
    {
        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float pipeRadiusMm = Read(parameters, "PipeRadiusMm");
            float bendRadiusMm = Read(parameters, "BendRadiusMm");
            float inletStraightMm = Read(parameters, "InletStraightMm");
            float outletStraightMm = Read(parameters, "OutletStraightMm");
            float radiusModulationMid = Read(parameters, "RadiusModulationMid");

            BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildPipeBend90(
                pipeRadiusMm,
                bendRadiusMm,
                inletStraightMm,
                outletStraightMm,
                radiusModulationMid);

            try
            {
                InternalFlowExportResult exported = MultiPatchFluidExporter.Export(
                    built.FluidVoxels,
                    built.Ports,
                    built.FluidSeedPoint,
                    outputDirectory,
                    $"Model_Gen{iteration}_Var{variant}");

                GeometryResult result = exported.ToGeometryResult();
                result.AddMetric(
                    "BendRadiusRatio",
                    bendRadiusMm / MathF.Max(pipeRadiusMm, 1e-4f),
                    MetricScaling.None);
                return result;
            }
            finally
            {
                built.FluidVoxels.Dispose();
            }
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing parameter '{name}' for PipeBend90. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
