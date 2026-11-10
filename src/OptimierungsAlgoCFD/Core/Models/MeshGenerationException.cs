using System;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Die Vernetzung ist gescheitert (z. B. Gmsh-Abbruch), unabhängig von der Strömungsrechnung.
    /// Der <see cref="WorkflowController"/> markiert die Variante dann mit <c>MeshFailed = 1</c>:
    /// ein Vernetzungsfehler ist kein Strömungsabsturz und darf die Suche nicht von der ganzen
    /// Formzone abschrecken (Plan wandschicht_zellzahl.md, FZ7).
    /// </summary>
    public sealed class MeshGenerationException : Exception
    {
        public MeshGenerationException(string message) : base(message) { }

        public MeshGenerationException(string message, Exception inner) : base(message, inner) { }
    }
}
