namespace FluidForge.Core
{
    /// <summary>
    /// Optional for an <see cref="IMeshGenerator"/>: reports the target surface cell size (mm)
    /// at refinement factor 1.0 so <see cref="VoxelResolutionAdvisor"/> can check resolution without solver coupling.
    /// </summary>
    public interface IMeshResolutionTarget
    {
        float TargetSurfaceCellSizeMm { get; }
    }
}
