using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Parsed quality and topology metrics from <c>checkMesh</c> and <c>snappyHexMesh</c>.
    /// </summary>
    public sealed record OpenFoamMeshQualityReport(
        int Cells,
        int Hexahedra,
        int Prisms,
        int Tetrahedra,
        int Polyhedra,
        float MaxNonOrthogonality,
        float AvgNonOrthogonality,
        float MaxSkewness,
        float MaxAspectRatio,
        float LayerCoveragePercent,
        float AvgLayerThicknessM,
        bool CheckMeshPassed,
        IReadOnlyList<string> FailureReasons);

    /// <summary>
    /// Result of evaluating hard quality gates against <see cref="OpenFoamOptions"/>.
    /// </summary>
    public sealed record OpenFoamMeshGateResult(
        bool Passed,
        IReadOnlyList<string> Violations);

    /// <summary>
    /// Parses <c>checkMesh</c> and <c>snappyHexMesh</c> stdout/logs and enforces hard quality gates
    /// (checkMesh pass, non-orthogonality, skewness, and boundary-layer collapse).
    /// </summary>
    public static class OpenFoamMeshQualityParser
    {
        private static readonly Regex CellsRegex = new(@"^\s*cells:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex HexRegex = new(@"^\s*hexahedra:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex PrismRegex = new(@"^\s*prisms:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TetRegex = new(@"^\s*tetrahedra:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex PolyRegex = new(@"^\s*polyhedra:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex NonOrthoRegex = new(
            @"Mesh non-orthogonality Max:\s*([0-9eE+\-.]+)\s+average:\s*([0-9eE+\-.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SkewnessRegex = new(
            @"Max skewness\s*=\s*([0-9eE+\-.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AspectRatioRegex = new(
            @"Max aspect ratio\s*=\s*([0-9eE+\-.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FailedChecksRegex = new(
            @"Failed\s+(\d+)\s+mesh\s+checks",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches snappyHexMesh layer summary table rows (5 columns or ESI v2506 6 columns with target), e.g.:
        //   wall            1234    5.0       0.00123       87.5
        //   wall            3075    2         1.98          0.00111       97.3
        private static readonly Regex LayerSummaryRowRegex = new(
            @"^\s*(?<patch>[A-Za-z0-9_.\-]+)\s+(?<faces>\d+)\s+(?:(?<target>[0-9eE+\-.]+)\s+)?(?<layers>[0-9eE+\-.]+)\s+(?<thick>[0-9eE+\-.]+)\s+(?<pct>[0-9eE+\-.]+)\s*%?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Parses combined <c>checkMesh</c> and optional <c>snappyHexMesh</c> logs into a structured report.
        /// </summary>
        public static OpenFoamMeshQualityReport Parse(
            string? checkMeshLog,
            string? snappyLog = null,
            string wallPatchName = "wall")
        {
            string cm = checkMeshLog ?? string.Empty;
            string sn = snappyLog ?? string.Empty;

            int cells = ParseInt(CellsRegex, cm);
            int hex = ParseInt(HexRegex, cm);
            int prism = ParseInt(PrismRegex, cm);
            int tet = ParseInt(TetRegex, cm);
            int poly = ParseInt(PolyRegex, cm);

            float maxNonOrtho = float.NaN;
            float avgNonOrtho = float.NaN;
            Match nonOrthoMatch = NonOrthoRegex.Match(cm);
            if (nonOrthoMatch.Success)
            {
                maxNonOrtho = ParseFloat(nonOrthoMatch.Groups[1].Value);
                avgNonOrtho = ParseFloat(nonOrthoMatch.Groups[2].Value);
            }

            float maxSkew = ParseRegexFloat(SkewnessRegex, cm);
            float maxAspect = ParseRegexFloat(AspectRatioRegex, cm);

            var failureReasons = new List<string>();
            bool meshOkExplicit = cm.Contains("Mesh OK.", StringComparison.OrdinalIgnoreCase);
            Match failedMatch = FailedChecksRegex.Match(cm);
            bool checkMeshPassed;
            if (failedMatch.Success)
            {
                checkMeshPassed = false;
                failureReasons.Add($"checkMesh reported {failedMatch.Groups[1].Value} failed mesh check(s).");
            }
            else if (meshOkExplicit)
            {
                checkMeshPassed = true;
            }
            else
            {
                checkMeshPassed = cells > 0 && !cm.Contains("***", StringComparison.Ordinal);
                if (!checkMeshPassed)
                    failureReasons.Add("checkMesh did not report 'Mesh OK.'.");
            }

            (float coveragePct, float avgThickness) = ParseLayerCoverage(sn, wallPatchName);

            return new OpenFoamMeshQualityReport(
                cells,
                hex,
                prism,
                tet,
                poly,
                maxNonOrtho,
                avgNonOrtho,
                maxSkew,
                maxAspect,
                coveragePct,
                avgThickness,
                checkMeshPassed,
                failureReasons);
        }

        /// <summary>
        /// Evaluates hard quality gates against <paramref name="options"/>.
        /// </summary>
        public static OpenFoamMeshGateResult EvaluateGates(OpenFoamMeshQualityReport report, OpenFoamOptions options)
        {
            ArgumentNullException.ThrowIfNull(report);
            ArgumentNullException.ThrowIfNull(options);

            var violations = new List<string>(report.FailureReasons);

            if (report.Cells <= 0)
            {
                violations.Add("Generated mesh contains 0 cells.");
            }
            else if (!report.CheckMeshPassed && report.FailureReasons.Count == 0)
            {
                violations.Add("checkMesh failed topology/geometry verification.");
            }

            if (float.IsFinite(report.MaxNonOrthogonality) && report.MaxNonOrthogonality > options.MaxNonOrthogonality)
            {
                violations.Add(
                    $"Max non-orthogonality {report.MaxNonOrthogonality.ToString("0.0", CultureInfo.InvariantCulture)} deg "
                    + $"exceeds limit {options.MaxNonOrthogonality.ToString("0.0", CultureInfo.InvariantCulture)} deg.");
            }

            if (float.IsFinite(report.MaxSkewness) && report.MaxSkewness > options.MaxSkewness)
            {
                violations.Add(
                    $"Max skewness {report.MaxSkewness.ToString("0.00", CultureInfo.InvariantCulture)} "
                    + $"exceeds limit {options.MaxSkewness.ToString("0.00", CultureInfo.InvariantCulture)}.");
            }

            if (options.AddLayers &&
                float.IsFinite(report.LayerCoveragePercent) &&
                report.LayerCoveragePercent < options.MinLayerCoveragePercent)
            {
                violations.Add(
                    $"Boundary layer coverage {report.LayerCoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)}% "
                    + $"is below minimum {options.MinLayerCoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)}% (layer collapse).");
            }

            return new OpenFoamMeshGateResult(violations.Count == 0, violations);
        }

        private static (float CoveragePercent, float AvgThicknessM) ParseLayerCoverage(string snappyLog, string wallPatchName)
        {
            if (string.IsNullOrWhiteSpace(snappyLog))
                return (float.NaN, float.NaN);

            string targetWall = string.IsNullOrWhiteSpace(wallPatchName) ? "wall" : wallPatchName.Trim();
            float? wallPct = null;
            float? wallThick = null;
            float? overallPct = null;
            float? overallThick = null;
            bool sawSixColumnRow = false;

            foreach (Match m in LayerSummaryRowRegex.Matches(snappyLog))
            {
                bool isSixCol = m.Groups["target"].Success;
                if (sawSixColumnRow && !isSixCol)
                    continue;
                if (isSixCol && !sawSixColumnRow)
                {
                    sawSixColumnRow = true;
                    wallPct = null;
                    wallThick = null;
                    overallPct = null;
                    overallThick = null;
                }

                string patch = m.Groups["patch"].Value.Trim();
                float thick = ParseFloat(m.Groups["thick"].Value);
                float pct = ParseFloat(m.Groups["pct"].Value);
                if (!float.IsFinite(pct)) continue;

                if (patch.Equals(targetWall, StringComparison.OrdinalIgnoreCase))
                {
                    wallPct = pct;
                    wallThick = thick;
                }
                else if (patch.Equals("overall", StringComparison.OrdinalIgnoreCase))
                {
                    overallPct = pct;
                    overallThick = thick;
                }
            }

            if (wallPct.HasValue)
                return (wallPct.Value, wallThick ?? float.NaN);
            if (overallPct.HasValue)
                return (overallPct.Value, overallThick ?? float.NaN);

            return (float.NaN, float.NaN);
        }

        private static int ParseInt(Regex regex, string input)
        {
            Match m = regex.Match(input);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val)
                ? val
                : 0;
        }

        private static float ParseRegexFloat(Regex regex, string input)
        {
            Match m = regex.Match(input);
            return m.Success ? ParseFloat(m.Groups[1].Value) : float.NaN;
        }

        private static float ParseFloat(string text)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)
                ? v
                : float.NaN;
    }
}
