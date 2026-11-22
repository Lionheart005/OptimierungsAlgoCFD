using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Mesh generation failed (e.g., Gmsh failure) independently of the flow solver.
    /// <see cref="WorkflowController"/> marks the variant with <c>MeshFailed = 1</c>:
    /// a meshing failure is not a flow crash and should not deter the optimizer from that region.
    /// </summary>
    public sealed class MeshGenerationException : Exception
    {
        public MeshGenerationException(string message) : base(message) { }

        public MeshGenerationException(string message, Exception inner) : base(message, inner) { }
    }
}
