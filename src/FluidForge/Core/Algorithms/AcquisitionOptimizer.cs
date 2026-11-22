using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FluidForge.Core
{
    /// <summary>
    /// Optimiert die Akquisitionsfunktion (Expected Improvement, Erkunder, Vertrauensbereich,
    /// Batch-Abstoßung und Kriging Believer) auf einem gelernten <see cref="FittedEnsemble"/>.
    /// </summary>
    internal sealed class AcquisitionOptimizer
    {
        private const double MinSeparation = 1e-3;
        private const int FullSamples = SurrogateModelEnsemble.FullSamples;
        private const int ScreenSamples = 8;
        private const int ScreenSurvivors = 200;
        private const int LocalStarts = 10;
        private const int LocalSteps = 30;

        /// <summary>Wie viele Standardabweichungen über dem Mittel die optimistische Schätzung im Modus "Fitness" liegt (Metrics: bester von 64 Stichproben, etwa 2,4).</summary>
        private const double OptimismSigmas = 2.4;

        /// <summary>Kandidaten mit einem Abstoßungsfaktor darunter (Absturzzone, Nähe zu Fehlschlägen) kommen für die Ausweichregel und den Erkunder nicht in Frage.</summary>
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
                Console.WriteLine("[BAYES] Vertrauensbereich ausgereizt, erkunde neues Gebiet.");
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
            Console.WriteLine($"[BAYES] Vertrauensbereich: L = {_trust.Length:F3} (Erfolge {_trust.Successes}, Misserfolge {_trust.Failures}/{_trust.FailureTolerance}).");
            return (lo, hi);
        }

        private (double[]? Center, double Best) RegionalBest(SurrogateTrainingSet training)
        {
            int best = -1;
            for (int i = 0; i < training.Records.Count; i++)
            {
                if (_trustSnapshot != null && _trustSnapshot.Contains(training.Records[i])) continue;
                if (best < 0 || training.Records[i].Fitness > training.Records[best].Fitness) best = i;
            }

            return best < 0 ? (null, double.NegativeInfinity) : (training.Inputs[best], training.Records[best].Fitness);
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

        /// <summary>Neues Zentrum: der aussichtsreichste Punkt in einem gleichverteilten Pool über den ganzen Raum.</summary>
        private double[] FindRestartCenter(FittedEnsemble fit, ParameterSpace space)
        {
            int d = space.Dimension;
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 5), d, _random);

            double[] values;
            if (_ensemble.ActiveMode == SurrogateTargetMode.Fitness)
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
            int uniform = size / 2;

            for (int i = 0; i < uniform; i++)
            {
                var p = new double[d];
                for (int k = 0; k < d; k++) p[k] = lo[k] + _random.NextDouble() * (hi[k] - lo[k]);
                pool[i] = p;
            }

            var bestPoints = Enumerable.Range(0, training.Records.Count)
                .OrderByDescending(i => training.Records[i].Fitness).ThenBy(i => i)
                .Take(5).Select(i => training.Inputs[i]).ToList();

            for (int i = uniform; i < size; i++)
            {
                double[] anchor = bestPoints[(i - uniform) % bestPoints.Count];
                var p = new double[d];
                for (int k = 0; k < d; k++)
                    p[k] = Math.Clamp(anchor[k] + 0.1 * NormalDistribution.Sample(_random), lo[k], hi[k]);
                pool[i] = p;
            }

            return pool;
        }

        private static double[][] UniformPool(int size, int d, Random random)
        {
            var pool = new double[size][];
            for (int i = 0; i < size; i++)
            {
                var p = new double[d];
                for (int k = 0; k < d; k++) p[k] = random.NextDouble();
                pool[i] = p;
            }
            return pool;
        }

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
            bool monteCarlo = _ensemble.ActiveMode == SurrogateTargetMode.Metrics;

            // Stufe 1: ganzer Pool (im Modus "Metrics" mit wenigen Zufallssätzen vorsortiert)
            var (ei, _, _, pred, rep) = Score(fit, space, pool, monteCarlo ? ScreenSamples : FullSamples);
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

            // Bester Kandidat, der nicht auf einer Rechnung oder einem anderen Vorschlag liegt.
            double bestScore = scores.Count > 0 ? scores.Max() : 0.0;
            if (!(bestScore > fit.MeaningfulImprovement))
            {
                // Nirgends ist eine nennenswerte Verbesserung zu erwarten: das Modell ist sich sehr sicher, oder der
                // bisher Beste ist ein Ausreißer nach oben (grobes Netz, Wirbelablösung). Bis 08.11.2026 galt hier
                // schon "exakt null" als Auslöser und die Regel hieß "größte Unsicherheit im ganzen Raum": Das
                // verbrauchte ganze Runden an den Rändern des Suchraums (Serverlauf: Anstellwinkel 15 bis 30°), obwohl
                // das Modell dort schlechte Werte vorhersagte. Auch winzige Restwerte der erwarteten Verbesserung
                // (lange Schwänze des Verhältnisses Lift/Drag im Strömungsabriss) lenkten dorthin. Jetzt wird
                // verfeinert: weiter dort, wo die vorhergesagte Fitness am höchsten ist. Das Erkunden übernimmt der
                // Erkunder-Platz.
                FallbackSelections++;

                var shortlist = Enumerable.Range(0, pool.Length)
                    .Where(i => rep[i] > MinRepulsionFactor && double.IsFinite(pred[i]))
                    .OrderByDescending(i => pred[i]).ThenBy(i => i).ToList();
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
            // Lokale Nachschärfung der besten Kandidaten
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
        /// Der Erkunder: größte Unsicherheit, aber nur unter Punkten, die nach Ansicht des Modells noch gut sein
        /// könnten (Sicherheitsnetz gegen ein selbstsicheres, falsches Modell). Punkte, von denen das Modell
        /// ziemlich sicher weiß, dass sie schlecht sind, kosten nur eine Rechnung und erkunden nichts.
        /// </summary>
        public double[]? PickExplorer(
            FittedEnsemble fit, ParameterSpace space, List<double[]> known, List<double[]> chosen, int d)
        {
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 2), d, _random);
            var (_, unc, opt, _, rep) = Score(fit, space, pool, _ensemble.ActiveMode == SurrogateTargetMode.Metrics ? ScreenSamples : 1);

            List<int> plausible = Enumerable.Range(0, pool.Length)
                .Where(i => rep[i] > MinRepulsionFactor && opt[i] >= -fit.PlausibleGap)
                .ToList();

            IEnumerable<int> ranking = plausible.Count > 0
                ? plausible.OrderByDescending(i => unc[i]).ThenBy(i => i)
                : Enumerable.Range(0, pool.Length).OrderByDescending(i => opt[i]).ThenBy(i => i);

            return ranking.Select(i => pool[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
        }

        /// <summary>
        /// Erwartete Verbesserung und Unsicherheit für viele Kandidaten. Beides ist mit der Abstoßung von
        /// fehlgeschlagenen Orten multipliziert.
        /// </summary>
        /// <remarks>
        /// <c>Opt</c> ist die optimistische Verbesserung: der beste Wert unter den Stichproben (Modus "Metrics")
        /// bzw. <c>μ + 2,4·σ</c> (Modus "Fitness"), jeweils gegen den bisher Besten gerechnet. <c>Pred</c> ist die
        /// Verbesserung der vorhergesagten (mittleren) Fitness, ohne Unsicherheit. <c>Rep</c> ist der
        /// Abstoßungsfaktor (0..1).
        /// </remarks>
        private (double[] Ei, double[] Unc, double[] Opt, double[] Pred, double[] Rep) Score(
            FittedEnsemble fit, ParameterSpace space, IReadOnlyList<double[]> points, int samples)
        {
            int n = points.Count, m = fit.Slots.Count;
            var mean = new double[m][];
            var variance = new double[m][];
            for (int s = 0; s < m; s++) { mean[s] = new double[n]; variance[s] = new double[n]; }

            void Predict(int i)
            {
                for (int s = 0; s < m; s++)
                    (mean[s][i], variance[s][i]) = fit.Slots[s].Model.Predict(points[i]);
            }

            if (n >= 64) Parallel.For(0, n, Predict);
            else for (int i = 0; i < n; i++) Predict(i);

            var ei = new double[n];
            var unc = new double[n];
            var opt = new double[n];
            var pred = new double[n];
            var rep = new double[n];

            for (int i = 0; i < n; i++)
            {
                double u = 0.0;
                for (int s = 0; s < m; s++) u += variance[s][i] / fit.Slots[s].OutputVariance;
                unc[i] = u;

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

                double repulsion = Repulsion(fit, points[i]);
                rep[i] = repulsion;
                ei[i] *= repulsion;
                unc[i] *= repulsion;
            }

            return (ei, unc, opt, pred, rep);
        }

        /// <summary>Verbesserung der Fitness, die sich aus den vorhergesagten (mittleren) Kennwerten ergibt; ohne Unsicherheit.</summary>
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

        /// <returns>Mittlere Verbesserung (erwartete Verbesserung) und größte Verbesserung unter den Stichproben (optimistisch).</returns>
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
        /// Dämpfung der Auswahlregel (Faktor 0..1): Erfolgswahrscheinlichkeit aus dem Erfolgsmodell,
        /// dazu die harte Abstoßung um fehlgeschlagene und verworfene Orte (Radius <c>0,05·√d</c>), die bereits gewählten Vorschläge derselben Runde mit dem halben Radius (höchstens 0,05, sonst wirkt er bei vielen Parametern zu stark):
        /// Kriging Believer allein reicht nicht, wenn das Modell kaum Streuung hat (gemessen am
        /// 08.11.2026: vier Vorschläge einer Runde lagen fast aufeinander).
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

        /// <summary>Kriging Believer: der Vorschlag gilt als gerechnet, mit der vorhergesagten Mitte als Ergebnis.</summary>
        public void Believe(FittedEnsemble fit, double[] x, Dictionary<string, float> parameters)
        {
            var passive = new Dictionary<string, float>();
            var means = new double[fit.Slots.Count];

            for (int k = 0; k < fit.Slots.Count; k++)
            {
                means[k] = fit.Slots[k].Model.Predict(x).Mean;
                fit.Slots[k].Model.AddPseudoObservation(x, means[k]);
                passive[fit.Slots[k].Name] = (float)fit.Slots[k].Back(means[k]);
            }

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

            if (believed > fit.BestTransformed) fit.BestTransformed = believed;
        }
    }
}
