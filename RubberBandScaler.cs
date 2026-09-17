using System;
using System.Collections.Generic;

namespace MyPicoGkProject
{
    public class RubberBandScaler
    {
        public float ShrinkFactor { get; private set; }
        public Dictionary<string, float> ShrunkParameters { get; private set; }

        public RubberBandScaler(Dictionary<string, float> realParameters, float targetSize = 20.0f)
        {
            float maxDimension = 0f;
            foreach (var val in realParameters.Values)
            {
                if (val > maxDimension) maxDimension = val;
            }

            if (maxDimension <= 0f) maxDimension = 1f;

            ShrinkFactor = targetSize / maxDimension;

            ShrunkParameters = new Dictionary<string, float>();
            foreach (var kvp in realParameters)
            {
                ShrunkParameters.Add(kvp.Key, kvp.Value * ShrinkFactor);
            }
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