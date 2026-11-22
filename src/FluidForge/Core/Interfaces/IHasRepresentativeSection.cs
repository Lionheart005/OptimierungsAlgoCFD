namespace FluidForge.Core
{
    /// <summary>
    /// Optional interface for 3D projects that have a representative 2D cross-section
    /// (e.g., wings, airfoils, fins) and can therefore use 2D pre-calibration before 3D confirmation.
    /// </summary>
    public interface IHasRepresentativeSection
    {
        IGeometryGenerator CreateSection2D(ProjectContext context);
    }
}
