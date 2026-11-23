using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FluidForge.Core
{
    /// <summary>
    /// Pure-Core Pareto dominance, 2D hypervolume, and Pareto artifact export utilities.
    /// </summary>
    public static class ParetoFrontCalculator
    {
        /// <summary>
        /// Computes a boolean mask marking all non-dominated, feasible, valid records in <paramref name="records"/>.
        /// Respects each objective's <see cref="OptimizationDirection"/> (<c>Minimize</c> vs. <c>Maximize</c>).
        /// </summary>
        public static bool[] ComputeParetoMask(IReadOnlyList<ModelRecord> records)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));

            bool[] mask = new bool[records.Count];
            if (records.Count == 0) return mask;

            // Eligible records: non-failed, non-disqualified, feasible, >= 1 objective, all finite.
            var eligible = new List<int>();
            for (int i = 0; i < records.Count; i++)
            {
                ModelRecord r = records[i];
                if (IsParetoEligible(r))
                {
                    eligible.Add(i);
                }
            }

            if (eligible.Count == 0) return mask;

            // Use canonical objective names & directions from the first eligible record.
            ModelRecord reference = records[eligible[0]];
            var objSpecs = reference.Objectives
                .Select(o => (o.Name, o.Direction))
                .ToList();

            // Build minimized vectors (every objective negated if Maximize).
            var vectors = new Dictionary<int, double[]>(eligible.Count);
            foreach (int idx in eligible)
            {
                ModelRecord r = records[idx];
                double[]? vec = TryExtractMinimizedVector(r, objSpecs);
                if (vec != null)
                {
                    vectors[idx] = vec;
                }
            }

            List<int> validIndices = vectors.Keys.ToList();
            for (int a = 0; a < validIndices.Count; a++)
            {
                int idxA = validIndices[a];
                double[] vecA = vectors[idxA];
                bool dominated = false;

                for (int b = 0; b < validIndices.Count; b++)
                {
                    if (a == b) continue;
                    int idxB = validIndices[b];
                    if (DominatesMinimized(vectors[idxB], vecA))
                    {
                        dominated = true;
                        break;
                    }
                }

                mask[idxA] = !dominated;
            }

            return mask;
        }

        /// <summary>
        /// Computes the exact 2D hypervolume dominated by <paramref name="minimizedPoints"/>
        /// relative to <paramref name="referencePoint"/> (both objectives minimized).
        /// Points with <c>F1 &gt;= R1</c> or <c>F2 &gt;= R2</c> are ignored.
        /// </summary>
        public static double ComputeHypervolume2D(
            IReadOnlyList<(double F1, double F2)> minimizedPoints,
            (double R1, double R2) referencePoint)
        {
            if (minimizedPoints == null || minimizedPoints.Count == 0)
                return 0.0;

            if (!double.IsFinite(referencePoint.R1) || !double.IsFinite(referencePoint.R2))
                return 0.0;

            var filtered = new List<(double F1, double F2)>(minimizedPoints.Count);
            foreach (var p in minimizedPoints)
            {
                if (double.IsFinite(p.F1) && double.IsFinite(p.F2)
                    && p.F1 < referencePoint.R1 && p.F2 < referencePoint.R2)
                {
                    filtered.Add(p);
                }
            }

            if (filtered.Count == 0)
                return 0.0;

            filtered.Sort((a, b) =>
            {
                int cmp = a.F1.CompareTo(b.F1);
                return cmp != 0 ? cmp : a.F2.CompareTo(b.F2);
            });

            double hypervolume = 0.0;
            double bestF2 = referencePoint.R2;

            foreach (var p in filtered)
            {
                if (p.F2 < bestF2)
                {
                    hypervolume += (referencePoint.R1 - p.F1) * (bestF2 - p.F2);
                    bestF2 = p.F2;
                }
            }

            return hypervolume;
        }

        /// <summary>
        /// Writes <c>pareto_front.json</c> into <paramref name="outputDirectory"/> and optionally copies
        /// Pareto-optimal STL files into <c>&lt;outputDirectory&gt;/pareto/</c>.
        /// </summary>
        public static ParetoSummary ExportParetoSummary(
            string outputDirectory,
            IReadOnlyList<ModelRecord> history,
            bool copyStls = true)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory must not be empty.", nameof(outputDirectory));
            if (history == null)
                throw new ArgumentNullException(nameof(history));

            Directory.CreateDirectory(outputDirectory);
            string paretoStlDir = Path.Combine(outputDirectory, "pareto");

            bool[] mask = ComputeParetoMask(history);

            var objectiveSpecs = history
                .Where(IsParetoEligible)
                .SelectMany(r => r.Objectives)
                .GroupBy(o => o.Name, StringComparer.Ordinal)
                .Select(g => new ExternalObjectiveSpec
                {
                    Name = g.Key,
                    Direction = g.First().Direction.ToString()
                })
                .ToList();

            var paretoEntries = new List<ParetoPointSummary>();
            var allEvaluations = new List<ParetoEvaluationEntry>(history.Count);

            for (int i = 0; i < history.Count; i++)
            {
                ModelRecord r = history[i];
                bool isPareto = mask[i];
                string exportedStlPath = string.Empty;

                if (isPareto && copyStls && !string.IsNullOrWhiteSpace(r.StlPath) && File.Exists(r.StlPath))
                {
                    Directory.CreateDirectory(paretoStlDir);
                    string ext = Path.GetExtension(r.StlPath);
                    string targetName = $"pareto_Gen{r.Iteration}_Var{r.Variant}{(string.IsNullOrEmpty(ext) ? ".stl" : ext)}";
                    string destPath = Path.Combine(paretoStlDir, targetName);
                    File.Copy(r.StlPath, destPath, overwrite: true);
                    exportedStlPath = destPath;
                }

                var objDict = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (ObjectiveValue obj in r.Objectives)
                {
                    if (float.IsFinite(obj.Value))
                        objDict[obj.Name] = obj.Value;
                }

                var paramDict = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var kv in r.ActiveParameters)
                {
                    paramDict[kv.Key] = kv.Value;
                }

                var conViolations = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (ConstraintEvaluation c in r.Constraints)
                {
                    conViolations[c.Name] = c.Violation;
                }

                allEvaluations.Add(new ParetoEvaluationEntry
                {
                    Iteration = r.Iteration,
                    Variant = r.Variant,
                    Feasible = r.Feasible && !r.SimulationFailed && !r.Disqualified,
                    SimulationFailed = r.SimulationFailed,
                    Disqualified = r.Disqualified,
                    IsPareto = isPareto,
                    Fitness = float.IsFinite(r.Fitness) ? r.Fitness : null,
                    StlPath = r.StlPath,
                    ExportedStlPath = exportedStlPath,
                    Parameters = paramDict,
                    Objectives = objDict,
                    ConstraintViolations = conViolations
                });

                if (isPareto)
                {
                    paretoEntries.Add(new ParetoPointSummary
                    {
                        Iteration = r.Iteration,
                        Variant = r.Variant,
                        Fitness = float.IsFinite(r.Fitness) ? r.Fitness : null,
                        Feasible = r.Feasible,
                        StlPath = r.StlPath,
                        ExportedStlPath = exportedStlPath,
                        Parameters = paramDict,
                        Objectives = objDict,
                        ConstraintViolations = conViolations
                    });
                }
            }

            double? hypervolume = null;
            Dictionary<string, double>? referencePointDict = null;
            var hvProgress = new List<ParetoHypervolumePoint>();

            if (objectiveSpecs.Count == 2)
            {
                var minPointsAll = new List<(int Iteration, double F1, double F2)>();
                string name1 = objectiveSpecs[0].Name;
                string name2 = objectiveSpecs[1].Name;
                double sign1 = string.Equals(objectiveSpecs[0].Direction, nameof(OptimizationDirection.Maximize), StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;
                double sign2 = string.Equals(objectiveSpecs[1].Direction, nameof(OptimizationDirection.Maximize), StringComparison.OrdinalIgnoreCase) ? -1.0 : 1.0;

                for (int i = 0; i < history.Count; i++)
                {
                    ModelRecord r = history[i];
                    if (!IsParetoEligible(r)) continue;
                    ObjectiveValue? o1 = r.Objectives.FirstOrDefault(o => string.Equals(o.Name, name1, StringComparison.Ordinal));
                    ObjectiveValue? o2 = r.Objectives.FirstOrDefault(o => string.Equals(o.Name, name2, StringComparison.Ordinal));
                    if (o1 == null || o2 == null || !float.IsFinite(o1.Value) || !float.IsFinite(o2.Value)) continue;

                    minPointsAll.Add((r.Iteration, sign1 * o1.Value, sign2 * o2.Value));
                }

                if (minPointsAll.Count > 0)
                {
                    double max1 = minPointsAll.Max(p => p.F1);
                    double min1 = minPointsAll.Min(p => p.F1);
                    double max2 = minPointsAll.Max(p => p.F2);
                    double min2 = minPointsAll.Min(p => p.F2);

                    double ref1 = max1 + Math.Max(0.1 * (max1 - min1), 1e-3);
                    double ref2 = max2 + Math.Max(0.1 * (max2 - min2), 1e-3);

                    referencePointDict = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        [name1] = sign1 * ref1,
                        [name2] = sign2 * ref2
                    };

                    var allPairs = minPointsAll.Select(p => (p.F1, p.F2)).ToList();
                    hypervolume = ComputeHypervolume2D(allPairs, (ref1, ref2));

                    foreach (int iter in minPointsAll.Select(p => p.Iteration).Distinct().OrderBy(x => x))
                    {
                        var prefix = minPointsAll.Where(p => p.Iteration <= iter).Select(p => (p.F1, p.F2)).ToList();
                        double hv = ComputeHypervolume2D(prefix, (ref1, ref2));
                        hvProgress.Add(new ParetoHypervolumePoint
                        {
                            Iteration = iter,
                            Evaluations = history.Count(r => r.Iteration <= iter),
                            Hypervolume = hv
                        });
                    }
                }
            }

            var summary = new ParetoSummary
            {
                GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                TotalEvaluations = history.Count,
                FeasibleCount = history.Count(r => !r.SimulationFailed && !r.Disqualified && r.Feasible),
                ParetoCount = paretoEntries.Count,
                Hypervolume = hypervolume,
                ReferencePoint = referencePointDict,
                Objectives = objectiveSpecs,
                Points = paretoEntries,
                HypervolumeHistory = hvProgress,
                Evaluations = allEvaluations
            };

            string jsonPath = Path.Combine(outputDirectory, "pareto_front.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, ExternalOptimizerProtocol.JsonOptions));
            return summary;
        }

        private static bool IsParetoEligible(ModelRecord r)
        {
            if (r == null || r.SimulationFailed || r.Disqualified || !r.Feasible || r.Objectives.Count == 0)
                return false;

            for (int i = 0; i < r.Objectives.Count; i++)
            {
                if (!float.IsFinite(r.Objectives[i].Value))
                    return false;
            }
            return true;
        }

        private static double[]? TryExtractMinimizedVector(
            ModelRecord record,
            IReadOnlyList<(string Name, OptimizationDirection Direction)> objSpecs)
        {
            var vec = new double[objSpecs.Count];
            for (int k = 0; k < objSpecs.Count; k++)
            {
                ObjectiveValue? match = record.Objectives.FirstOrDefault(
                    o => string.Equals(o.Name, objSpecs[k].Name, StringComparison.Ordinal));
                if (match == null || !float.IsFinite(match.Value))
                    return null;

                vec[k] = objSpecs[k].Direction == OptimizationDirection.Maximize
                    ? -(double)match.Value
                    : (double)match.Value;
            }
            return vec;
        }

        private static bool DominatesMinimized(double[] candidate, double[] other)
        {
            bool strictlyBetterInAtLeastOne = false;
            for (int i = 0; i < candidate.Length; i++)
            {
                if (candidate[i] > other[i]) return false;
                if (candidate[i] < other[i]) strictlyBetterInAtLeastOne = true;
            }
            return strictlyBetterInAtLeastOne;
        }
    }

    public sealed class ParetoSummary
    {
        public string GeneratedAtUtc { get; set; } = string.Empty;
        public int TotalEvaluations { get; set; }
        public int FeasibleCount { get; set; }
        public int ParetoCount { get; set; }
        public double? Hypervolume { get; set; }
        public Dictionary<string, double>? ReferencePoint { get; set; }
        public List<ExternalObjectiveSpec> Objectives { get; set; } = new();
        public List<ParetoPointSummary> Points { get; set; } = new();
        public List<ParetoHypervolumePoint> HypervolumeHistory { get; set; } = new();
        public List<ParetoEvaluationEntry> Evaluations { get; set; } = new();
    }

    public sealed class ParetoPointSummary
    {
        public int Iteration { get; set; }
        public int Variant { get; set; }
        public double? Fitness { get; set; }
        public bool Feasible { get; set; }
        public string StlPath { get; set; } = string.Empty;
        public string ExportedStlPath { get; set; } = string.Empty;
        public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> Objectives { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ConstraintViolations { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class ParetoEvaluationEntry
    {
        public int Iteration { get; set; }
        public int Variant { get; set; }
        public bool Feasible { get; set; }
        public bool SimulationFailed { get; set; }
        public bool Disqualified { get; set; }
        public bool IsPareto { get; set; }
        public double? Fitness { get; set; }
        public string StlPath { get; set; } = string.Empty;
        public string ExportedStlPath { get; set; } = string.Empty;
        public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> Objectives { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ConstraintViolations { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class ParetoHypervolumePoint
    {
        public int Iteration { get; set; }
        public int Evaluations { get; set; }
        public double Hypervolume { get; set; }
    }
}
