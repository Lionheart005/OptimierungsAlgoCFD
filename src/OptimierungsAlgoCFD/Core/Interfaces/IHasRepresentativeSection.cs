namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Optionale Schnittstelle für 3D-Projekte, die einen repräsentativen 2D-Querschnitt
    /// besitzen (z.B. Tragflügel, Profile, Leitbleche) und damit die 2D-Vorkalibrierung
    /// vor der 3D-Bestätigung nutzen können (TODO-48, Entscheidung E7).
    /// </summary>
    public interface IHasRepresentativeSection
    {
        IGeometryGenerator CreateSection2D(ProjectContext context);
    }
}
