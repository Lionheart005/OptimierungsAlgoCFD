using System;
using System.Collections.Generic;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.TestprojektBaars
{
    /// <summary>
    /// Die Bewertung für das NACA-Flügelprofil im Testprojekt Baars.
    /// Maximiert die aerodynamische Gleitzahl:
    /// <code>Fitness = CL / CD (Gleitzahl E = Lift / Drag)</code>
    /// </summary>
    public sealed class TestprojektBaarsFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fitness eines Modells, das nicht bewertbar ist — abgebrochene Simulation,
        /// fehlender oder ungültiger Auftrieb/Widerstand oder negativer Auftrieb.
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Pflicht-Ziele aus <c>OptimizationTargets</c>.
        /// Leer, da die Gleitzahl rein aus den CFD-Beiwerten berechnet wird.
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metriken aus <c>PassiveParameters</c>, die für die Berechnung zwingend nötig sind.
        /// Sowohl Drag (CD) als auch Lift (CL) kommen aus der SU2-history.csv via solvers/su2.json.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "Lift" };

        /// <summary>
        /// Prüft die Pflicht-Ziele sofort im Konstruktor.
        /// </summary>
        public TestprojektBaarsFitnessCalculator(ProjectConfig project)
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

            bool hasDrag = record.PassiveParameters.TryGetValue("Drag", out float drag);
            bool hasLift = record.PassiveParameters.TryGetValue("Lift", out float lift);

            if (!hasDrag || !hasLift ||
                float.IsNaN(drag) || float.IsInfinity(drag) ||
                float.IsNaN(lift) || float.IsInfinity(lift))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Widerstandsbeiwert absichern gegen Vorzeichen und Division durch (nahe) 0:
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            // Aerodynamische Gleitzahl E = CL / CD:
            float glideRatio = lift / safeDrag;
            record.PassiveParameters["GlideRatio"] = glideRatio;

            // Deckelung nach unten bei FailedFitness, damit negativer oder verschwindender Auftrieb
            // nicht zu negativer oder dominierend schlechter Fitness führt und ChampionValidator stabil bleibt:
            record.Fitness = Math.Max(glideRatio, FailedFitness);
        }
    }
}
