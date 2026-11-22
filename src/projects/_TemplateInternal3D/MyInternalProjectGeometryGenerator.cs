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
        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float lengthMm = Read(parameters, "LengthMm");
            float radiusMm = Read(parameters, "RadiusMm");

            BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildStraightPipe(lengthMm, radiusMm);
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
