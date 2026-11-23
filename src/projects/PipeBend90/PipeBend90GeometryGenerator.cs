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
        public const float DefaultPipeRadiusMm = 12.0f;
        private readonly float _pipeRadiusMm;

        /// <summary>
        /// Initializes the generator using <c>OptimizationTargets.PipeRadiusMm</c> from
        /// <paramref name="project"/> (defaults to 12 mm when omitted).
        /// </summary>
        public PipeBend90GeometryGenerator(ProjectConfig? project = null)
        {
            _pipeRadiusMm = DefaultPipeRadiusMm;
            if (project?.OptimizationTargets != null &&
                project.OptimizationTargets.TryGetValue("PipeRadiusMm", out float r))
            {
                if (!float.IsFinite(r) || r <= 0f)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} PipeBend90 OptimizationTargets.PipeRadiusMm must be finite and > 0 (got {r}).");
                }
                _pipeRadiusMm = r;
            }
        }

        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float bendRadiusRatio = Read(parameters, "BendRadiusRatio");
            float inletStraightRatio = Read(parameters, "InletStraightRatio");
            float outletStraightRatio = Read(parameters, "OutletStraightRatio");
            float radiusModulationMid = Read(parameters, "RadiusModulationMid");

            float bendRadiusMm = _pipeRadiusMm * bendRadiusRatio;
            float inletStraightMm = _pipeRadiusMm * inletStraightRatio;
            float outletStraightMm = _pipeRadiusMm * outletStraightRatio;

            BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildPipeBend90(
                _pipeRadiusMm,
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
                    bendRadiusRatio,
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
