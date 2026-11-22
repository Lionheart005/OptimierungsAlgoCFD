using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Geometry kernel without a host runtime: executes the workflow callback directly.
    ///
    /// Used by projects whose geometry generation does not require a host environment
    /// (mesh libraries, CAD exports, pre-built STL files) and by unit tests running
    /// <see cref="WorkflowController"/> without PicoGK.
    /// </summary>
    public class DirectGeometryKernel : IGeometryKernel
    {
        public string Name => "Direct (no host runtime)";

        public void RunHosted(float voxelResolution, Action body) => body();
    }
}
