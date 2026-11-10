using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Optional für einen <see cref="IMeshGenerator"/>: liefert Kennwerte des zuletzt erzeugten Netzes
    /// (z. B. Schichtzahl und Dicke der Wandschicht). Der <see cref="WorkflowController"/> übernimmt sie
    /// nach jeder Vernetzung in die <c>PassiveParameters</c> der Variante, damit sie in der CSV stehen.
    /// </summary>
    public interface IMeshMetricsSource
    {
        /// <summary>Kennwerte des letzten <see cref="IMeshGenerator.GenerateMesh(MeshRequest)"/>-Aufrufs.</summary>
        IReadOnlyDictionary<string, float> LastMeshMetrics { get; }
    }
}
