using System;
using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk.InternalFlow;

namespace FluidForge.Projects.MyInternalProject
{
    /// <summary>
    /// Internal-flow geometry generator template using <see cref="ShapeKernelPipeBuilder"/>
    /// and <see cref="MultiPatchFluidExporter"/> to produce watertight multi-patch STLs
    /// (<c>inlet</c>, <c>outlet</c>, <c>wall</c>) plus an interior <c>FluidSeedPoint</c>.
    /// </summary>
    public sealed class MyInternalProjectGeometryGenerator : IGeometryGenerator
    {
        public const float DefaultInletRadiusMm = 10.0f;
        private readonly float _inletRadiusMm;

        /// <summary>
        /// Initializes the generator using <c>OptimizationTargets.InletRadiusMm</c> from
        /// <paramref name="project"/> (defaults to 10 mm when omitted).
        /// </summary>
        public MyInternalProjectGeometryGenerator(ProjectConfig? project = null)
        {
            _inletRadiusMm = DefaultInletRadiusMm;
            if (project?.OptimizationTargets != null &&
                project.OptimizationTargets.TryGetValue("InletRadiusMm", out float r))
            {
                if (!float.IsFinite(r) || r <= 0f)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} MyInternalProject OptimizationTargets.InletRadiusMm must be finite and > 0 (got {r}).");
                }
                _inletRadiusMm = r;
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
            float lengthMm = 2.0f * _inletRadiusMm * Read(parameters, "LengthToDiameterRatio");

            BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildStraightPipe(lengthMm, _inletRadiusMm);
            try
            {
                InternalFlowExportResult exported = MultiPatchFluidExporter.Export(
                    built.FluidVoxels,
                    built.Ports,
                    built.FluidSeedPoint,
                    outputDirectory,
                    $"Model_Gen{iteration}_Var{variant}");

                return exported.ToGeometryResult();
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
                $"[GEOMETRY] Missing parameter '{name}'. Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
