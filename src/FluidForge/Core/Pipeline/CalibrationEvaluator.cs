using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Data for a single mesh calibration level (TODO-42).
    /// </summary>
    public sealed record CalibrationStageData(
        int LevelIndex,
        float Factor,
        int MeshCells,
        double DurationSeconds,
        IReadOnlyDictionary<string, float> Metrics,
        float Fitness,
        bool Converged,
        float YPlusP95,
        bool YPlusOk,
        IReadOnlyDictionary<string, float>? Uncertainty = null,
        bool CellLimitExceeded = false,
        int BoundaryLayerLayers = 0,
        float BoundaryLayerThicknessMm = 0f,
        int BoundaryLayerStage = 0,
        int SurfaceTriangles = 0,
        int SurfaceQuads = 0,
        int VolumePrisms = 0,
        int VolumeHexahedra = 0,
        int VolumeTetrahedra = 0,
        int VolumePyramids = 0,
        float TunnelBlockagePercent = float.NaN,
        float PressureDragCoeff = float.NaN,
        float ViscousDragCoeff = float.NaN,
        float YPlusLimit = float.NaN);

    /// <summary>
    /// Result of the Grid Convergence Index (GCI) calculation per Celik et al. (2008).
    /// </summary>
    public sealed record GciResult(
        float ObservedOrderP,
        float GciFinePercent,
        float GciSelectedPercent,
        bool IsMonotonic,
        string? Warning,
        float ExtrapolatedValue = float.NaN,
        float ExtrapolatedDeviationPercent = float.NaN,
        bool IsAsymptotic = true);

    /// <summary>
    /// Decision and full evaluation of a mesh calibration run (TODO-42).
    /// </summary>
    public sealed record CalibrationDecision(
        bool Success,
        CalibrationStageData? SelectedStage,
        CalibrationStageData ReferenceStage,
        IReadOnlyList<CalibrationStageData> AllStages,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, float>> RelativeDeviationsToRef,
        GciResult? Gci,
        string SummaryMessage,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, float>>? NormalizedDeviationsToRef = null,
        CalibrationStageData? FallbackStage = null);

    /// <summary>
    /// Pure evaluation logic for mesh calibration (stopping criterion, GCI, monotonicity).
    /// Free of I/O for clean unit testability.
    /// </summary>
    public static class CalibrationEvaluator
    {
        /// <summary>
        /// The five standard refinement levels with constant ratio r = √2 ≈ 1.414.
        /// </summary>
        public static readonly float[] StandardFactors = { 2.0f, 1.41f, 1.0f, 0.71f, 0.5f };

        /// <summary>
        /// Computes the Grid Convergence Index (GCI) per Celik et al. (2008), ASME J. Fluids Eng.
        /// </summary>
        /// <param name="f3Coarse">Result quantity on the coarse mesh.</param>
        /// <param name="f2Medium">Result quantity on the medium mesh.</param>
        /// <param name="f1Fine">Result quantity on the fine reference mesh.</param>
        /// <param name="r">Refinement ratio (default √2 ≈ 1.41421356f).</param>
        /// <param name="safetyFactorFs">Safety factor (default 1.25 for 3 grids).</param>
        public static GciResult ComputeGci(
            float f3Coarse,
            float f2Medium,
            float f1Fine,
            float r = 1.41421356f,
            float safetyFactorFs = 1.25f)
        {
            float eps32 = f3Coarse - f2Medium;
            float eps21 = f2Medium - f1Fine;

            // Exact convergence special case: all three levels yield the identical value.
            if (Math.Abs(eps21) <= 1e-9f && Math.Abs(eps32) <= 1e-9f)
            {
                return new GciResult(
                    ObservedOrderP: 1.0f,
                    GciFinePercent: 0f,
                    GciSelectedPercent: 0f,
                    IsMonotonic: true,
                    Warning: null,
                    ExtrapolatedValue: f1Fine,
                    ExtrapolatedDeviationPercent: 0f,
                    IsAsymptotic: true);
            }

            // Monotonicity and asymptotic range check per Celik et al. (2008):
            // Sign flip of differences => non-monotonic; |eps32 / eps21| <= 1 => differences
            // do not decrease under refinement, so not in the asymptotic convergence range.
            bool isMonotonic = (eps32 * eps21 > 0f);
            float ratio = Math.Abs(eps21) > 1e-9f ? Math.Abs(eps32 / eps21) : float.PositiveInfinity;
            bool isAsymptotic = isMonotonic && ratio > 1.0f;

            if (!isAsymptotic)
            {
                string nonAsymptoticWarning = !isMonotonic
                    ? "Convergence sequence is not monotonic (sign change in differences) - GCI and Richardson extrapolation not applicable."
                    : "Convergence sequence is not in the asymptotic range (differences do not decrease with refinement, |eps32/eps21| <= 1) - GCI and Richardson extrapolation not applicable.";

                return new GciResult(
                    ObservedOrderP: float.NaN,
                    GciFinePercent: float.NaN,
                    GciSelectedPercent: float.NaN,
                    IsMonotonic: isMonotonic,
                    Warning: nonAsymptoticWarning,
                    ExtrapolatedValue: float.NaN,
                    ExtrapolatedDeviationPercent: float.NaN,
                    IsAsymptotic: false);
            }

            // Observed order p: for constant refinement ratio r = r21 = r32
            float p = float.IsFinite(ratio)
                ? Math.Clamp(MathF.Log(ratio) / MathF.Log(r), 0.5f, 8.0f)
                : 8.0f;

            // Relative apparent errors e_a
            float ea21 = Math.Abs(f1Fine - f2Medium) / Math.Max(Math.Abs(f1Fine), 1e-7f);
            float ea32 = Math.Abs(f2Medium - f3Coarse) / Math.Max(Math.Abs(f2Medium), 1e-7f);

            // GCI_fine and GCI_selected (in percent)
            float denom = MathF.Pow(r, p) - 1.0f;
            float gciFine = (denom > 1e-9f) ? (safetyFactorFs * ea21 / denom) * 100.0f : 0f;
            float gciSelected = (denom > 1e-9f) ? (safetyFactorFs * ea32 / denom) * 100.0f : 0f;

            // Richardson extrapolation: estimate of the grid-independent value from the two finest levels.
            float extrapolated = float.NaN;
            float extrapolatedDeviation = float.NaN;
            if (denom > 1e-9f)
            {
                extrapolated = f1Fine + (f1Fine - f2Medium) / denom;
                extrapolatedDeviation = Math.Abs(f1Fine - extrapolated) / Math.Max(Math.Abs(extrapolated), 1e-7f) * 100.0f;
            }

            return new GciResult(p, gciFine, gciSelected, isMonotonic, null, extrapolated, extrapolatedDeviation, IsAsymptotic: true);
        }

        /// <summary>
        /// Deviations of all levels relative to the reference level: relative (for reports) and normalized
        /// per <see cref="MetricTolerance"/> (for the selection decision; at most 1 = within tolerance, TODO-W1).
        /// </summary>
        public static (Dictionary<int, IReadOnlyDictionary<string, float>> Relative,
                       Dictionary<int, IReadOnlyDictionary<string, float>> Normalized)
            ComputeDeviations(
                IReadOnlyList<CalibrationStageData> stages,
                CalibrationStageData refStage,
                IReadOnlyList<string> metrics,
                float tolerance,
                IReadOnlyDictionary<string, float>? absoluteTolerances)
        {
            var relative = new Dictionary<int, IReadOnlyDictionary<string, float>>();
            var normalized = new Dictionary<int, IReadOnlyDictionary<string, float>>();

            foreach (var stage in stages)
            {
                var rel = new Dictionary<string, float>();
                var norm = new Dictionary<string, float>();
                foreach (string metric in metrics)
                {
                    float refVal = refStage.Metrics.TryGetValue(metric, out float rv) ? rv : 0f;
                    float stageVal = stage.Metrics.TryGetValue(metric, out float sv) ? sv : 0f;
                    rel[metric] = MetricTolerance.Relative(stageVal, refVal);

                    // Oscillating runs (vortex shedding): allowable deviation grows with the uncertainty of both levels.
                    float uStage = 0f, uRef = 0f;
                    stage.Uncertainty?.TryGetValue(metric, out uStage);
                    refStage.Uncertainty?.TryGetValue(metric, out uRef);
                    norm[metric] = MetricTolerance.Normalized(
                        stageVal, refVal, MetricTolerance.WithUncertainty(tolerance, uStage, uRef),
                        MetricTolerance.AbsoluteFor(metric, absoluteTolerances));
                }
                relative[stage.LevelIndex] = rel;
                normalized[stage.LevelIndex] = norm;
            }

            return (relative, normalized);
        }

        /// <summary>
        /// Evaluates a calibration sequence using a contiguous fine-to-coarse suffix check and selects
        /// the coarsest level whose entire finer suffix up to the reference level satisfies all criteria
        /// (Converged, YPlusOk, and within tolerance). Stops immediately at the first failing level so a
        /// coincidental coarse match cannot bypass an intermediate out-of-tolerance mesh.
        /// Allowed deviation is <c>|Δ| ≤ tolerance · |reference| + absoluteFloor</c> (<see cref="MetricTolerance"/>).
        /// </summary>
        public static CalibrationDecision EvaluateCalibration(
            IReadOnlyList<CalibrationStageData> stages,
            IReadOnlyCollection<string> monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances = null)
        {
            if (stages == null || stages.Count == 0)
                throw new InvalidOperationException($"{OutputConventions.CalibrationTag} No levels supplied for evaluation.");

            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Default reference level is the finest computed level (smallest factor)
            var refStage = stages.OrderBy(s => s.Factor).First();

            // If the finest level is invalid, check whether another valid level exists in stages that is finer
            // than the coarsest level. Only if fewer than 2 levels exist or the finest level of the 5 base levels
            // is invalid (without a valid extra level) do we fail with "reference level failed".
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                float coarsestFactor = stages.Max(s => s.Factor);
                var alternativeRef = stages
                    .Where(s => s.Converged && s.YPlusOk && s.Factor < coarsestFactor)
                    .OrderBy(s => s.Factor)
                    .FirstOrDefault();

                bool hasValidExtraStage = stages.Any(s => s.Factor < 0.5f && s.Converged && s.YPlusOk);
                bool baseStage5Valid = stages.Any(s => Math.Abs(s.Factor - 0.5f) < 0.05f && s.Converged && s.YPlusOk);
                bool baseStage5InvalidWithoutExtra = !baseStage5Valid && !hasValidExtraStage;

                if (stages.Count >= 2 && !baseStage5InvalidWithoutExtra && alternativeRef != null)
                {
                    refStage = alternativeRef;
                }
            }

            // Deviations of all levels relative to reference: relative (report) and normalized (decision)
            var (deviations, normalizedDeviations) = ComputeDeviations(stages, refStage, metricsToMonitor, tolerance, absoluteTolerances);

            // Compute GCI when at least 3 levels are available
            GciResult? gci = null;
            if (stages.Count >= 3)
            {
                var sortedAsc = stages.Where(s => s.Factor >= refStage.Factor).OrderBy(s => s.Factor).ToList();
                if (sortedAsc.Count < 3)
                {
                    sortedAsc = stages.OrderBy(s => s.Factor).ToList();
                }

                var sFine = sortedAsc[0];
                var sMed = sortedAsc[1];
                var sCoarse = sortedAsc[2];

                string primaryMetric = metricsToMonitor[0];
                if (sFine.Metrics.ContainsKey(primaryMetric)
                    && sMed.Metrics.ContainsKey(primaryMetric)
                    && sCoarse.Metrics.ContainsKey(primaryMetric))
                {
                    float ratio = sMed.Factor / Math.Max(sFine.Factor, 1e-5f);
                    gci = ComputeGci(
                        sCoarse.Metrics[primaryMetric],
                        sMed.Metrics[primaryMetric],
                        sFine.Metrics[primaryMetric],
                        ratio);
                }
            }

            // Reference level validity: not converged or y+ violated -> calibration failed
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                string reason = FormatReferenceFailureReason(refStage, "reference level");

                return new CalibrationDecision(
                    false,
                    null,
                    refStage,
                    stages,
                    deviations,
                    gci,
                    $"{OutputConventions.CalibrationTag} Calibration failed: {reason}",
                    normalizedDeviations);
            }

            // Candidate search from fine to coarse (contiguous suffix check excluding the reference level itself)
            bool StageMatches(CalibrationStageData candidate)
            {
                if (!candidate.Converged || !candidate.YPlusOk) return false;
                if (!normalizedDeviations.TryGetValue(candidate.LevelIndex, out var devDict)) return false;

                foreach (string metric in metricsToMonitor)
                {
                    if (devDict.TryGetValue(metric, out float dev) && dev > 1.0f)
                        return false;
                }
                return true;
            }

            CalibrationStageData? selected = null;
            CalibrationStageData? firstFailing = null;
            var candidates = stages
                .Where(s => s.Factor > refStage.Factor)
                .OrderBy(s => s.Factor)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (StageMatches(candidate))
                {
                    selected = candidate;
                }
                else
                {
                    firstFailing = candidate;
                    break;
                }
            }

            CalibrationStageData? skippedCoarseMatch = firstFailing != null
                ? candidates.FirstOrDefault(c => c.Factor > firstFailing.Factor && StageMatches(c))
                : null;

            string nonMonotonicNote = string.Empty;
            if (firstFailing != null && skippedCoarseMatch != null)
            {
                string failCause = !firstFailing.Converged
                    ? "did not converge"
                    : (!firstFailing.YPlusOk ? "violated y+" : "exceeded tolerance");
                nonMonotonicNote = $" (non-monotonic: coarser level {skippedCoarseMatch.LevelIndex} matched reference coincidentally but was rejected because finer level {firstFailing.LevelIndex} {failCause})";
            }

            if (selected != null)
            {
                string msg = $"Level {selected.LevelIndex} (factor {selected.Factor:0.00}) meets {tolerance * 100:0.0} % tolerance relative to reference level {refStage.Factor:0.00}.{nonMonotonicNote}";
                return new CalibrationDecision(true, selected, refStage, stages, deviations, gci, msg, normalizedDeviations);
            }
            else
            {
                string msg = $"No coarse level achieves {tolerance * 100:0.0} % tolerance relative to reference level {refStage.Factor:0.00}.{nonMonotonicNote}";
                var fallback = stages
                    .Where(s => s.Factor > refStage.Factor && s.Converged && s.YPlusOk)
                    .OrderBy(s => s.Factor)
                    .FirstOrDefault() ?? refStage;
                return new CalibrationDecision(false, null, refStage, stages, deviations, gci, msg, normalizedDeviations, fallback);
            }
        }

        /// <summary>
        /// Computes the 3 confirmation factors for 3D confirmation around the 2D calibration factor f2D (TODO-48).
        /// Ordered from coarse to fine: { f2D * √2, f2D, f2D / √2 }.
        /// </summary>
        public static float[] Build3DConfirmationFactors(float factor2D)
        {
            float coarse = (float)Math.Round(factor2D * Math.Sqrt(2.0), 2);
            float medium = (float)Math.Round(factor2D, 2);
            float fine = (float)Math.Round(factor2D / Math.Sqrt(2.0), 2);
            return new[] { coarse, medium, fine };
        }

        /// <summary>
        /// Evaluates the 3-level 3D confirmation around f2D (TODO-48) using a contiguous fine-to-coarse check.
        /// The finest of the three levels is the 3D reference.
        /// First checks the medium level (f2D): only if f2D satisfies all criteria (Converged, YPlusOk, within tolerance)
        /// may the coarse level (f2D * √2) be checked and selected when it also matches; otherwise f2D is selected.
        /// If f2D fails, the coarse level is rejected even if it coincidentally matches, returning Success = false.
        /// </summary>
        public static CalibrationDecision EvaluateConfirmation3D(
            float factor2D,
            IReadOnlyList<CalibrationStageData> stages3D,
            IReadOnlyCollection<string> monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances = null)
        {
            if (stages3D == null || stages3D.Count == 0)
                throw new InvalidOperationException("[CALIBRATION 3D] No 3D levels supplied for evaluation.");

            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Sort ascending by factor: finest mesh first
            var sortedAsc = stages3D.OrderBy(s => s.Factor).ToList();
            var refStage = sortedAsc[0]; // finest level (smallest factor, approx. f2D / √2)

            var (deviations, normalizedDeviations) = ComputeDeviations(stages3D, refStage, metricsToMonitor, tolerance, absoluteTolerances);

            // Compute GCI from the 3 levels
            GciResult? gci = null;
            if (stages3D.Count >= 3)
            {
                var sFine = sortedAsc[0];
                var sMed = sortedAsc[1];
                var sCoarse = sortedAsc[2];

                string primaryMetric = metricsToMonitor[0];
                if (sFine.Metrics.ContainsKey(primaryMetric)
                    && sMed.Metrics.ContainsKey(primaryMetric)
                    && sCoarse.Metrics.ContainsKey(primaryMetric))
                {
                    float ratio = sMed.Factor / Math.Max(sFine.Factor, 1e-5f);
                    gci = ComputeGci(
                        sCoarse.Metrics[primaryMetric],
                        sMed.Metrics[primaryMetric],
                        sFine.Metrics[primaryMetric],
                        ratio);
                }
            }

            // 3D reference level validity
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                string reason = FormatReferenceFailureReason(refStage, "3D reference level");

                return new CalibrationDecision(
                    false,
                    null,
                    refStage,
                    stages3D,
                    deviations,
                    gci,
                    $"[CALIBRATION 3D] 3D confirmation failed: {reason}",
                    normalizedDeviations);
            }

            // Identify levels: coarse (largest factor) and medium (middle factor)
            // Contiguous fine-to-coarse check: medium (f2D) must pass before coarse can be considered
            CalibrationStageData? coarseStage = sortedAsc.Count >= 3 ? sortedAsc[^1] : null;
            CalibrationStageData? mediumStage = sortedAsc.Count >= 2 ? sortedAsc[^2] : null;

            bool StageMatches(CalibrationStageData c)
            {
                if (!c.Converged || !c.YPlusOk) return false;
                if (!normalizedDeviations.TryGetValue(c.LevelIndex, out var devDict)) return false;
                foreach (string metric in metricsToMonitor)
                {
                    if (devDict.TryGetValue(metric, out float d) && d > 1.0f)
                        return false;
                }
                return true;
            }

            bool mediumOk = mediumStage != null && StageMatches(mediumStage);
            if (mediumOk)
            {
                if (coarseStage != null && StageMatches(coarseStage))
                {
                    string msg = $"Coarse 3D level (factor {coarseStage.Factor:0.00}, level {coarseStage.LevelIndex}) meets {tolerance * 100:0.0} % tolerance relative to 3D reference level {refStage.Factor:0.00}.";
                    return new CalibrationDecision(true, coarseStage, refStage, stages3D, deviations, gci, msg, normalizedDeviations);
                }

                string medMsg = $"2D level (factor {mediumStage!.Factor:0.00}, level {mediumStage.LevelIndex}) confirmed in 3D: meets {tolerance * 100:0.0} % tolerance relative to 3D reference level {refStage.Factor:0.00}.";
                return new CalibrationDecision(true, mediumStage, refStage, stages3D, deviations, gci, medMsg, normalizedDeviations);
            }

            string failMsg = $"Neither the coarse 3D level nor the 2D factor ({factor2D:0.00}) achieves {tolerance * 100:0.0} % tolerance in 3D relative to 3D reference level {refStage.Factor:0.00}.";
            if (coarseStage != null && StageMatches(coarseStage))
            {
                string medDesc = mediumStage != null ? $"level {mediumStage.LevelIndex} (factor {mediumStage.Factor:0.00})" : $"2D factor ({factor2D:0.00})";
                string medFailCause = mediumStage != null && !mediumStage.Converged
                    ? "did not converge"
                    : (mediumStage != null && !mediumStage.YPlusOk ? "violated y+" : "exceeded tolerance");
                failMsg += $" (non-monotonic: coarse 3D level {coarseStage.LevelIndex} matched reference coincidentally but was rejected because {medDesc} {medFailCause})";
            }
            var fallback3D = (mediumStage != null && mediumStage.Converged && mediumStage.YPlusOk) ? mediumStage : refStage;
            return new CalibrationDecision(false, null, refStage, stages3D, deviations, gci, failMsg, normalizedDeviations, fallback3D);
        }

        private static string FormatReferenceFailureReason(CalibrationStageData refStage, string stageLabel)
        {
            if (!refStage.Converged)
                return $"{stageLabel} (finest mesh) did not converge numerically.";

            string yPlusDetails = refStage.YPlusP95 > 0f
                ? (float.IsFinite(refStage.YPlusLimit) && refStage.YPlusLimit > 0f
                    ? $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)} > limit {refStage.YPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)} from TargetYPlus * YPlusWarnFactor)"
                    : $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)})")
                : string.Empty;

            return $"y+ of {stageLabel}{yPlusDetails} is out of tolerance.";
        }
    }
}
