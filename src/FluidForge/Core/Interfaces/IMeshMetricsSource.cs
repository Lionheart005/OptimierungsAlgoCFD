using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Optional for an <see cref="IMeshGenerator"/>: provides metrics of the most recently generated
    /// mesh (e.g., boundary layer count and thickness). <see cref="WorkflowController"/> copies them
    /// into <c>PassiveParameters</c> after meshing so they appear in the results CSV.
    /// </summary>
    public interface IMeshMetricsSource
    {
        /// <summary>Metrics from the most recent <see cref="IMeshGenerator.GenerateMesh(MeshRequest)"/> call.</summary>
        IReadOnlyDictionary<string, float> LastMeshMetrics { get; }
    }
}
