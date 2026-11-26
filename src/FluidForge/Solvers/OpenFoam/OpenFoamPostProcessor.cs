using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Single time sample of patch area-averaged pressure and velocity.
    /// </summary>
    public sealed record PatchPressureVelocitySample(float Time, float KinematicPressure, Vector3 Velocity);

    /// <summary>
    /// Single time sample of patch volumetric flux <c>phi</c>.
    /// </summary>
    public sealed record PatchFluxSample(float Time, float Flux);

    /// <summary>
    /// Aggregated OpenFOAM post-processing metrics extracted from <c>postProcessing/</c>.
    /// </summary>
    public sealed record OpenFoamPostProcessResult(
        IReadOnlyDictionary<string, float> Metrics,
        IReadOnlyDictionary<string, IReadOnlyList<float>> MonitoredSeries,
        float? YPlusMed,
        float? YPlusP95,
        float? YPlusMax,
        float? MassBalanceErrorPercent,
        float? FlowImbalancePercent,
        float? InletSpeedMs,
        bool HasNonFiniteValues);

    /// <summary>
    /// Reads OpenFOAM ESI <c>v2506</c> <c>postProcessing/</c> <c>.dat</c> files
    /// (<c>surfaceFieldValue</c>, <c>yPlus</c>, <c>forceCoeffs</c>) and computes
    /// internal pressure-drop, mass-balance, flow-imbalance, y+, and aerodynamic metrics.
    /// </summary>
    public static class OpenFoamPostProcessor
    {
        private static readonly Regex VectorLiteralRegex = new(
            @"\(\s*([0-9eE+\-.naninfNANINF]+)\s+([0-9eE+\-.naninfNANINF]+)\s+([0-9eE+\-.naninfNANINF]+)\s*\)",
            RegexOptions.Compiled);

        /// <summary>
        /// Evaluates <c>postProcessing/</c> inside <paramref name="caseDirectory"/> according to <paramref name="options"/>.
        /// </summary>
        public static OpenFoamPostProcessResult Evaluate(string caseDirectory, OpenFoamOptions options)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caseDirectory);
            ArgumentNullException.ThrowIfNull(options);

            var metrics = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var series = new Dictionary<string, IReadOnlyList<float>>(StringComparer.OrdinalIgnoreCase);
            bool hasNonFinite = false;

            string postDir = Path.Combine(caseDirectory, "postProcessing");
            string inletName = string.IsNullOrWhiteSpace(options.InletPatchName) ? "inlet" : options.InletPatchName.Trim();
            string outletPrefix = string.IsNullOrWhiteSpace(options.OutletPatchPrefix) ? "outlet" : options.OutletPatchPrefix.Trim();
            int window = Math.Max(1, options.TrailingWindowIterations);

            float? massBalanceErr = null;
            float? flowImbalance = null;
            float? inletSpeed = null;

            if (Directory.Exists(postDir))
            {
                EvaluateInternalFlow(
                    postDir, options, inletName, outletPrefix, window,
                    metrics, series, ref massBalanceErr, ref flowImbalance, ref inletSpeed, ref hasNonFinite);

                EvaluateForceCoeffs(postDir, window, metrics, series, ref hasNonFinite);
            }

            (float? ypMed, float? ypP95, float? ypMax) = EvaluateYPlus(postDir);
            if (ypMed.HasValue) metrics["YPlus_Med"] = ypMed.Value;
            if (ypP95.HasValue) metrics["YPlus_P95"] = ypP95.Value;
            if (ypMax.HasValue) metrics["YPlus_Max"] = ypMax.Value;

            return new OpenFoamPostProcessResult(
                metrics,
                series,
                ypMed,
                ypP95,
                ypMax,
                massBalanceErr,
                flowImbalance,
                inletSpeed,
                hasNonFinite);
        }

        private static void EvaluateInternalFlow(
            string postDir,
            OpenFoamOptions options,
            string inletName,
            string outletPrefix,
            int window,
            Dictionary<string, float> metrics,
            Dictionary<string, IReadOnlyList<float>> series,
            ref float? massBalanceErr,
            ref float? flowImbalance,
            ref float? inletSpeed,
            ref bool hasNonFinite)
        {
            var pressureSamplesByPatch = new Dictionary<string, IReadOnlyList<PatchPressureVelocitySample>>(StringComparer.OrdinalIgnoreCase);
            var fluxSamplesByPatch = new Dictionary<string, IReadOnlyList<PatchFluxSample>>(StringComparer.OrdinalIgnoreCase);

            foreach (string funcDir in Directory.EnumerateDirectories(postDir))
            {
                string dirName = Path.GetFileName(funcDir);
                if (dirName.StartsWith("pressure_", StringComparison.OrdinalIgnoreCase))
                {
                    string patch = dirName.Substring("pressure_".Length);
                    IReadOnlyList<string> datFiles = FindAllDatFilesChronological(funcDir, "surfaceFieldValue.dat");
                    if (datFiles.Count > 0)
                    {
                        var merged = new List<PatchPressureVelocitySample>();
                        foreach (string datFile in datFiles)
                        {
                            IReadOnlyList<PatchPressureVelocitySample> parsed = ParsePressureVelocityDat(datFile, out bool nonFinite);
                            if (nonFinite) hasNonFinite = true;
                            MergeTimeSeries(merged, parsed, static s => s.Time);
                        }
                        if (merged.Count > 0)
                            pressureSamplesByPatch[patch] = merged;
                    }
                }
                else if (dirName.StartsWith("flow_", StringComparison.OrdinalIgnoreCase))
                {
                    string patch = dirName.Substring("flow_".Length);
                    IReadOnlyList<string> datFiles = FindAllDatFilesChronological(funcDir, "surfaceFieldValue.dat");
                    if (datFiles.Count > 0)
                    {
                        var merged = new List<PatchFluxSample>();
                        foreach (string datFile in datFiles)
                        {
                            IReadOnlyList<PatchFluxSample> parsed = ParseFluxDat(datFile, out bool nonFinite);
                            if (nonFinite) hasNonFinite = true;
                            MergeTimeSeries(merged, parsed, static s => s.Time);
                        }
                        if (merged.Count > 0)
                            fluxSamplesByPatch[patch] = merged;
                    }
                }
            }

            if (!pressureSamplesByPatch.TryGetValue(inletName, out IReadOnlyList<PatchPressureVelocitySample>? inletPSeries) ||
                inletPSeries.Count == 0)
            {
                return;
            }

            var outletPatches = pressureSamplesByPatch.Keys
                .Where(k => k.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (outletPatches.Count == 0)
                return;

            double rho = Math.Max(options.Density, 1e-6);

            // Average over trailing window for each patch
            PatchPressureVelocitySample inMean = TailMeanPressureVelocity(inletPSeries, window);
            inletSpeed = inMean.Velocity.Length();

            double pInStat = inMean.KinematicPressure * rho;
            double pInTot = rho * (inMean.KinematicPressure + 0.5 * inMean.Velocity.LengthSquared());

            double phiIn = fluxSamplesByPatch.TryGetValue(inletName, out IReadOnlyList<PatchFluxSample>? inFluxSeries)
                ? TailMeanFlux(inFluxSeries, window)
                : 0.0;

            double weightSum = 0.0;
            double weightedOutStat = 0.0;
            double weightedOutTot = 0.0;
            var outletFluxes = new List<double>(outletPatches.Count);

            foreach (string outPatch in outletPatches)
            {
                PatchPressureVelocitySample outMean = TailMeanPressureVelocity(pressureSamplesByPatch[outPatch], window);
                double pOutStat = outMean.KinematicPressure * rho;
                double pOutTot = rho * (outMean.KinematicPressure + 0.5 * outMean.Velocity.LengthSquared());

                double phiOut = fluxSamplesByPatch.TryGetValue(outPatch, out IReadOnlyList<PatchFluxSample>? outFluxSeries)
                    ? TailMeanFlux(outFluxSeries, window)
                    : 0.0;

                outletFluxes.Add(phiOut);
                double w = Math.Abs(phiOut);
                if (w > 1e-15)
                {
                    weightSum += w;
                    weightedOutStat += w * pOutStat;
                    weightedOutTot += w * pOutTot;
                }
            }

            if (weightSum <= 0.0)
            {
                // Fallback to equal weighting if flux files were omitted or zero.
                foreach (string outPatch in outletPatches)
                {
                    PatchPressureVelocitySample outMean = TailMeanPressureVelocity(pressureSamplesByPatch[outPatch], window);
                    weightedOutStat += outMean.KinematicPressure * rho;
                    weightedOutTot += rho * (outMean.KinematicPressure + 0.5 * outMean.Velocity.LengthSquared());
                    weightSum += 1.0;
                }
            }

            double avgOutStat = weightedOutStat / weightSum;
            double avgOutTot = weightedOutTot / weightSum;

            float dpTot = (float)(pInTot - avgOutTot);
            float dpStat = (float)(pInStat - avgOutStat);

            if (!float.IsFinite(dpTot) || !float.IsFinite(dpStat))
            {
                hasNonFinite = true;
            }
            else
            {
                metrics["PressureDrop"] = dpTot;
                metrics["PressureDropStatic"] = dpStat;
            }

            // Build time series for PressureDrop convergence drift evaluation
            int minCount = inletPSeries.Count;
            foreach (string outPatch in outletPatches)
                minCount = Math.Min(minCount, pressureSamplesByPatch[outPatch].Count);

            if (minCount >= 2)
            {
                var dpSeries = new List<float>(minCount);
                for (int idx = 0; idx < minCount; idx++)
                {
                    PatchPressureVelocitySample sIn = inletPSeries[inletPSeries.Count - minCount + idx];
                    double totIn = rho * (sIn.KinematicPressure + 0.5 * sIn.Velocity.LengthSquared());

                    double totOutSum = 0.0;
                    double wOutSum = 0.0;
                    foreach (string outPatch in outletPatches)
                    {
                        IReadOnlyList<PatchPressureVelocitySample> os = pressureSamplesByPatch[outPatch];
                        PatchPressureVelocitySample sOut = os[os.Count - minCount + idx];
                        double totOut = rho * (sOut.KinematicPressure + 0.5 * sOut.Velocity.LengthSquared());

                        double w = 1.0;
                        if (fluxSamplesByPatch.TryGetValue(outPatch, out IReadOnlyList<PatchFluxSample>? ofs) &&
                            ofs.Count >= minCount)
                        {
                            double fw = Math.Abs(ofs[ofs.Count - minCount + idx].Flux);
                            if (fw > 1e-15) w = fw;
                        }

                        totOutSum += w * totOut;
                        wOutSum += w;
                    }

                    double dpSample = totIn - (totOutSum / Math.Max(wOutSum, 1e-15));
                    if (double.IsFinite(dpSample))
                        dpSeries.Add((float)dpSample);
                }

                if (dpSeries.Count > 0)
                    series["PressureDrop"] = dpSeries;
            }

            // Mass balance and flow imbalance
            if (fluxSamplesByPatch.ContainsKey(inletName) && outletPatches.All(fluxSamplesByPatch.ContainsKey))
            {
                double sumOut = outletFluxes.Sum();
                double denom = Math.Max(Math.Abs(phiIn), 1e-12);
                double mbErr = 100.0 * Math.Abs(phiIn + sumOut) / denom;
                if (double.IsFinite(mbErr))
                {
                    float mbFloat = (float)mbErr;
                    metrics["MassBalanceError"] = mbFloat;
                    massBalanceErr = mbFloat;
                }
                else
                {
                    hasNonFinite = true;
                }

                if (outletFluxes.Count >= 2)
                {
                    double maxOut = outletFluxes.Max();
                    double minOut = outletFluxes.Min();
                    double meanMag = outletFluxes.Select(Math.Abs).Average();
                    double imb = 100.0 * (maxOut - minOut) / Math.Max(meanMag, 1e-12);
                    float imbFloat = double.IsFinite(imb) ? (float)imb : 0f;
                    metrics["FlowImbalance"] = imbFloat;
                    flowImbalance = imbFloat;
                }
                else
                {
                    metrics["FlowImbalance"] = 0f;
                    flowImbalance = 0f;
                }
            }
        }

        private readonly record struct ForceCoeffSample(float Time, float? Cd, float? Cl);

        private static void EvaluateForceCoeffs(
            string postDir,
            int window,
            Dictionary<string, float> metrics,
            Dictionary<string, IReadOnlyList<float>> series,
            ref bool hasNonFinite)
        {
            string forceDir = Path.Combine(postDir, "forceCoeffs");
            if (!Directory.Exists(forceDir)) return;

            IReadOnlyList<string> datFiles = FindAllDatFilesChronological(forceDir, "coefficient.dat");
            if (datFiles.Count == 0)
                datFiles = FindAllDatFilesChronological(forceDir, "forceCoeffs.dat");
            if (datFiles.Count == 0) return;

            var merged = new List<ForceCoeffSample>();
            foreach (string datFile in datFiles)
            {
                var fileSamples = new List<ForceCoeffSample>();
                int cdCol = -1;
                int clCol = -1;

                foreach (string rawLine in File.ReadLines(datFile))
                {
                    string trimmed = rawLine.Trim();
                    if (trimmed.Length == 0) continue;

                    if (trimmed.StartsWith('#'))
                    {
                        string headerContent = trimmed.TrimStart('#').Trim();
                        string[] headerCols = headerContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < headerCols.Length; i++)
                        {
                            if (headerCols[i].Equals("Cd", StringComparison.OrdinalIgnoreCase)) cdCol = i;
                            else if (headerCols[i].Equals("Cl", StringComparison.OrdinalIgnoreCase)) clCol = i;
                        }
                        continue;
                    }

                    string[] cols = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length == 0 || !TryParseFloat(cols[0], out float time))
                    {
                        if (cols.Length > 0) hasNonFinite = true;
                        continue;
                    }

                    int effectiveCdCol = cdCol >= 0 ? cdCol : 1;
                    int effectiveClCol = clCol >= 0 ? clCol : 2;

                    float? cdVal = null;
                    if (effectiveCdCol < cols.Length)
                    {
                        if (TryParseFloat(cols[effectiveCdCol], out float cd))
                            cdVal = cd;
                        else
                            hasNonFinite = true;
                    }

                    float? clVal = null;
                    if (effectiveClCol < cols.Length)
                    {
                        if (TryParseFloat(cols[effectiveClCol], out float cl))
                            clVal = cl;
                        else
                            hasNonFinite = true;
                    }

                    fileSamples.Add(new ForceCoeffSample(time, cdVal, clVal));
                }

                MergeTimeSeries(merged, fileSamples, static s => s.Time);
            }

            var cdList = new List<float>(merged.Count);
            var clList = new List<float>(merged.Count);
            foreach (ForceCoeffSample sample in merged)
            {
                if (sample.Cd.HasValue) cdList.Add(sample.Cd.Value);
                if (sample.Cl.HasValue) clList.Add(sample.Cl.Value);
            }

            if (cdList.Count > 0)
            {
                metrics["Drag"] = TailMean(cdList, window);
                series["Drag"] = cdList;
            }
            if (clList.Count > 0)
            {
                metrics["Lift"] = TailMean(clList, window);
                series["Lift"] = clList;
            }
        }

        private static (float? Med, float? P95, float? Max) EvaluateYPlus(string postDir)
        {
            string yPlusDir = Path.Combine(postDir, "yPlus");
            if (!Directory.Exists(yPlusDir))
                return (null, null, null);

            IReadOnlyList<string> datFiles = FindAllDatFilesChronological(yPlusDir, "yPlus.dat");
            if (datFiles.Count == 0)
                return (null, null, null);

            float? bestTime = null;
            var bestAvgs = new List<float>();
            var bestMaxes = new List<float>();

            foreach (string datFile in datFiles)
            {
                float? fileLastTime = null;
                var fileAvgs = new List<float>();
                var fileMaxes = new List<float>();

                foreach (string rawLine in File.ReadLines(datFile))
                {
                    string trimmed = rawLine.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                    string[] cols = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // Format: Time patch min max average
                    if (cols.Length >= 5 &&
                        TryParseFloat(cols[0], out float time) &&
                        TryParseFloat(cols[3], out float maxVal) &&
                        TryParseFloat(cols[4], out float avgVal))
                    {
                        if (!fileLastTime.HasValue || time > fileLastTime.Value + 1e-6f)
                        {
                            fileLastTime = time;
                            fileAvgs.Clear();
                            fileMaxes.Clear();
                        }

                        if (MathF.Abs(time - fileLastTime.Value) <= 1e-6f)
                        {
                            fileAvgs.Add(avgVal);
                            fileMaxes.Add(maxVal);
                        }
                    }
                }

                // Ignore empty restart files without valid rows; newer files at >= bestTime win.
                if (fileLastTime.HasValue && fileAvgs.Count > 0 &&
                    (!bestTime.HasValue || fileLastTime.Value >= bestTime.Value - 1e-6f))
                {
                    bestTime = fileLastTime.Value;
                    bestAvgs = fileAvgs;
                    bestMaxes = fileMaxes;
                }
            }

            if (bestAvgs.Count == 0 || bestMaxes.Count == 0)
                return (null, null, null);

            float ypMed = bestAvgs.Average();
            float ypMax = bestMaxes.Max();
            // Conservative P95 estimate from average and maximum when full surface distribution is absent.
            float ypP95 = MathF.Min(ypMax, ypMed + 0.65f * MathF.Max(0f, ypMax - ypMed));
            return (ypMed, ypP95, ypMax);
        }

        /// <summary>
        /// Parses an OpenFOAM <c>surfaceFieldValue.dat</c> file containing <c>Time areaAverage(p) areaAverage(U)</c>.
        /// </summary>
        public static IReadOnlyList<PatchPressureVelocitySample> ParsePressureVelocityDat(string datPath, out bool hasNonFinite)
        {
            hasNonFinite = false;
            var samples = new List<PatchPressureVelocitySample>();

            foreach (string rawLine in File.ReadLines(datPath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                Match vecMatch = VectorLiteralRegex.Match(trimmed);
                if (!vecMatch.Success) continue;

                string scalarPrefix = trimmed.Substring(0, vecMatch.Index).Trim();
                string[] scalarTokens = scalarPrefix.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (scalarTokens.Length < 2) continue;

                if (!TryParseFloat(scalarTokens[0], out float time) ||
                    !TryParseFloat(scalarTokens[1], out float pKin) ||
                    !TryParseFloat(vecMatch.Groups[1].Value, out float ux) ||
                    !TryParseFloat(vecMatch.Groups[2].Value, out float uy) ||
                    !TryParseFloat(vecMatch.Groups[3].Value, out float uz))
                {
                    hasNonFinite = true;
                    continue;
                }

                samples.Add(new PatchPressureVelocitySample(time, pKin, new Vector3(ux, uy, uz)));
            }

            return samples;
        }

        /// <summary>
        /// Parses an OpenFOAM <c>surfaceFieldValue.dat</c> file containing <c>Time sum(phi)</c>.
        /// </summary>
        public static IReadOnlyList<PatchFluxSample> ParseFluxDat(string datPath, out bool hasNonFinite)
        {
            hasNonFinite = false;
            var samples = new List<PatchFluxSample>();

            foreach (string rawLine in File.ReadLines(datPath))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                string[] cols = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 2) continue;

                if (!TryParseFloat(cols[0], out float time) ||
                    !TryParseFloat(cols[1], out float phi))
                {
                    hasNonFinite = true;
                    continue;
                }

                samples.Add(new PatchFluxSample(time, phi));
            }

            return samples;
        }

        private static IReadOnlyList<string> FindAllDatFilesChronological(string functionDirectory, string fileName)
        {
            if (!Directory.Exists(functionDirectory))
                return Array.Empty<string>();

            var numeric = new List<(double Time, string Path)>();
            var nonNumeric = new List<string>();

            foreach (string timeDir in Directory.EnumerateDirectories(functionDirectory))
            {
                string candidate = Path.Combine(timeDir, fileName);
                if (!File.Exists(candidate)) continue;

                string folderName = Path.GetFileName(timeDir);
                if (double.TryParse(folderName, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                    double.IsFinite(t))
                {
                    numeric.Add((t, candidate));
                }
                else
                {
                    nonNumeric.Add(candidate);
                }
            }

            if (numeric.Count > 0)
            {
                numeric.Sort(static (a, b) =>
                {
                    int cmp = a.Time.CompareTo(b.Time);
                    return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Path, b.Path);
                });
                return numeric.Select(static x => x.Path).ToList();
            }

            if (nonNumeric.Count > 0)
            {
                nonNumeric.Sort(StringComparer.Ordinal);
                return nonNumeric;
            }

            string direct = Path.Combine(functionDirectory, fileName);
            return File.Exists(direct) ? new[] { direct } : Array.Empty<string>();
        }

        private static void MergeTimeSeries<TSample>(
            List<TSample> merged,
            IReadOnlyList<TSample> incoming,
            Func<TSample, float> getTime)
        {
            if (incoming.Count == 0) return;

            float firstNewTime = getTime(incoming[0]);
            if (merged.Count > 0 && firstNewTime <= getTime(merged[^1]))
            {
                int keepCount = merged.Count;
                while (keepCount > 0 && getTime(merged[keepCount - 1]) >= firstNewTime)
                    keepCount--;
                if (keepCount < merged.Count)
                    merged.RemoveRange(keepCount, merged.Count - keepCount);
            }

            foreach (TSample sample in incoming)
            {
                float t = getTime(sample);
                while (merged.Count > 0 && getTime(merged[^1]) >= t)
                    merged.RemoveAt(merged.Count - 1);
                merged.Add(sample);
            }
        }

        internal static int ResolveEffectiveTailCount(int sampleCount, int configuredWindow)
        {
            if (sampleCount <= 0) return 0;
            int boundedWindow = Math.Max(1, configuredWindow);
            int maxTail = sampleCount <= 8 ? sampleCount : Math.Max(4, sampleCount / 3);
            return Math.Min(boundedWindow, maxTail);
        }

        private static PatchPressureVelocitySample TailMeanPressureVelocity(
            IReadOnlyList<PatchPressureVelocitySample> samples,
            int window)
        {
            int count = ResolveEffectiveTailCount(samples.Count, window);
            int start = samples.Count - count;
            double pSum = 0.0, uxSum = 0.0, uySum = 0.0, uzSum = 0.0;
            for (int i = start; i < samples.Count; i++)
            {
                pSum += samples[i].KinematicPressure;
                uxSum += samples[i].Velocity.X;
                uySum += samples[i].Velocity.Y;
                uzSum += samples[i].Velocity.Z;
            }
            return new PatchPressureVelocitySample(
                samples[^1].Time,
                (float)(pSum / count),
                new Vector3((float)(uxSum / count), (float)(uySum / count), (float)(uzSum / count)));
        }

        private static double TailMeanFlux(IReadOnlyList<PatchFluxSample> samples, int window)
        {
            int count = ResolveEffectiveTailCount(samples.Count, window);
            int start = samples.Count - count;
            double sum = 0.0;
            for (int i = start; i < samples.Count; i++)
                sum += samples[i].Flux;
            return sum / count;
        }

        private static float TailMean(IReadOnlyList<float> values, int window)
        {
            int count = ResolveEffectiveTailCount(values.Count, window);
            int start = values.Count - count;
            double sum = 0.0;
            for (int i = start; i < values.Count; i++)
                sum += values[i];
            return (float)(sum / count);
        }

        private static bool TryParseFloat(string token, out float value)
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value))
                return true;
            value = float.NaN;
            return false;
        }
    }
}
