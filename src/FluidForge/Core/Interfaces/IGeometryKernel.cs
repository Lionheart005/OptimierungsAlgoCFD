using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Runtime environment of the geometry kernel.
    ///
    /// Some kernels — such as PicoGK — must initialize their own host runtime before any
    /// geometry can be created, executing the workflow inside that callback. This abstraction
    /// keeps <c>Program.cs</c> decoupled from <c>Library.Go(...)</c> and PicoGK.
    ///
    /// Separated from <see cref="IGeometryGenerator"/> because the kernel is project-independent
    /// while the generator is project-specific. Kernels without a host runtime use
    /// <see cref="DirectGeometryKernel"/>.
    /// </summary>
    public interface IGeometryKernel
    {
        /// <summary>Kernel name for console output.</summary>
        string Name { get; }

        /// <summary>
        /// Starts the kernel and executes <paramref name="body"/> within its runtime environment.
        /// Returns only after <paramref name="body"/> completes.
        /// </summary>
        /// <param name="voxelResolution">
        /// Working resolution of the kernel (voxel size for PicoGK). Non-voxel kernels may ignore this.
        /// </param>
        /// <param name="body">The workflow to execute inside the kernel.</param>
        void RunHosted(float voxelResolution, Action body);
    }
}
