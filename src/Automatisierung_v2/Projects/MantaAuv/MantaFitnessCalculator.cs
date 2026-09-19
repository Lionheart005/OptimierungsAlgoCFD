using System;
using System.Collections.Generic;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MantaAuv
{
    /// <summary>
    /// Manta-Ray AUV Fitness-Berechnung.
    /// Formel: Fitness = (Volume * SensorDistance) / Drag^DragBalanceFactor
    /// </summary>
    public class MantaFitnessCalculator : IFitnessCalculator
    {
        /// <summary>Ziele, ohne die sich die Fitness-Formel nicht rechnen lässt.</summary>
        private static readonly string[] RequiredTargets =
        {
            "MinimumAllowedVolume", "MaximumAllowedVolume", "DragBalanceFactor"
        };

        /// <summary>
        /// Metriken, ohne die die Formel nicht rechnet: Drag kommt aus der SU2-history.csv
        /// (Zuordnung in su2.json unter 'ResultMetrics'), Volume und SensorDistance meldet
        /// der <see cref="MantaGeometryGenerator"/>.
        ///
        /// FrontalArea steht bewusst nicht hier: die Fitness braucht sie nicht, sie geht als
        /// REF_AREA in den Solver — und der warnt seit TODO-12 selbst, wenn sie fehlt.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "Volume", "SensorDistance" };

        private readonly float _minimumAllowedVolume;
        private readonly float _maximumAllowedVolume;
        private readonly float _dragBalanceFactor;

        /// <summary>
        /// Prüft die Pflicht-Ziele sofort: fehlt eines, bricht der Lauf hier ab —
        /// beim Verdrahten in <c>Program.cs</c>, vor der ersten Simulation. Vorher kam
        /// mitten im Lauf eine nackte <see cref="KeyNotFoundException"/>, nach Stunden
        /// Rechenzeit (TODO-16).
        /// </summary>
        /// <exception cref="InvalidOperationException">Ein Pflicht-Ziel fehlt in der Konfiguration.</exception>
        public MantaFitnessCalculator(ProjectConfig project)
        {
            var missing = RequiredTargets.Where(key => !project.OptimizationTargets.ContainsKey(key)).ToList();

            if (missing.Count > 0)
            {
                string present = project.OptimizationTargets.Count > 0
                    ? string.Join(", ", project.OptimizationTargets.Keys)
                    : "(keine)";

                throw new InvalidOperationException(
                    $"[KONFIGURATION] Projekt '{project.ProjectName}': in OptimizationTargets fehlt/fehlen " +
                    $"{string.Join(", ", missing)}. Die Manta-Fitness braucht {string.Join(", ", RequiredTargets)}. " +
                    $"Vorhanden ist: {present}. Nachtragen in config/projects/{project.ProjectName}.json " +
                    $"(oder in MantaProjectConfig.Create()).");
            }

            _minimumAllowedVolume = project.OptimizationTargets["MinimumAllowedVolume"];
            _maximumAllowedVolume = project.OptimizationTargets["MaximumAllowedVolume"];
            _dragBalanceFactor = project.OptimizationTargets["DragBalanceFactor"];
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            // Abgebrochene Vernetzung/Simulation: der Kern meldet das generisch per Flag,
            // früher stand dafür ein "Drag" = float.MaxValue im Record.
            if (record.SimulationFailed)
            {
                record.Fitness = 0.0001f;
                return;
            }

            float drag = record.PassiveParameters.ContainsKey("Drag") ? record.PassiveParameters["Drag"] : float.MaxValue;
            float volume = record.PassiveParameters.ContainsKey("Volume") ? record.PassiveParameters["Volume"] : 0f;
            float sensorDist = record.PassiveParameters.ContainsKey("SensorDistance") ? record.PassiveParameters["SensorDistance"] : 0f;

            if (drag == float.MaxValue || float.IsNaN(drag))
            {
                record.Fitness = 0.0001f;
                return;
            }

            // Die Ziele sind im Konstruktor geprüft und übernommen worden.
            float minVol = _minimumAllowedVolume;
            float maxVol = _maximumAllowedVolume;
            float dragBalance = _dragBalanceFactor;

            // 1. Sichere Werte für den Nenner garantieren
            float realDrag = Math.Abs(drag);
            float safeDrag = Math.Max(realDrag, 0.001f); 
            
            // 2. Die mathematische Fitness-Gleichung aus dem AUV-Konzept
            float adjustedDrag = (float)Math.Pow(safeDrag, dragBalance);               

            // Fitness = (Volume * SensorDistance) / adjustedDrag
            record.Fitness = (volume * sensorDist) / adjustedDrag;

            // 3. Harte Strafen (Penalty)
            if (volume < minVol || volume > maxVol) 
            {
                record.Fitness *= 0.1f; // Harte Strafe, aber nicht direkt 0, damit Reste der Form bewertet werden
            }
        }
    }
}
