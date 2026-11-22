using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Scales geometry parameters to the kernel's working size and restores output metrics.
    /// Scales ONLY dimensional parameters (mm); dimensionless parameters (e.g., TailTaper)
    /// are passed through unchanged based on <see cref="ProjectConfig.DimensionalParameters"/>.
    /// </summary>
    public class RubberBandScaler
    {
        public float ShrinkFactor { get; private set; }
        public Dictionary<string, float> ShrunkParameters { get; private set; }

        /// <summary>
        /// Creates an active or neutral scaler depending on <paramref name="enabled"/>,
        /// so callers do not need conditional branching.
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
        /// Neutral scaler: ShrinkFactor 1.0, parameters unchanged, all Restore methods act as identity.
        /// </summary>
        public static RubberBandScaler CreateNeutral(Dictionary<string, float> realParameters)
            => new RubberBandScaler(realParameters);

        private RubberBandScaler(Dictionary<string, float> realParameters)
        {
            ShrinkFactor = 1.0f;
            ShrunkParameters = new Dictionary<string, float>(realParameters);
        }

        /// <summary>
        /// Creates a scaler with an explicit set of dimensional parameter names.
        /// </summary>
        /// <param name="realParameters">Physical parameters in mm.</param>
        /// <param name="dimensionalParameters">Parameter names with length units to scale.</param>
        /// <param name="targetSize">Target kernel working size in mm.</param>
        public RubberBandScaler(
            Dictionary<string, float> realParameters, 
            HashSet<string> dimensionalParameters,
            float targetSize = 20.0f)
        {
            // ShrinkFactor is based solely on dimensional parameters
            float maxDimension = 0f;
            foreach (var kvp in realParameters)
            {
                if (dimensionalParameters.Contains(kvp.Key) && kvp.Value > maxDimension)
                {
                    maxDimension = kvp.Value;
                }
            }

            ShrinkFactor = maxDimension > 0f ? targetSize / maxDimension : 1.0f;

            // Scale dimensional parameters only; pass dimensionless ones through unchanged
            ShrunkParameters = new Dictionary<string, float>();
            foreach (var kvp in realParameters)
            {
                if (dimensionalParameters.Contains(kvp.Key))
                {
                    ShrunkParameters.Add(kvp.Key, kvp.Value * ShrinkFactor);
                }
                else
                {
                    ShrunkParameters.Add(kvp.Key, kvp.Value); // Dimensionless -> unchanged
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
