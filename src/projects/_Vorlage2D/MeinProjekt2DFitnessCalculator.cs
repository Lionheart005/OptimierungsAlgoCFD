using System;
using System.Collections.Generic;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MeinProjekt2D
{
    /// <summary>
    /// Die Bewertung eines zweidimensionalen CFD-Projekts (TODO-47).
    /// Diese Vorlage minimiert <b>nur den Widerstand</b>:
    ///
    /// <code>Fitness = 1 / Drag</code>
    ///
    /// <para>
    /// <b>Das Framework maximiert immer die Fitness.</b> Was klein werden soll, gehört in den
    /// Nenner; was groß werden soll, in den Zähler.
    /// </para>
    /// </summary>
    public sealed class MeinProjekt2DFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fitness eines Modells, das nicht bewertbar ist — abgebrochene Simulation oder fehlender Widerstand.
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Pflicht-Ziele aus <c>OptimizationTargets</c>.
        /// Leer, da diese Vorlagen-Formel nur den Widerstand braucht.
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metriken aus <c>PassiveParameters</c>, die für die Berechnung zwingend nötig sind.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag" };

        /// <summary>
        /// Prüft die Pflicht-Ziele sofort im Konstruktor.
        /// </summary>
        public MeinProjekt2DFitnessCalculator(ProjectConfig project)
        {
            var missing = RequiredTargets
                .Where(key => !project.OptimizationTargets.ContainsKey(key))
                .ToList();

            if (missing.Count > 0)
            {
                string present = project.OptimizationTargets.Count > 0
                    ? string.Join(", ", project.OptimizationTargets.Keys)
                    : "(keine)";

                throw new InvalidOperationException(
                    $"[KONFIGURATION] Projekt '{project.ProjectName}': in OptimizationTargets "
                    + $"fehlt/fehlen {string.Join(", ", missing)}. Vorhanden ist: {present}. "
                    + $"Nachtragen in src/projects/{project.ProjectName}/project.json.");
            }
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            if (record.SimulationFailed)
            {
                record.Fitness = FailedFitness;
                return;
            }

            float drag = record.PassiveParameters.TryGetValue("Drag", out float value)
                ? value
                : float.NaN;

            if (float.IsNaN(drag) || float.IsInfinity(drag))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Widerstandsbeiwert absichern gegen Vorzeichen und Division durch (nahe) 0:
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            record.Fitness = 1.0f / safeDrag;
        }
    }
}
