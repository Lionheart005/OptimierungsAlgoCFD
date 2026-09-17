using System;
using System.Collections.Generic;

namespace MyPicoGkProject
{
    public static class FitnessCalculator
    {
        public static void CalculateFitness(ModelRecord record, SimulationData data)
        {
            float drag = record.PassiveParameters.ContainsKey("Drag") ? record.PassiveParameters["Drag"] : float.MaxValue;
            float volume = record.PassiveParameters.ContainsKey("Volume") ? record.PassiveParameters["Volume"] : 0f;
            float sensorDist = record.PassiveParameters.ContainsKey("SensorDistance") ? record.PassiveParameters["SensorDistance"] : 0f;

            if (drag == float.MaxValue || float.IsNaN(drag))
            {
                record.Fitness = 0.0001f;
                return;
            }

            float minVol = data.Settings.OptimizationTargets["MinimumAllowedVolume"];
            float maxVol = data.Settings.OptimizationTargets["MaximumAllowedVolume"];
            float dragBalance = data.Settings.OptimizationTargets["DragBalanceFactor"];

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