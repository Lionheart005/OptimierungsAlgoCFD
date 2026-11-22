using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Prognose der Laufzeit (Plan zeitschaetzung.md).
    /// </summary>
    /// <param name="VariantSeconds">Mittlere Dauer einer Variante (gleitendes Fenster).</param>
    /// <param name="Remaining">Rest bis zum Ende der Optimierung, ohne die Feinprüfung am Ende.</param>
    /// <param name="IterationNumber">Die Iteration, auf die sich <paramref name="IterationRemaining"/> bezieht; 0 = keine mehr offen.</param>
    /// <param name="IterationRemaining">Rest bis zum Ende dieser Iteration (samt Nachprüfung des Champions).</param>
    /// <param name="FineVerification">Obergrenze für die Feinprüfung am Ende; <see cref="TimeSpan.Zero"/> = keine.</param>
    /// <param name="FineCandidates">So viele Kandidaten prüft die Feinprüfung am Ende.</param>
    /// <param name="UpperBound">Das Automatische Ende ist an: der Lauf kann früher enden, die Werte sind Höchstwerte.</param>
    public sealed record RunTimeEstimate(
        double VariantSeconds,
        TimeSpan Remaining,
        int IterationNumber,
        TimeSpan IterationRemaining,
        TimeSpan FineVerification,
        int FineCandidates,
        bool UpperBound);

    /// <summary>
    /// Schätzt die Restzeit eines Laufs. Reine Rechnung ohne Solver, damit sie ohne Server prüfbar ist.
    ///
    /// <para>
    /// Grundlage ist der Mittelwert der letzten <see cref="WindowSize"/> gerechneten Varianten, nicht aller
    /// bisherigen: Bayes wandert im Lauf zu größeren, teureren Formen, und der Gesamtschnitt hinkt dem hinterher
    /// (MantaAuv: Schätzung 30 % zu kurz). Eingerechnet werden die Nachprüfung des Champions am Ende jeder
    /// Iteration (eine Rechnung, so lang wie eine Variante) und die Feinprüfung nach der Optimierung
    /// (<c>ChampionVerification</c>).
    /// </para>
    /// </summary>
    public sealed class RunTimeEstimator
    {
        /// <summary>So viele der jüngsten Varianten bestimmen die mittlere Dauer.</summary>
        public const int WindowSize = 10;

        /// <summary>
        /// Wie stark die Zellzahl je √2-Verfeinerungsstufe wächst (als Potenz von √2): in 3D gemessen 2,4
        /// (MantaAuv, Kalibrierung 0,71 → 0,50: Faktor 2,28 statt 2,83, weil Wandschicht und Oberfläche nur
        /// quadratisch wachsen), in 2D höchstens 2.
        /// </summary>
        public const double CellGrowthExponent3D = 2.4;

        /// <summary>Siehe <see cref="CellGrowthExponent3D"/>.</summary>
        public const double CellGrowthExponent2D = 2.0;

        private readonly int _maxIterations;
        private readonly int _variantsPerIteration;
        private readonly string _verificationMode;
        private readonly int _verificationTopN;
        private readonly double _fineFactor;
        private readonly bool _autoStopEnabled;
        private readonly List<double> _samples = new();

        /// <param name="config">Liefert Iterations- und Variantenzahl sowie die Einstellungen der Feinprüfung.</param>
        /// <param name="dimensions">2 oder 3 (bestimmt, wie stark die Zellzahl bei der Feinprüfung wächst).</param>
        /// <param name="autoStopEnabled">Das Automatische Ende ist an: Prognose gilt als Höchstwert.</param>
        public RunTimeEstimator(SimulationConfig config, int dimensions = 3, bool autoStopEnabled = false)
        {
            _maxIterations = Math.Max(1, config.MaxIterations);
            _variantsPerIteration = Math.Max(1, config.VariantsPerIteration);
            _verificationMode = (config.ChampionVerification.Mode ?? "Off").Trim();
            _verificationTopN = Math.Max(1, config.ChampionVerification.TopN);
            _autoStopEnabled = autoStopEnabled;
            _fineFactor = FineCostFactor(config, dimensions);
        }

        /// <summary>Anzahl der Messwerte, die bisher im Fenster stehen können (höchstens <see cref="WindowSize"/> zählen).</summary>
        public int SampleCount => _samples.Count;

        /// <summary>
        /// Merkt sich die Dauer einer gerechneten Variante. Gescheiterte Varianten (Sekunden statt Stunden)
        /// und Werte ohne Messung gehören nicht hierher, sie würden den Schnitt nach unten ziehen.
        /// </summary>
        public void AddVariant(double seconds)
        {
            if (seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)) _samples.Add(seconds);
        }

        /// <summary>Mittlere Dauer der jüngsten Varianten; <c>null</c>, solange nichts gemessen ist.</summary>
        public double? RecentMeanSeconds
        {
            get
            {
                if (_samples.Count == 0) return null;
                return _samples.Skip(Math.Max(0, _samples.Count - WindowSize)).Average();
            }
        }

        /// <summary>
        /// Wie viel länger als eine normale Variante eine Feinprüfungs-Rechnung dauert: mehr Zellen
        /// (Verfeinerungsstufen) mal das strenge Iterationslimit der Kontrollrechnungen. Eine Obergrenze,
        /// denn die strenge Rechnung kann früher konvergieren.
        /// </summary>
        public static double FineCostFactor(SimulationConfig config, int dimensions)
        {
            int steps = Math.Max(1, config.ChampionVerification.RefinementSteps);
            double exponent = dimensions == 2 ? CellGrowthExponent2D : CellGrowthExponent3D;
            double cellFactor = Math.Pow(Math.Sqrt(2.0), steps * exponent);

            double normalIterations = Math.Max(1, config.Su2MaxIterations);
            double controlIterations = Math.Max(normalIterations, config.ControlRuns.Su2MaxIterations);
            return cellFactor * (controlIterations / normalIterations);
        }

        /// <summary>
        /// Prognose für den aktuellen Stand; <c>null</c>, solange keine Variante gemessen wurde.
        /// </summary>
        /// <param name="iteration">Laufende Iteration (ab 1).</param>
        /// <param name="variantsDoneInIteration">Fertige Varianten dieser Iteration (bei <paramref name="iterationFinished"/> egal).</param>
        /// <param name="iterationFinished">Die Iteration ist komplett, also auch Nachprüfung und Feinprüfung dieser Runde.</param>
        public RunTimeEstimate? Estimate(int iteration, int variantsDoneInIteration, bool iterationFinished)
        {
            double? mean = RecentMeanSeconds;
            if (mean == null) return null;

            double perVariant = mean.Value;
            double fineRun = perVariant * _fineFactor;
            bool fineEachIteration = _verificationMode.Equals("EachIteration", StringComparison.OrdinalIgnoreCase);
            double eachIterationExtra = fineEachIteration ? fineRun : 0.0;

            int iterationsAfter = Math.Max(0, _maxIterations - iteration);
            int doneNow = Math.Clamp(variantsDoneInIteration, 0, _variantsPerIteration);

            int variantsLeft = iterationsAfter * _variantsPerIteration + (iterationFinished ? 0 : _variantsPerIteration - doneNow);
            int validationsLeft = iterationsAfter + (iterationFinished ? 0 : 1);
            int fineEachLeft = fineEachIteration ? iterationsAfter + (iterationFinished ? 0 : 1) : 0;

            double remaining = perVariant * (variantsLeft + validationsLeft) + fineRun * fineEachLeft;

            int iterationNumber;
            double iterationRemaining;
            if (iterationFinished)
            {
                if (iterationsAfter == 0)
                {
                    iterationNumber = 0;
                    iterationRemaining = 0;
                }
                else
                {
                    iterationNumber = iteration + 1;
                    iterationRemaining = perVariant * (_variantsPerIteration + 1) + eachIterationExtra;
                }
            }
            else
            {
                iterationNumber = iteration;
                iterationRemaining = perVariant * (_variantsPerIteration - doneNow + 1) + eachIterationExtra;
            }

            int candidates = _verificationMode.Equals("Final", StringComparison.OrdinalIgnoreCase) ? 1
                : _verificationMode.Equals("FinalTopN", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1, Math.Min(_verificationTopN, _maxIterations * _variantsPerIteration))
                    : 0;

            return new RunTimeEstimate(
                VariantSeconds: perVariant,
                Remaining: TimeSpan.FromSeconds(remaining),
                IterationNumber: iterationNumber,
                IterationRemaining: TimeSpan.FromSeconds(iterationRemaining),
                FineVerification: TimeSpan.FromSeconds(fineRun * candidates),
                FineCandidates: candidates,
                UpperBound: _autoStopEnabled);
        }
    }
}
