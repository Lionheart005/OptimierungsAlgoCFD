using System;
using System.Threading;
using PicoGK;

using FluidForge.Core;

namespace FluidForge.Kernels.PicoGk
{
    /// <summary>
    /// PicoGK geometry kernel.
    ///
    /// <c>Library.Go(voxelSize, body)</c> initializes the PicoGK runtime
    /// (voxel backend, log file) and then invokes <c>body</c>. No <c>Lattice</c> or
    /// <c>Voxels</c> instance may be created outside this call.
    ///
    /// Apart from <c>Projects/MantaAuv/</c>, this file is the only location outside
    /// project code that may import <c>using PicoGK</c>.
    /// </summary>
    public class PicoGkKernel : IGeometryKernel
    {
        public string Name => "PicoGK";

        // Library.Go expects a ThreadStart, which is signature-compatible with Action;
        // the conversion stays here so the framework core only depends on the neutral
        // Action delegate.
        //
        // bEndAppWithTask: true -- without this flag, Library.Go keeps the process open
        // until the viewer window is closed. Under xvfb-run nobody closes it, causing
        // headless runs to hang indefinitely after task completion while software OpenGL
        // keeps spinning in the background.
        public void RunHosted(float voxelResolution, Action body)
            => Library.Go(voxelResolution, new ThreadStart(body.Invoke), bEndAppWithTask: true);
    }
}
