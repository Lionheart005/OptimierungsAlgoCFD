using System;

namespace MyPicoGkProject
{
    /// <summary>
    /// Manta-Ray AUV Fitness-Berechnung.
    /// Formel: Fitness = (Volume * SensorDistance) / Drag^DragBalanceFactor
    /// </summary>
    public class MantaFitnessCalculator : IFitnessCalculator
    {
        private readonly ProjectConfig _project;

        public MantaFitnessCalculator(ProjectConfig project)
        {
            _project = project;
        }

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

            float minVol = _project.OptimizationTargets["MinimumAllowedVolume"];
            float maxVol = _project.OptimizationTargets["MaximumAllowedVolume"];
            float dragBalance = _project.OptimizationTargets["DragBalanceFactor"];

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
