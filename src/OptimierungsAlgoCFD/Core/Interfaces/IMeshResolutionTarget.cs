namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Optional für einen <see cref="IMeshGenerator"/>: meldet die Ziel-Oberflächenzellgröße (mm)
    /// bei Netzfaktor 1.0, damit <see cref="VoxelResolutionAdvisor"/> ohne Solver-Abhängigkeit prüfen kann.
    /// </summary>
    public interface IMeshResolutionTarget
    {
        float TargetSurfaceCellSizeMm { get; }
    }
}
