using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Feine Nachrechnung der Champions auf verfeinertem Netz (TODO-43).
    /// Überprüft die Netzunabhängigkeit der Optimierungsergebnisse.
    /// </summary>
    public static class ChampionFineVerifier
    {
        public const string VerificationCsvFileName = "Verifikation.csv";

        /// <summary>
        /// Berechnet den feinen Netzfaktor: wirksamer Faktor / (√2 ^ RefinementSteps).
        /// </summary>
        public static float CalculateFineFactor(float effectiveFactor, int refinementSteps = 1)
        {
            int steps = Math.Max(1, refinementSteps);
            return effectiveFactor / MathF.Pow(MathF.Sqrt(2.0f), steps);
        }

        /// <summary>
        /// Berechnet die maximale relative Abweichung aller überwachten Kennwerte zwischen normalem und feinem Lauf.
        /// </summary>
        public static float ComputeMaxRelativeDeviation(
            ModelRecord normalRecord,
            ModelRecord fineRecord,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            var metricsToCheck = new List<string>();

            if (monitoredMetrics != null && monitoredMetrics.Count > 0)
            {
                metricsToCheck.AddRange(monitoredMetrics);
            }
            else
            {
                // Fallback: alle numerischen passiven Parameter, die keine Framework-Metadaten sind
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ScaleFactor", "MeshCells", "MeshRefinementFactor", "FineVerified", "FineDeviation",
                    "BoundaryLayerHexShare", "ReynoldsNumber"
                };

                // Su2_* (Iterationen, Konvergenz, Schwingung, iterativer Fehler) und YPlus_* sind
                // Rechnungs-Metadaten, keine Kennwerte der Geometrie.
                metricsToCheck.AddRange(normalRecord.PassiveParameters.Keys.Where(k =>
                    !excluded.Contains(k)
                    && !ModelRecord.IsMeshMetadataKey(k)
                    && !k.StartsWith("Su2_", StringComparison.OrdinalIgnoreCase)
                    && !k.StartsWith("YPlus_", StringComparison.OrdinalIgnoreCase)));
            }

            if (metricsToCheck.Count == 0)
            {
                float refFit = Math.Max(Math.Abs(normalRecord.Fitness), 1e-7f);
                return Math.Abs(fineRecord.Fitness - normalRecord.Fitness) / refFit;
            }

            float maxDev = 0f;
            foreach (var metric in metricsToCheck)
            {
                if (normalRecord.PassiveParameters.TryGetValue(metric, out float qNormal))
                {
                    float qFine = fineRecord.PassiveParameters.TryGetValue(metric, out float qf) ? qf : 0f;
                    float dev = Math.Abs(qFine - qNormal) / Math.Max(Math.Abs(qNormal), 1e-7f);
                    if (dev > maxDev) maxDev = dev;
                }
            }

            return maxDev;
        }

        /// <summary>
        /// Größte <b>normierte</b> Abweichung (TODO-W1): <c>|fein − normal| / (Toleranz·|normal| + Mindestbetrag)</c>
        /// über alle Kennwerte; höchstens 1 = innerhalb der Toleranz. Bei Werten nahe 0 zählt der Mindestbetrag
        /// aus <see cref="MetricTolerance"/>, bei großen Werten die relative Abweichung. Ohne Kennwerte: Fitness.
        /// </summary>
        public static float ComputeMaxNormalizedDeviation(
            ModelRecord normalRecord,
            ModelRecord fineRecord,
            IReadOnlyCollection<string>? monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances)
        {
            var metrics = monitoredMetrics != null && monitoredMetrics.Count > 0
                ? monitoredMetrics
                : (IReadOnlyCollection<string>)Array.Empty<string>();

            if (metrics.Count == 0)
            {
                MetricUncertainty.TryGetFitness(normalRecord, Array.Empty<string>(), out double uNormal);
                MetricUncertainty.TryGetFitness(fineRecord, Array.Empty<string>(), out double uFine);
                return MetricTolerance.Normalized(fineRecord.Fitness, normalRecord.Fitness, MetricTolerance.WithUncertainty(tolerance, uNormal, uFine), 0f);
            }

            float max = 0f;
            foreach (string metric in metrics)
            {
                if (!normalRecord.PassiveParameters.TryGetValue(metric, out float qNormal)) continue;
                float qFine = fineRecord.PassiveParameters.TryGetValue(metric, out float qf) ? qf : 0f;

                // Schwankende Rechnungen (Wirbelablösung): die zulässige Abweichung wächst mit der Unsicherheit beider Werte.
                MetricUncertainty.TryGet(normalRecord, metric, out double uA);
                MetricUncertainty.TryGet(fineRecord, metric, out double uB);
                float n = MetricTolerance.Normalized(
                    qFine, qNormal, MetricTolerance.WithUncertainty(tolerance, uA, uB), MetricTolerance.AbsoluteFor(metric, absoluteTolerances));
                if (n > max) max = n;
            }
            return max;
        }

        /// <summary>
        /// Führt die Verifikation nach einer Iteration durch (Mode = "EachIteration").
        /// Ändert weder die Auswahl noch die History (Regel F7).
        /// </summary>
        public static void VerifyIterationWinner(
            ModelRecord winner,
            int iteration,
            SimulationContext context,
            Func<Dictionary<string, float>, float, string, ModelRecord> resimulate,
            Action<ModelRecord> evaluateFitness,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            string mode = (context.Config.ChampionVerification.Mode ?? "Off").Trim();
            if (!mode.Equals("EachIteration", StringComparison.OrdinalIgnoreCase)) return;

            float fineFactor = CalculateFineFactor(
                context.EffectiveMeshRefinementFactor,
                context.Config.ChampionVerification.RefinementSteps);

            string tag = $"Fein_Gen{iteration}";

            SimulationLog.Detail($"[VERIFIKATION] Iterationssieger Gen {iteration} wird fein nachgerechnet (Faktor: {fineFactor:0.00}, Tag: {tag})...");

            ModelRecord fineRecord = resimulate(winner.ActiveParameters, fineFactor, tag);
            evaluateFitness(fineRecord);

            float maxRelDev = ComputeMaxRelativeDeviation(winner, fineRecord, monitoredMetrics);
            float maxNorm = ComputeMaxNormalizedDeviation(winner, fineRecord, monitoredMetrics,
                context.Config.ChampionVerification.Tolerance, context.Config.MeshCalibration.AbsoluteTolerances);

            // Dokumentation am Record
            winner.PassiveParameters["FineVerified"] = 1f;
            winner.PassiveParameters["FineDeviation"] = maxRelDev;

            SimulationLog.Detail($"Normale Fitness: {winner.Fitness:F4} | Feine Fitness: {fineRecord.Fitness:F4} | Max. Kennwert-Abweichung: {maxRelDev * 100:0.0} %");

            if (maxNorm > 1.0f)
            {
                SimulationLog.Warning(
                    $"Ergebnis netzabhängig – Abweichung im feinen Netz beträgt {maxRelDev * 100:0.0} % "
                    + $"(Erlaubt: {context.Config.ChampionVerification.Tolerance * 100:0.0} % plus Mindestbetrag; normiert {maxNorm:0.0}, erlaubt höchstens 1) – Kalibrierung prüfen!");
            }
        }

        /// <summary>
        /// Führt die finale Verifikation am Ende der Optimierung durch (Mode = "Final" oder "FinalTopN").
        /// </summary>
        public static void VerifyFinal(
            SimulationContext context,
            Func<Dictionary<string, float>, float, string, ModelRecord> resimulate,
            Action<ModelRecord> evaluateFitness,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            string mode = (context.Config.ChampionVerification.Mode ?? "Off").Trim();

            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("EachIteration", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            float fineFactor = CalculateFineFactor(
                context.EffectiveMeshRefinementFactor,
                context.Config.ChampionVerification.RefinementSteps);

            var validRecords = context.History.Where(r => !r.SimulationFailed).ToList();
            if (validRecords.Count == 0) return;

            var itemsToVerify = new List<ModelRecord>();

            if (mode.Equals("FinalTopN", StringComparison.OrdinalIgnoreCase))
            {
                int topN = Math.Max(1, context.Config.ChampionVerification.TopN);
                itemsToVerify = validRecords.OrderByDescending(r => r.Fitness).Take(topN).ToList();
            }
            else // "Final"
            {
                ModelRecord? champ = WorkflowController.SelectBestRecord(validRecords);
                if (champ != null) itemsToVerify.Add(champ);
            }

            if (itemsToVerify.Count == 0) return;

            SimulationLog.PrintBanner(
                $"FINALE GEWINNER-VERIFIKATION (Modus: {mode})",
                $"Feiner Netzfaktor: {fineFactor:0.00} (Verfeinerungsstufen: {context.Config.ChampionVerification.RefinementSteps})",
                $"Zu prüfende Kandidaten: {itemsToVerify.Count}");

            var results = new List<VerificationRow>();

            for (int i = 0; i < itemsToVerify.Count; i++)
            {
                var candidate = itemsToVerify[i];
                // Der Tag nennt den Kandidaten (Fein1_Gen5_Var1): sonst hießen alle Dateien Gen99_Var99_Fein1.
                string tag = $"Fein{i + 1}_Gen{candidate.Iteration}_Var{candidate.Variant}";

                SimulationLog.Detail($"[VERIFIKATION] Prüfe Kandidat {i + 1}/{itemsToVerify.Count}: Iteration {candidate.Iteration}, Variante {candidate.Variant}...");

                ModelRecord fineRecord = resimulate(candidate.ActiveParameters, fineFactor, tag);
                evaluateFitness(fineRecord);

                float maxDev = ComputeMaxRelativeDeviation(candidate, fineRecord, monitoredMetrics);
                float maxNorm = ComputeMaxNormalizedDeviation(candidate, fineRecord, monitoredMetrics,
                    context.Config.ChampionVerification.Tolerance, context.Config.MeshCalibration.AbsoluteTolerances);

                // Eine gescheiterte feine Rechnung (Vernetzung oder SU2) verifiziert nichts.
                // Vorher stand dann FineVerified = 1 und "Rangfolge hält" (Servertest 06.11.2026).
                candidate.PassiveParameters["FineVerified"] = fineRecord.SimulationFailed ? 0f : 1f;
                candidate.PassiveParameters["FineDeviation"] = maxDev;

                results.Add(new VerificationRow
                {
                    Candidate = candidate,
                    FineRecord = fineRecord,
                    NormalRank = i + 1,
                    NormalFitness = candidate.Fitness,
                    FineFitness = fineRecord.Fitness,
                    MaxDeviation = maxDev,
                    MaxNormalized = maxNorm
                });
            }

            // Rangfolge im feinen Netz bestimmen
            var fineRanked = results.OrderByDescending(r => r.FineFitness).ToList();
            for (int r = 0; r < fineRanked.Count; r++)
            {
                fineRanked[r].FineRank = r + 1;
            }

            int failedFine = results.Count(r => r.FineRecord.SimulationFailed);
            bool rankingHolds = failedFine == 0 && results.All(r => r.NormalRank == r.FineRank);

            // Verifikation.csv schreiben
            WriteVerificationCsv(Path.Combine(context.WorkingDirectory, VerificationCsvFileName), results, rankingHolds, monitoredMetrics);

            // Simulation_Results.csv mit FineVerified/FineDeviation aktualisieren
            context.ExportToCsv();

            // Konsolenausgabe
            SimulationLog.PrintBanner("VERIFIKATIONSERGEBNIS");
            if (failedFine > 0)
            {
                SimulationLog.Warning($"{failedFine} von {results.Count} feinen Rechnungen sind gescheitert – für sie ist nichts verifiziert (FineVerified = 0).");
            }
            
            string rankingText = rankingHolds ? "JA (Rangfolge hält)" : failedFine > 0 ? "NICHT PRÜFBAR (feine Rechnung gescheitert)" : "NEIN (Rangfolge geändert)";
            if (rankingHolds)
            {
                SimulationLog.Success($"Rangfolge stabil im feinen Netz: {rankingText}");
            }
            else
            {
                SimulationLog.Warning($"Rangfolge stabil im feinen Netz: {rankingText}");
            }

            Console.WriteLine("Iter | Var | Normal-Rang | Fein-Rang | Normal-Score | Fein-Score | Max. Abw. | normiert (<= 1 ok)");
            Console.WriteLine("--------------------------------------------------------------------------------------------------");

            bool anyWarning = false;
            foreach (var row in results)
            {
                Console.WriteLine(
                    $"{row.Candidate.Iteration,4} | {row.Candidate.Variant,3} | {row.NormalRank,11} | {row.FineRank,9} | "
                    + $"{row.NormalFitness,12:F4} | {row.FineFitness,10:F4} | {row.MaxDeviation * 100,8:0.0} % | {row.MaxNormalized,8:0.00}");

                if (row.MaxNormalized > 1.0f)
                {
                    anyWarning = true;
                }
            }

            if (anyWarning)
            {
                Console.WriteLine();
                SimulationLog.Warning(
                    $"Ergebnis netzabhängig – Abweichung im feinen Netz übersteigt die Toleranz "
                    + $"von {context.Config.ChampionVerification.Tolerance * 100:0.0} % – Kalibrierung prüfen!");
            }
            Console.WriteLine("==================================================\n");
        }

        private static void WriteVerificationCsv(
            string filePath,
            List<VerificationRow> rows,
            bool rankingHolds,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            var sb = new StringBuilder();

            var metrics = monitoredMetrics != null && monitoredMetrics.Count > 0
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Header
            sb.Append("Iteration;Variant;NormalRank;FineRank;NormalFitness;FineFitness;MaxMetricDeviation;RankingHolds;MaxNormalizedDeviation");
            foreach (var m in metrics)
            {
                sb.Append($";{m}_Normal;{m}_Fine");
            }
            sb.AppendLine();

            foreach (var r in rows)
            {
                sb.Append($"{r.Candidate.Iteration};{r.Candidate.Variant};{r.NormalRank};{r.FineRank};");
                sb.Append($"{r.NormalFitness.ToString("F5", CultureInfo.InvariantCulture)};");
                sb.Append($"{r.FineFitness.ToString("F5", CultureInfo.InvariantCulture)};");
                sb.Append($"{r.MaxDeviation.ToString("P2", CultureInfo.InvariantCulture)};");
                sb.Append(rankingHolds ? "1" : "0");
                sb.Append($";{r.MaxNormalized.ToString("F3", CultureInfo.InvariantCulture)}");

                foreach (var m in metrics)
                {
                    float normVal = r.Candidate.PassiveParameters.TryGetValue(m, out float nv) ? nv : 0f;
                    float fineVal = r.FineRecord.PassiveParameters.TryGetValue(m, out float fv) ? fv : 0f;
                    sb.Append($";{normVal.ToString("F5", CultureInfo.InvariantCulture)};{fineVal.ToString("F5", CultureInfo.InvariantCulture)}");
                }
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private sealed class VerificationRow
        {
            public ModelRecord Candidate { get; set; } = null!;
            public ModelRecord FineRecord { get; set; } = null!;
            public int NormalRank { get; set; }
            public int FineRank { get; set; }
            public float NormalFitness { get; set; }
            public float FineFitness { get; set; }
            public float MaxDeviation { get; set; }
            public float MaxNormalized { get; set; }
        }
    }
}
