using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FluidForge.Core
{
    /// <summary>
    /// Optimizes the acquisition function (Expected Improvement, explorer, trust region,
    /// batch repulsion, and Kriging Believer) on a fitted <see cref="FittedEnsemble"/>.
    /// </summary>
    internal sealed class AcquisitionOptimizer
    {
        private const double MinSeparation = 1e-3;
        private const int FullSamples = SurrogateModelEnsemble.FullSamples;
        private const int ScreenSamples = 16;
        private const int ScreenSurvivors = 200;
        private const int LocalStarts = 10;
        private const int LocalSteps = 30;

        /// <summary>Number of standard deviations above the mean for the optimistic estimate in "Fitness" mode (Metrics: best of 128 samples, approx. 2.4).</summary>
        private const double OptimismSigmas = 2.4;

        /// <summary>Candidates with a repulsion factor below this threshold (crash zone, near failures) are excluded from fallback and explorer selection.</summary>
        private const double MinRepulsionFactor = 0.5;

        private readonly BayesTuning _tuning;
        private readonly Random _random;
        private readonly SurrogateModelEnsemble _ensemble;

        private TrustRegionState? _trust;
        private double[]? _trustCenter;
        private HashSet<ModelRecord>? _trustSnapshot;
        private double? _trustLastBest;

        public AcquisitionOptimizer(BayesTuning tuning, Random random, SurrogateModelEnsemble ensemble)
        {
            _tuning = tuning;
            _random = random;
            _ensemble = ensemble;
        }

        public bool TrustRegionActive => _trust != null;
        public double? TrustRegionLength => _trust?.Length;
        public double[]? TrustCenter => _trustCenter;
        public int FallbackSelections { get; private set; }
        public bool LastRoundHadModelPoints { get; set; }

        public void InitializeTrustRegion(int dimension, int variants)
        {
            if (TrustRegionState.IsActive(_tuning.TrustRegion, dimension))
                _trust = new TrustRegionState(dimension, variants);
        }

        public void UpdateTrustRegionCounters(SurrogateTrainingSet training)
        {
            if (_trust == null) return;

            var (_, best) = RegionalBest(training);
            if (double.IsNegativeInfinity(best)) return;

            if (_trustLastBest.HasValue && LastRoundHadModelPoints)
            {
                double previous = _trustLastBest.Value;
                bool success = best > previous + 1e-3 * Math.Max(Math.Abs(previous), 1e-12);
                _trust.Register(success);
            }

            _trustLastBest = best;
        }

        public (double[] Lo, double[] Hi) PrepareTrustBounds(
            FittedEnsemble fit, SurrogateTrainingSet training, ParameterSpace space, double[] bestPoint)
        {
            int d = space.Dimension;
            double[] lo = new double[d];
            double[] hi = Enumerable.Repeat(1.0, d).ToArray();
            if (_trust == null) return (lo, hi);

            if (_trust.NeedsRestart)
            {
                Console.WriteLine($"{OutputConventions.BayesTag} Trust region exhausted; restarting in a new area.");
                _trustCenter = FindRestartCenter(fit, space);
                _trust.Restart();
                _trustSnapshot = new HashSet<ModelRecord>(training.Records);
                _trustLastBest = null;
            }
            else
            {
                var (regionalCenter, _) = RegionalBest(training);
                if (regionalCenter != null) _trustCenter = regionalCenter;
            }

            double[] center = _trustCenter ?? bestPoint;
            (lo, hi) = TrustBox(center, _trust.Length, fit.Relevance);
            Console.WriteLine($"{OutputConventions.BayesTag} Trust region: L = {_trust.Length:F3} (successes {_trust.Successes}, failures {_trust.Failures}/{_trust.FailureTolerance}).");
            return (lo, hi);
        }

        private (double[]? Center, double Best) RegionalBest(SurrogateTrainingSet training)
        {
            IReadOnlyList<ModelRecord> records = training.FeasibleRecords.Count > 0 ? training.FeasibleRecords : training.Records;
            IReadOnlyList<double[]> inputs = training.FeasibleRecords.Count > 0 ? training.FeasibleInputs : training.Inputs;

            int best = -1;
            for (int i = 0; i < records.Count; i++)
            {
                if (_trustSnapshot != null && _trustSnapshot.Contains(records[i])) continue;
                if (best < 0 || records[i].Fitness > records[best].Fitness) best = i;
            }

            return best < 0 ? (null, double.NegativeInfinity) : (inputs[best], records[best].Fitness);
        }

        private static (double[] Lo, double[] Hi) TrustBox(double[] center, double length, double[] relevance)
        {
            int d = center.Length;
            var logWeights = new double[d];
            for (int i = 0; i < d; i++) logWeights[i] = -Math.Log(Math.Max(relevance[i], 1e-6));
            double mean = logWeights.Average();

            var lo = new double[d];
            var hi = new double[d];
            for (int i = 0; i < d; i++)
            {
                double w = Math.Clamp(Math.Exp(logWeights[i] - mean), 0.05, 20.0);
                double half = 0.5 * length * w;
                lo[i] = Math.Max(0.0, center[i] - half);
                hi[i] = Math.Min(1.0, center[i] + half);
            }
            return (lo, hi);
        }

        /// <summary>New restart center: the most promising point in a uniform pool over the entire space.</summary>
        private double[] FindRestartCenter(FittedEnsemble fit, ParameterSpace space)
        {
            int d = space.Dimension;
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 5), d, _random);

            double[] values;
            if (fit.Slots.Count > 0 && fit.ConstraintSlots.Count == 0 && _ensemble.ActiveMode == SurrogateTargetMode.Fitness)
            {
                values = new double[pool.Length];
                SurrogateSlot slot = fit.Slots[0];
                for (int i = 0; i < pool.Length; i++)
                {
                    var (mean, variance) = slot.Model.Predict(pool[i]);
                    values[i] = mean + 2.0 * Math.Sqrt(variance);
                }
            }
            else
            {
                values = Score(fit, space, pool, ScreenSamples).Ei;
            }

            int best = 0;
            for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
            return pool[best];
        }

        public double[][] BuildPool(SurrogateTrainingSet training, double[] lo, double[] hi, int size)
        {
            int d = lo.Length;
            var pool = new double[size][];
            bool hasAnchors = training.Records.Count > 0;
            int uniform = hasAnchors ? size / 2 : size;

            double[][] sobol = SobolSequence.GenerateShifted(uniform, d, _random);
            for (int i = 0; i < uniform; i++)
            {
                var p = new double[d];
                for (int k = 0; k < d; k++) p[k] = lo[k] + sobol[i][k] * (hi[k] - lo[k]);
                pool[i] = p;
            }

            if (hasAnchors)
            {
                IReadOnlyList<ModelRecord> anchorRecords = training.FeasibleRecords.Count > 0 ? training.FeasibleRecords : training.Records;
                IReadOnlyList<double[]> anchorInputs = training.FeasibleRecords.Count > 0 ? training.FeasibleInputs : training.Inputs;

                var bestPoints = Enumerable.Range(0, anchorRecords.Count)
                    .OrderByDescending(i => anchorRecords[i].Fitness).ThenBy(i => i)
                    .Take(5).Select(i => anchorInputs[i]).ToList();

                for (int i = uniform; i < size; i++)
                {
                    double[] anchor = bestPoints[(i - uniform) % bestPoints.Count];
                    var p = new double[d];
                    for (int k = 0; k < d; k++)
                        p[k] = Math.Clamp(anchor[k] + 0.1 * NormalDistribution.Sample(_random), lo[k], hi[k]);
                    pool[i] = p;
                }
            }

            return pool;
        }

        private static double[][] UniformPool(int size, int d, Random random)
            => SobolSequence.GenerateShifted(size, d, random);

        private static double SquaredDistance(double[] a, double[] b)
        {
            double sum = 0.0;
            for (int k = 0; k < a.Length; k++)
            {
                double diff = a[k] - b[k];
                sum += diff * diff;
            }
            return sum;
        }

        private static bool IsTooClose(double[] x, IEnumerable<double[]> others)
        {
            foreach (double[] other in others)
                if (SquaredDistance(x, other) < MinSeparation * MinSeparation) return true;
            return false;
        }

        public double[]? PickByExpectedImprovement(
            FittedEnsemble fit, ParameterSpace space, double[][] pool,
            List<double[]> known, List<double[]> chosen, double[] lo, double[] hi)
        {
            bool monteCarlo = fit.Slots.Count > 0 && fit.HasFeasibleIncumbent && _ensemble.ActiveMode == SurrogateTargetMode.Metrics;

            // Stage 1: full pool (in "Metrics" mode pre-screened with a small sample count)
            var (ei, _, _, pred, rep, pFeas) = Score(fit, space, pool, monteCarlo ? ScreenSamples : FullSamples);
            var order = Enumerable.Range(0, pool.Length).OrderByDescending(i => ei[i]).ThenBy(i => i).ToList();

            var candidates = new List<double[]>();
            var scores = new List<double>();
            if (monteCarlo)
            {
                var survivors = order.Take(ScreenSurvivors).Select(i => pool[i]).ToList();
                var refined = Score(fit, space, survivors, FullSamples);
                candidates.AddRange(survivors);
                scores.AddRange(refined.Ei);
            }
            else
            {
                candidates.AddRange(pool);
                scores.AddRange(ei);
            }

            RefineLocally(fit, space, candidates, scores, lo, hi);

            // Best candidate not colliding with an existing evaluation or another proposal.
            double bestScore = scores.Count > 0 ? scores.Max() : 0.0;
            if (!(bestScore > fit.MeaningfulImprovement))
            {
                // Nowhere is meaningful improvement expected: the model is very confident, or the
                // current best is an upward outlier (coarse mesh, vortex shedding). Instead of
                // wasting rounds on boundary uncertainty, refine where predicted fitness is highest;
                // exploration is handled by the dedicated explorer slot.
                FallbackSelections++;

                bool hasConstraints = fit.ConstraintSlots.Count > 0;
                var shortlist = Enumerable.Range(0, pool.Length)
                    .Where(i => rep[i] > MinRepulsionFactor
                                && double.IsFinite(pred[i])
                                && (!hasConstraints || !fit.HasFeasibleIncumbent || pFeas[i] >= 0.25))
                    .OrderByDescending(i => pred[i]).ThenBy(i => i).ToList();

                if (shortlist.Count == 0)
                    shortlist = Enumerable.Range(0, pool.Length)
                        .Where(i => rep[i] > MinRepulsionFactor && double.IsFinite(pred[i]))
                        .OrderByDescending(i => hasConstraints ? pFeas[i] : pred[i])
                        .ThenByDescending(i => pred[i]).ThenBy(i => i).ToList();

                if (shortlist.Count == 0)
                    shortlist = Enumerable.Range(0, pool.Length).OrderByDescending(i => pred[i]).ThenBy(i => i).ToList();

                return shortlist.Select(i => pool[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
            }

            var ranking = Enumerable.Range(0, candidates.Count).OrderByDescending(i => scores[i]).ThenBy(i => i);
            return ranking.Select(i => candidates[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
        }

        private void RefineLocally(
            FittedEnsemble fit, ParameterSpace space, List<double[]> candidates, List<double> scores, double[] lo, double[] hi)
        {
            // Local refinement of the top candidates
            var top = Enumerable.Range(0, candidates.Count).OrderByDescending(i => scores[i]).ThenBy(i => i).Take(LocalStarts).ToList();
            foreach (int start in top)
            {
                double[] x = (double[])candidates[start].Clone();
                double score = scores[start];
                double sigma = 0.05;

                for (int step = 0; step < LocalSteps; step++)
                {
                    var trial = new double[x.Length];
                    for (int k = 0; k < x.Length; k++)
                        trial[k] = Math.Clamp(x[k] + sigma * (hi[k] - lo[k]) * NormalDistribution.Sample(_random), lo[k], hi[k]);

                    double trialScore = Score(fit, space, new[] { trial }, FullSamples).Ei[0];
                    if (trialScore > score) { x = trial; score = trialScore; }
                    else sigma *= 0.5;
                }

                candidates.Add(x);
                scores.Add(score);
            }
        }

        /// <summary>
        /// Explorer slot: highest uncertainty, restricted to points the model still considers plausible
        /// (safety net against an overconfident wrong model). Points that the model is confident are
        /// poor only waste an evaluation and explore nothing useful.
        /// </summary>
        public double[]? PickExplorer(
            FittedEnsemble fit, ParameterSpace space, List<double[]> known, List<double[]> chosen, int d)
        {
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 2), d, _random);
            int samples = fit.Slots.Count > 0 && fit.HasFeasibleIncumbent && _ensemble.ActiveMode == SurrogateTargetMode.Metrics
                ? ScreenSamples
                : 1;
            var (_, unc, opt, _, rep, pFeas) = Score(fit, space, pool, samples);

            bool hasConstraints = fit.ConstraintSlots.Count > 0;
            List<int> plausible = Enumerable.Range(0, pool.Length)
                .Where(i => rep[i] > MinRepulsionFactor
                            && opt[i] >= -fit.PlausibleGap
                            && (!hasConstraints || pFeas[i] >= 0.25))
                .ToList();

            IEnumerable<int> ranking = plausible.Count > 0
                ? plausible.OrderByDescending(i => unc[i]).ThenBy(i => i)
                : Enumerable.Range(0, pool.Length)
                    .OrderByDescending(i => hasConstraints ? pFeas[i] : opt[i])
                    .ThenByDescending(i => opt[i])
                    .ThenBy(i => i);

            return ranking.Select(i => pool[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
        }

        /// <summary>
        /// Expected improvement and uncertainty for a batch of candidates, multiplied by
        /// constraint feasibility probability <c>∏ P(margin_k &gt;= 0)</c> and repulsion from failed locations.
        /// </summary>
        /// <remarks>
        /// <c>Opt</c> is the optimistic improvement: the best sample ("Metrics" mode) or
        /// <c>μ + 2.4·σ</c> ("Fitness" mode), each relative to the current best. <c>Pred</c> is the
        /// improvement of predicted (mean) fitness without uncertainty. <c>Rep</c> is the
        /// repulsion factor (0..1).
        /// </remarks>
        private (double[] Ei, double[] Unc, double[] Opt, double[] Pred, double[] Rep, double[] PFeasible) Score(
            FittedEnsemble fit, ParameterSpace space, IReadOnlyList<double[]> points, int samples)
        {
            int n = points.Count, m = fit.Slots.Count, c = fit.ConstraintSlots.Count;
            var mean = new double[m][];
            var variance = new double[m][];
            for (int s = 0; s < m; s++) { mean[s] = new double[n]; variance[s] = new double[n]; }

            var cMean = new double[c][];
            var cVar = new double[c][];
            for (int k = 0; k < c; k++) { cMean[k] = new double[n]; cVar[k] = new double[n]; }

            void Predict(int i)
            {
                for (int s = 0; s < m; s++)
                    (mean[s][i], variance[s][i]) = fit.Slots[s].Model.Predict(points[i]);
                for (int k = 0; k < c; k++)
                    (cMean[k][i], cVar[k][i]) = fit.ConstraintSlots[k].Model.Predict(points[i]);
            }

            if (n >= 64) Parallel.For(0, n, Predict);
            else for (int i = 0; i < n; i++) Predict(i);

            var ei = new double[n];
            var unc = new double[n];
            var opt = new double[n];
            var pred = new double[n];
            var rep = new double[n];
            var pFeas = new double[n];

            for (int i = 0; i < n; i++)
            {
                double u = 0.0;
                for (int s = 0; s < m; s++) u += variance[s][i] / fit.Slots[s].OutputVariance;
                if (m == 0)
                {
                    for (int k = 0; k < c; k++) u += cVar[k][i];
                }
                unc[i] = u;

                double pFeasible = 1.0;
                for (int k = 0; k < c; k++)
                {
                    double obsNoise = fit.ConstraintSlots[k].Model.ObservationNoiseVariance;
                    double cSd = Math.Sqrt(Math.Max(cVar[k][i] + obsNoise, 0.0));
                    double cMu = cMean[k][i];
                    double pk = cSd < 1e-12 ? (cMu >= 0.0 ? 1.0 : 0.0) : NormalDistribution.Cdf(cMu / cSd);
                    pFeasible *= pk;
                }
                pFeas[i] = pFeasible;

                if (c > 0 && !fit.HasFeasibleIncumbent)
                {
                    // Pure feasibility search until at least one feasible point is found.
                    ei[i] = pFeasible;
                    pred[i] = pFeasible;
                    opt[i] = pFeasible;
                }
                else if (m > 0)
                {
                    if (_ensemble.ActiveMode == SurrogateTargetMode.Fitness)
                    {
                        double sd = Math.Sqrt(variance[0][i]);
                        double improvement = mean[0][i] - fit.BestTransformed - fit.Offset;
                        opt[i] = improvement + OptimismSigmas * sd;
                        pred[i] = improvement;
                        if (sd < 1e-12) ei[i] = Math.Max(improvement, 0.0);
                        else { double z = improvement / sd; ei[i] = improvement * NormalDistribution.Cdf(z) + sd * NormalDistribution.Pdf(z); }
                    }
                    else
                    {
                        (ei[i], opt[i]) = MonteCarloImprovement(fit, space, points[i], mean, variance, i, samples);
                        pred[i] = PredictedImprovement(fit, space, points[i], mean, i);
                    }

                    if (c > 0)
                    {
                        ei[i] *= pFeasible;
                    }
                }

                double repulsion = Repulsion(fit, points[i]);
                rep[i] = repulsion;
                ei[i] *= repulsion;
                unc[i] *= repulsion;
            }

            return (ei, unc, opt, pred, rep, pFeas);
        }

        /// <summary>Fitness improvement resulting from predicted (mean) metrics without uncertainty.</summary>
        private double PredictedImprovement(FittedEnsemble fit, ParameterSpace space, double[] point, double[][] mean, int index)
        {
            var passive = new Dictionary<string, float>();
            for (int k = 0; k < fit.Slots.Count; k++)
            {
                double value = fit.Slots[k].Back(mean[k][index]);
                if (!double.IsFinite(value) || !float.IsFinite((float)value)) return double.NegativeInfinity;
                passive[fit.Slots[k].Name] = (float)value;
            }

            double f = _ensemble.FitnessOf(space.FromUnit(point), passive);
            if (!double.IsFinite(f) || (fit.FitnessLog && f <= 0.0)) return double.NegativeInfinity;
            return fit.T(f) - fit.BestTransformed;
        }

        /// <returns>Mean improvement (expected improvement) and maximum improvement across samples (optimistic).</returns>
        private (double Mean, double Max) MonteCarloImprovement(
            FittedEnsemble fit, ParameterSpace space, double[] point, double[][] mean, double[][] variance, int index, int samples)
        {
            Dictionary<string, float> active = space.FromUnit(point);
            var passive = new Dictionary<string, float>();
            double sum = 0.0, max = double.NegativeInfinity;
            int used = Math.Min(samples, fit.Z.Length);

            for (int s = 0; s < used; s++)
            {
                passive.Clear();
                bool valid = true;
                for (int k = 0; k < fit.Slots.Count; k++)
                {
                    double t = mean[k][index] + Math.Sqrt(variance[k][index]) * fit.Z[s][k];
                    double value = fit.Slots[k].Back(t);
                    if (!double.IsFinite(value) || !float.IsFinite((float)value)) { valid = false; break; }
                    passive[fit.Slots[k].Name] = (float)value;
                }
                if (!valid) continue;

                double f = _ensemble.FitnessOf(active, passive);
                if (!double.IsFinite(f) || (fit.FitnessLog && f <= 0.0)) continue;

                double improvement = fit.T(f) - fit.BestTransformed;
                if (improvement > max) max = improvement;
                if (improvement > 0.0) sum += improvement;
            }

            return (used > 0 ? sum / used : 0.0, max);
        }

        /// <summary>
        /// Selection damping (factor 0..1): success probability from the success model,
        /// plus hard repulsion around failed/rejected points (radius <c>0.05·√d</c>) and
        /// already chosen proposals in the same round at half radius (capped at 0.05 so it
        /// does not over-penalize in high dimensions).
        /// </summary>
        private static double Repulsion(FittedEnsemble fit, double[] x)
        {
            double factor = 1.0;
            if (fit.SuccessModel != null)
                factor *= Math.Clamp(fit.SuccessModel.Predict(x).Mean, 0.0, 1.0);
            factor *= RepulsionFrom(fit.Excluded, x, fit.RepulsionRadius);
            factor *= RepulsionFrom(fit.Chosen, x, Math.Min(0.5 * fit.RepulsionRadius, 0.05));
            return factor;
        }

        private static double RepulsionFrom(IReadOnlyList<double[]> points, double[] x, double radius)
        {
            if (points.Count == 0) return 1.0;

            double r2 = 2.0 * radius * radius;
            double factor = 1.0;
            foreach (double[] e in points)
                factor *= 1.0 - Math.Exp(-SquaredDistance(x, e) / r2);
            return factor;
        }

        /// <summary>Kriging Believer: treats the proposal as evaluated with the predicted mean as outcome.</summary>
        public void Believe(FittedEnsemble fit, double[] x, Dictionary<string, float> parameters)
        {
            foreach (ConstraintSlot cSlot in fit.ConstraintSlots)
            {
                double cMean = cSlot.Model.Predict(x).Mean;
                cSlot.Model.AddPseudoObservation(x, cMean);
            }

            if (fit.Slots.Count == 0) return;

            var passive = new Dictionary<string, float>();
            var means = new double[fit.Slots.Count];

            for (int k = 0; k < fit.Slots.Count; k++)
            {
                means[k] = fit.Slots[k].Model.Predict(x).Mean;
                fit.Slots[k].Model.AddPseudoObservation(x, means[k]);
                passive[fit.Slots[k].Name] = (float)fit.Slots[k].Back(means[k]);
            }

            if (!fit.HasFeasibleIncumbent) return;

            double believed;
            if (_ensemble.ActiveMode == SurrogateTargetMode.Fitness)
            {
                believed = means[0];
            }
            else
            {
                double f = _ensemble.FitnessOf(parameters, passive);
                if (!double.IsFinite(f) || (fit.FitnessLog && f <= 0.0)) return;
                believed = fit.T(f);
            }

            // Cap believed incumbent lift at 0.25 * PlausibleGap above the real observed best so an
            // optimistic extrapolation cannot wipe out EI around known good regions for the rest of the batch.
            double cap = fit.InitialBestTransformed + 0.25 * fit.PlausibleGap;
            double clamped = Math.Min(believed, cap);
            if (clamped > fit.BestTransformed) fit.BestTransformed = clamped;
        }
    }
}
