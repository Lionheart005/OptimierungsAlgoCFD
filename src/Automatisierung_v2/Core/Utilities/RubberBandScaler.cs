using System;
using System.Collections.Generic;

namespace MyPicoGkProject
{
    /// <summary>
    /// Skaliert Geometrie-Parameter auf PicoGK-Arbeitsgröße und zurück.
    /// 
    /// BUG-FIX: Skaliert jetzt NUR dimensionsbehaftete Parameter (mm).
    /// Dimensionslose Parameter wie TailTaper werden NICHT mehr skaliert.
    /// Die Info, welche Parameter dimensional sind, kommt aus der ProjectConfig.
    /// </summary>
    public class RubberBandScaler
    {
        public float ShrinkFactor { get; private set; }
        public Dictionary<string, float> ShrunkParameters { get; private set; }

        /// <summary>
        /// Erzeugt je nach Schalter einen echten oder einen neutralen Scaler. Dadurch braucht
        /// der Aufrufer kein <c>if</c> — der Aus-Fall ist einfach die Identität.
        /// </summary>
        public static RubberBandScaler Create(
            bool enabled,
            Dictionary<string, float> realParameters,
            HashSet<string> dimensionalParameters,
            float targetSize = 20.0f)
        {
            return enabled
                ? new RubberBandScaler(realParameters, dimensionalParameters, targetSize)
                : CreateNeutral(realParameters);
        }

        /// <summary>
        /// Neutraler Scaler: ShrinkFactor 1.0, Parameter unverändert, alle Restore-Methoden
        /// sind die Identität (weil sie mit 1/1 potenziert multiplizieren).
        /// </summary>
        public static RubberBandScaler CreateNeutral(Dictionary<string, float> realParameters)
            => new RubberBandScaler(realParameters);

        private RubberBandScaler(Dictionary<string, float> realParameters)
        {
            ShrinkFactor = 1.0f;
            ShrunkParameters = new Dictionary<string, float>(realParameters);
        }

        /// <summary>
        /// Erstellt einen neuen Scaler mit expliziter Liste dimensionaler Parameter.
        /// </summary>
        /// <param name="realParameters">Die echten Parameter in mm.</param>
        /// <param name="dimensionalParameters">Parameter die Längen-Dimensionen haben und skaliert werden sollen.</param>
        /// <param name="targetSize">Zielgröße für PicoGK in mm.</param>
        public RubberBandScaler(
            Dictionary<string, float> realParameters, 
            HashSet<string> dimensionalParameters,
            float targetSize = 20.0f)
        {
            // ShrinkFactor basiert nur auf dimensionalen Parametern
            float maxDimension = 0f;
            foreach (var kvp in realParameters)
            {
                if (dimensionalParameters.Contains(kvp.Key) && kvp.Value > maxDimension)
                {
                    maxDimension = kvp.Value;
                }
            }

            if (maxDimension <= 0f) maxDimension = 1f;

            ShrinkFactor = targetSize / maxDimension;

            // Nur dimensionale Parameter skalieren, Rest unverändert durchreichen
            ShrunkParameters = new Dictionary<string, float>();
            foreach (var kvp in realParameters)
            {
                if (dimensionalParameters.Contains(kvp.Key))
                {
                    ShrunkParameters.Add(kvp.Key, kvp.Value * ShrinkFactor);
                }
                else
                {
                    ShrunkParameters.Add(kvp.Key, kvp.Value); // Dimensionslos → unverändert!
                }
            }
        }

        public float RestoreLength(float picoGkLength)
        {
            return picoGkLength * (1.0f / ShrinkFactor);
        }

        public float RestoreVolume(float picoGkVolume)
        {
            return picoGkVolume * (float)Math.Pow(1.0f / ShrinkFactor, 3);
        }

        public float RestoreArea(float picoGkArea)
        {
            return picoGkArea * (float)Math.Pow(1.0f / ShrinkFactor, 2);
        }
    }
}

