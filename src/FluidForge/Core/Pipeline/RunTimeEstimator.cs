using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Run-time forecast (time-estimation plan).
    /// </summary>
    /// <param name="VariantSeconds">Mean duration of one variant (sliding window).</param>
    /// <param name="Remaining">Remaining time until the end of optimization, excluding final fine verification.</param>
    /// <param name="IterationNumber">The iteration to which <paramref name="IterationRemaining"/> applies; 0 = none remaining.</param>
    /// <param name="IterationRemaining">Remaining time until the end of that iteration (including champion validation).</param>
    /// <param name="FineVerification">Upper bound for final fine verification; <see cref="TimeSpan.Zero"/> = none.</param>
    /// <param name="FineCandidates">Number of candidates checked during final fine verification.</param>
    /// <param name="UpperBound">Automatic stop is enabled: the run may finish earlier, so estimates are upper bounds.</param>
    public sealed record RunTimeEstimate(
        double VariantSeconds,
        TimeSpan Remaining,
        int IterationNumber,
        TimeSpan IterationRemaining,
        TimeSpan FineVerification,
        int FineCandidates,
        bool UpperBound);

    /// <summary>
    /// Estimates remaining run time. Pure math without solver dependencies so it can be unit-tested offline.
    ///
    /// <para>
    /// Based on the mean of the last <see cref="WindowSize"/> evaluated variants rather than all prior variants:
    /// Bayesian optimization drifts toward larger, more expensive geometries over time, causing a global average
    /// to underestimate (MantaAuv: underestimated by 30 %). Accounts for champion validation at the end of each
    /// iteration (one evaluation as long as a variant) and post-optimization fine verification (<c>ChampionVerification</c>).
    /// </para>
    /// </summary>
    public sealed class RunTimeEstimator
    {
        /// <summary>Number of most recent variants used to compute the mean duration.</summary>
        public const int WindowSize = 10;

        /// <summary>
        /// How strongly cell count grows per √2 refinement step (as a power of √2): measured at 2.4 in 3D
        /// (MantaAuv, calibration 0.71 -> 0.50: factor 2.28 instead of 2.83 because boundary layer and surface
        /// grow quadratically), and at most 2 in 2D.
        /// </summary>
        public const double CellGrowthExponent3D = 2.4;

        /// <summary>See <see cref="CellGrowthExponent3D"/>.</summary>
        public const double CellGrowthExponent2D = 2.0;

        private readonly int _maxIterations;
        private readonly int _variantsPerIteration;
        private readonly string _verificationMode;
        private readonly int _verificationTopN;
        private readonly double _fineFactor;
        private readonly bool _autoStopEnabled;
        private readonly List<double> _samples = new();

        /// <param name="config">Provides iteration/variant counts and fine-verification settings.</param>
        /// <param name="dimensions">2 or 3 (determines cell growth during fine verification).</param>
        /// <param name="autoStopEnabled">Automatic stop is enabled: forecast represents an upper bound.</param>
        public RunTimeEstimator(SimulationConfig config, int dimensions = 3, bool autoStopEnabled = false)
        {
            _maxIterations = Math.Max(1, config.MaxIterations);
            _variantsPerIteration = Math.Max(1, config.VariantsPerIteration);
            _verificationMode = (config.ChampionVerification.Mode ?? "Off").Trim();
            _verificationTopN = Math.Max(1, config.ChampionVerification.TopN);
            _autoStopEnabled = autoStopEnabled;
            _fineFactor = FineCostFactor(config, dimensions);
        }

        /// <summary>Total number of duration samples recorded so far (at most <see cref="WindowSize"/> contribute).</summary>
        public int SampleCount => _samples.Count;

        /// <summary>
        /// Records the duration of a completed variant. Failed variants (seconds instead of hours)
        /// and unmeasured values are excluded so they do not skew the average downward.
        /// </summary>
        public void AddVariant(double seconds)
        {
            if (seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)) _samples.Add(seconds);
        }

        /// <summary>Mean duration of the most recent variants; <c>null</c> until at least one sample is recorded.</summary>
        public double? RecentMeanSeconds
        {
            get
            {
                if (_samples.Count == 0) return null;
                return _samples.Skip(Math.Max(0, _samples.Count - WindowSize)).Average();
            }
        }

        /// <summary>
        /// Cost multiplier of one fine-verification run relative to a normal variant: more cells
        /// (refinement steps) times the stricter control-run iteration limit. This is an upper bound
        /// because the stricter run may converge earlier.
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
        /// Estimates remaining time for the current progress; <c>null</c> until at least one variant has been measured.
        /// </summary>
        /// <param name="iteration">Current iteration (1-based).</param>
        /// <param name="variantsDoneInIteration">Completed variants in this iteration (ignored when <paramref name="iterationFinished"/> is true).</param>
        /// <param name="iterationFinished">True when the entire iteration (including validation and per-iteration verification) has finished.</param>
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
