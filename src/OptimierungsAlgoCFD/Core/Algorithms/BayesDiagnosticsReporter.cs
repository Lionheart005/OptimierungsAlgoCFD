using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OptimierungsAlgoCFD.Core
{
    internal sealed class BayesProposal
    {
        public double[] Unit = Array.Empty<double>();
        public Dictionary<string, float> Parameters = new();
        public bool FromModel;
        public bool Explorer;
        public double Median, Low, High;
    }

    internal sealed class BayesDiagnostics
    {
        public int TrainingPoints;
        public double[] Relevance = Array.Empty<double>();
    }

    internal sealed record BayesAccuracy(double MeanDeviation, int InBand, int Count)
    {
        public double Share => Count > 0 ? (double)InBand / Count : 0.0;
    }

    /// <summary>
    /// Vorhersagebänder, Modell-Treffsicherheit und Verlaufsprotokollierung (<c>Bayes_Verlauf.csv</c>) (TODO-58).
    /// </summary>
    internal sealed class BayesDiagnosticsReporter
    {
        private readonly SurrogateModelEnsemble _ensemble;
        private readonly List<string> _historyRows = new();

        public BayesDiagnosticsReporter(SurrogateModelEnsemble ensemble) => _ensemble = ensemble;

        public int HistoryRowCount => _historyRows.Count;

        public (double Median, double Low, double High) PredictFitnessBand(
            FittedEnsemble fit, double[] x, Dictionary<string, float> parameters)
        {
            if (_ensemble.ActiveMode == SurrogateTargetMode.Fitness)
            {
                SurrogateSlot slot = fit.Slots[0];
                var (mean, variance) = slot.Model.Predict(x);
                double predictiveVar = Math.Max(variance + slot.Model.ObservationNoiseVariance, 0.0);
                double sd = Math.Sqrt(predictiveVar);
                return (slot.Back(mean), slot.Back(mean - 1.96 * sd), slot.Back(mean + 1.96 * sd));
            }

            var predictions = fit.Slots.Select(s => s.Model.Predict(x)).ToArray();

            double Evaluate(Func<int, double> z)
            {
                var passive = new Dictionary<string, float>();
                for (int k = 0; k < fit.Slots.Count; k++)
                {
                    double predVar = Math.Max(predictions[k].Variance + fit.Slots[k].Model.ObservationNoiseVariance, 0.0);
                    double value = fit.Slots[k].Back(predictions[k].Mean + Math.Sqrt(predVar) * z(k));
                    if (!float.IsFinite((float)value)) return double.NaN;
                    passive[fit.Slots[k].Name] = (float)value;
                }
                return _ensemble.FitnessOf(parameters, passive);
            }

            double median = Evaluate(_ => 0.0);
            var samples = new List<double>();
            for (int s = 0; s < fit.Z.Length; s++)
            {
                int sample = s;
                double f = Evaluate(k => fit.Z[sample][k]);
                if (double.IsFinite(f)) samples.Add(f);
            }

            if (samples.Count == 0) return (median, median, median);
            samples.Sort();
            double low = samples[(int)Math.Floor(0.025 * (samples.Count - 1))];
            double high = samples[(int)Math.Ceiling(0.975 * (samples.Count - 1))];
            return (median, Math.Min(low, median), Math.Max(high, median));
        }

        public static void AttachPrediction(
            ModelRecord record, IReadOnlyDictionary<(int Iteration, int Variant), BayesProposal> issued)
        {
            if (!issued.TryGetValue((record.Iteration, record.Variant), out BayesProposal? proposal)) return;
            if (!SameParameters(record.ActiveParameters, proposal.Parameters)) return;

            record.PassiveParameters[BayesOptimizationAlgorithm.PredictedFitnessKey] = (float)proposal.Median;
            record.PassiveParameters[BayesOptimizationAlgorithm.PredictedLowKey] = (float)proposal.Low;
            record.PassiveParameters[BayesOptimizationAlgorithm.PredictedHighKey] = (float)proposal.High;
            record.PassiveParameters[BayesOptimizationAlgorithm.ExplorerKey] = proposal.Explorer ? 1f : 0f;
        }

        private static bool SameParameters(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out float other)) return false;
                if (Math.Abs(value - other) > 1e-6f * Math.Max(1f, Math.Abs(value))) return false;
            }
            return true;
        }

        /// <summary>
        /// Treffsicherheit der eben beendeten Runde: mittlere relative Abweichung zwischen Vorhersage und
        /// echter Fitness und Anteil der Varianten im 95-%-Band. Nur Modellvorschläge zählen.
        /// </summary>
        public static BayesAccuracy? MeasureAccuracy(IEnumerable<ModelRecord> records)
        {
            double deviationSum = 0.0;
            int inBand = 0, count = 0;

            foreach (ModelRecord r in records)
            {
                if (r.SimulationFailed || r.Disqualified) continue;
                if (!r.PassiveParameters.TryGetValue(BayesOptimizationAlgorithm.PredictedFitnessKey, out float predicted)) continue;
                r.PassiveParameters.TryGetValue(BayesOptimizationAlgorithm.PredictedLowKey, out float low);
                r.PassiveParameters.TryGetValue(BayesOptimizationAlgorithm.PredictedHighKey, out float high);

                double actual = r.Fitness;
                // Symmetrisch bezogen auf den Mittelwert beider Zahlen (0 bis 2): Teilen durch den Istwert allein
                // ergab Millionen Prozent, sobald ein Wert nahe null lag (z. B. Fitness 0 bei negativem Auftrieb).
                deviationSum += Math.Abs(predicted - actual) / Math.Max(0.5 * (Math.Abs(predicted) + Math.Abs(actual)), 1e-6);
                if (actual >= low && actual <= high) inBand++;
                count++;
            }

            return count == 0 ? null : new BayesAccuracy(deviationSum / count, inBand, count);
        }

        public static void ReportAccuracy(BayesAccuracy? accuracy)
        {
            if (accuracy == null) return;

            Console.WriteLine(
                $"[BAYES] Treffsicherheit der letzten Runde: Ø Abweichung {accuracy.MeanDeviation * 100:F0} %, "
                + $"{accuracy.InBand} von {accuracy.Count} im Erwartungsband.");

            if (accuracy.Share < 0.5)
                SimulationLog.Warning("[BAYES] Modell noch unsicher oder Ergebnisse stark verrauscht.");
        }

        public void LogSummary(FittedEnsemble fit, int trainingPoints, ParameterSpace space)
        {
            Console.WriteLine($"[BAYES] Ersatzmodell gelernt aus {trainingPoints} Rechnungen (Modus {_ensemble.ActiveMode}).");

            foreach (SurrogateSlot slot in fit.Slots)
            {
                IReadOnlyList<double>? relevance = slot.Model.ParameterRelevance;
                if (relevance == null || relevance.Count != space.Dimension) continue;

                var ranked = Enumerable.Range(0, space.Dimension)
                    .OrderByDescending(i => relevance[i])
                    .ThenBy(i => i)
                    .ToList();

                var parts = ranked.Take(8).Select(i => $"{space.FreeNames[i]} {Bar(relevance[i])}").ToList();
                if (ranked.Count > 8) parts.Add("…");
                Console.WriteLine($"[BAYES]   {slot.Name}: {string.Join("  ", parts)}");
            }
        }

        private static string Bar(double relevance)
        {
            int blocks = (int)Math.Round(Math.Clamp(relevance, 0.0, 1.0) * 10.0);
            return blocks <= 0 ? "▏" : new string('█', blocks);
        }

        public void AppendHistoryRow(
            SimulationContext context, ParameterSpace space, int iteration, string mode,
            int usable, double? trustRegionLength, BayesAccuracy? accuracy, double? bestFitness, BayesDiagnostics? diagnostics)
        {
            var inv = CultureInfo.InvariantCulture;
            var row = new StringBuilder();
            row.Append(iteration.ToString(inv)).Append(';');
            row.Append(mode).Append(';');
            row.Append((diagnostics?.TrainingPoints ?? usable).ToString(inv)).Append(';');
            row.Append(trustRegionLength.HasValue ? trustRegionLength.Value.ToString("F4", inv) : string.Empty).Append(';');
            row.Append(accuracy != null ? accuracy.MeanDeviation.ToString("F4", inv) : string.Empty).Append(';');
            row.Append(accuracy != null ? accuracy.Share.ToString("F3", inv) : string.Empty).Append(';');
            row.Append(bestFitness.HasValue ? bestFitness.Value.ToString("G6", inv) : string.Empty);

            for (int i = 0; i < space.Dimension; i++)
            {
                row.Append(';');
                if (diagnostics != null && diagnostics.Relevance.Length == space.Dimension)
                    row.Append(diagnostics.Relevance[i].ToString("F4", inv));
            }

            _historyRows.Add(row.ToString());

            try
            {
                string directory = Path.Combine(context.WorkingDirectory, "Analyseergebnisse");
                Directory.CreateDirectory(directory);

                var header = "Iteration;Modus;Lernpunkte;Vertrauensbereich_L;Abweichung_Mittel;Anteil_im_Band;BesteFitness;"
                    + string.Join(";", space.FreeNames.Select(n => "Relevanz_" + n));
                header = header.TrimEnd(';');

                var text = new StringBuilder();
                text.AppendLine(header);
                foreach (string line in _historyRows) text.AppendLine(line);

                File.WriteAllText(Path.Combine(directory, BayesOptimizationAlgorithm.HistoryFileName), text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SimulationLog.Warning($"[BAYES] {BayesOptimizationAlgorithm.HistoryFileName} konnte nicht geschrieben werden: {ex.Message}");
            }
        }
    }
}
