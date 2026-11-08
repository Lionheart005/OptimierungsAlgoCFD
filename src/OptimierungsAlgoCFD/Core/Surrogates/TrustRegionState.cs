using System;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Steuerung des Vertrauensbereichs nach dem Vorbild von TuRBO (TODO-57, F21): Bei vielen
    /// Parametern sucht der Bayes-Optimierer nicht im ganzen Raum, sondern in einem Würfel um den
    /// besten Punkt. Der Würfel wächst nach Erfolgen und schrumpft nach Misserfolgen. Fällt er unter
    /// das Minimum, beginnt die Suche an einer neuen Stelle.
    /// </summary>
    public sealed class TrustRegionState
    {
        /// <summary>Ab so vielen freien Parametern schaltet sich der Vertrauensbereich bei "Auto" zu.</summary>
        public const int AutoThreshold = 10;

        public const double InitialLength = 0.8;
        public const double MaximumLength = 1.6;
        /// <summary>0,5⁷: nach sieben Halbierungen ist der Bereich ausgereizt.</summary>
        public static readonly double MinimumLength = Math.Pow(0.5, 7);

        /// <summary>Erfolge in Folge, nach denen die Seitenlänge verdoppelt wird.</summary>
        public const int SuccessTolerance = 3;

        private int _successes;
        private int _failures;

        /// <param name="freeDimensions">Anzahl der freien Parameter.</param>
        /// <param name="variantsPerIteration">Varianten je Iteration (V).</param>
        public TrustRegionState(int freeDimensions, int variantsPerIteration)
        {
            int v = Math.Max(1, variantsPerIteration);
            FailureTolerance = (int)Math.Ceiling(Math.Max(4.0 / v, freeDimensions / (double)v));
        }

        /// <summary>Aktuelle Seitenlänge L (im Einheitswürfel).</summary>
        public double Length { get; private set; } = InitialLength;

        /// <summary>Misserfolge in Folge, nach denen L halbiert wird: ⌈max(4/V, d/V)⌉.</summary>
        public int FailureTolerance { get; }

        public int Successes => _successes;
        public int Failures => _failures;

        /// <summary>Ist der Bereich so klein geworden, dass ein Neustart an anderer Stelle sinnvoll ist?</summary>
        public bool NeedsRestart => Length < MinimumLength;

        /// <summary>Bei "Auto" ab <see cref="AutoThreshold"/> freien Parametern aktiv, sonst "On" oder "Off".</summary>
        public static bool IsActive(string? setting, int freeDimensions)
        {
            switch ((setting ?? "Auto").Trim().ToLowerInvariant())
            {
                case "on": return true;
                case "off": return false;
                default: return freeDimensions >= AutoThreshold;
            }
        }

        /// <summary>
        /// Meldet das Ergebnis einer Runde. Drei Erfolge in Folge verdoppeln L (höchstens
        /// <see cref="MaximumLength"/>), <see cref="FailureTolerance"/> Misserfolge in Folge halbieren es.
        /// </summary>
        public void Register(bool success)
        {
            if (success)
            {
                _successes++;
                _failures = 0;
                if (_successes >= SuccessTolerance)
                {
                    Length = Math.Min(Length * 2.0, MaximumLength);
                    _successes = 0;
                }
            }
            else
            {
                _failures++;
                _successes = 0;
                if (_failures >= FailureTolerance)
                {
                    Length /= 2.0;
                    _failures = 0;
                }
            }
        }

        /// <summary>Neustart an anderer Stelle: L zurück auf den Anfangswert, Zähler leer.</summary>
        public void Restart()
        {
            Length = InitialLength;
            _successes = 0;
            _failures = 0;
        }
    }
}
