using System;
using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk.InternalFlow;

namespace FluidForge.Projects.BenchmarkPipeBend90
{
    /// <summary>
    /// Parametric 90-degree pipe bend generator using <see cref="ShapeKernelPipeBuilder.BuildPipeBend90"/>
    /// with fixed 5D inlet and 10D outlet straight tangents for Idelchik/Miller validation.
    /// </summary>
    public sealed class BenchmarkPipeBend90GeometryGenerator : IGeometryGenerator
    {
        public const float DefaultPipeRadiusMm = 15.0f;
        public const float DefaultInletStraightRatio = 10.0f;
        public const float DefaultOutletStraightRatio = 20.0f;

        private readonly float _pipeRadiusMm;
        private readonly float _inletStraightRatio;
        private readonly float _outletStraightRatio;

        /// <summary>
        /// Initializes the generator from <c>OptimizationTargets</c> in <paramref name="project"/>.
        /// </summary>
        public BenchmarkPipeBend90GeometryGenerator(ProjectConfig? project = null)
        {
            _pipeRadiusMm = ReadPositiveTarget(project, "PipeRadiusMm", DefaultPipeRadiusMm);
            _inletStraightRatio = ReadPositiveTarget(project, "InletStraightRatio", DefaultInletStraightRatio);
            _outletStraightRatio = ReadPositiveTarget(project, "OutletStraightRatio", DefaultOutletStraightRatio);
        }

        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

            float bendRadiusRatio = Read(parameters, "BendRadiusRatio");
            float radiusModulationMid = Read(parameters, "RadiusModulationMid");

            float bendRadiusMm = _pipeRadiusMm * bendRadiusRatio;
            float inletStraightMm = _pipeRadiusMm * _inletStraightRatio;
            float outletStraightMm = _pipeRadiusMm * _outletStraightRatio;

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
                result.AddMetric("BendRadiusRatio", bendRadiusRatio, MetricScaling.None);
                result.AddMetric("BendRadiusToDiameter", bendRadiusRatio * 0.5f, MetricScaling.None);

                float totalCenterlineLengthMm = inletStraightMm + 0.5f * MathF.PI * bendRadiusMm + outletStraightMm;
                result.AddMetric("TotalCenterlineLengthMm", totalCenterlineLengthMm, MetricScaling.Linear);

                return result;
            }
            finally
            {
                built.FluidVoxels.Dispose();
            }
        }

        private static float ReadPositiveTarget(ProjectConfig? project, string key, float fallback)
        {
            if (project?.OptimizationTargets == null || !project.OptimizationTargets.TryGetValue(key, out float value))
                return fallback;

            if (!float.IsFinite(value) || value <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} BenchmarkPipeBend90 OptimizationTargets.{key} must be finite and > 0 (got {value}).");
            }

            return value;
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing parameter '{name}' for BenchmarkPipeBend90. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
