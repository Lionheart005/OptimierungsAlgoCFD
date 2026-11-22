using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Cli
{
    internal sealed class SmokeTestProbeResult
    {
        public string Label { get; init; } = "";
        public Dictionary<string, float> Parameters { get; init; } = new();
        public string GeometryPath { get; init; } = "";
        public GeometryKind Kind { get; init; } = GeometryKind.SurfaceStl;
        public long FileSizeBytes { get; init; }
        public string FileSha256 { get; init; } = "";
        public bool Passed { get; init; }
        public string? ErrorMessage { get; init; }
        public int ElementCount { get; init; }
        public int OpenEdges { get; init; }
        public int NonManifoldEdges { get; init; }
        public Dictionary<string, float> Metrics { get; init; } = new();
        public (Vector3 Min, Vector3 Max) Bounds { get; init; }
        public bool HasConstraints { get; init; }
        public bool ConstraintsSatisfied { get; init; } = true;
        public List<ConstraintEvaluation> Constraints { get; init; } = new();
    }

    internal sealed class SmokeTestSensitivityResult
    {
        public string ParameterName { get; init; } = "";
        public float BaseValue { get; init; }
        public float PerturbedValue { get; init; }
        public bool Effective { get; init; }
        public bool IsFlowParameter { get; init; }
        public bool GeometryValid { get; init; }
        public double MaxMetricRelativeDelta { get; init; }
        public double MaxBBoxDelta { get; init; }
        public bool Sha256Changed { get; init; }
        public string Status { get; init; } = "";
        public string? ErrorMessage { get; init; }
    }

    internal sealed class SmokeTestReport
    {
        public string ProjectName { get; init; } = "";
        public string OutputDirectory { get; init; } = "";
        public List<SmokeTestProbeResult> Probes { get; init; } = new();
        public List<SmokeTestSensitivityResult> Sensitivities { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
        public bool Passed { get; init; }
        public string Summary { get; init; } = "";
    }

    /// <summary>
    /// Geometry smoke test (<c>--smoke-test</c>): probes 6 deterministic points across parameter bounds
    /// (Base, Min, Mid, Max, Rand1, Rand2) and verifies ±5% sensitivity for every free parameter.
    /// </summary>
    internal static class SmokeTestCommand
    {
        private const int RandomProbeSeed = 42;
        private const double SensitivityStepFraction = 0.05;

        public static bool Run(IProjectDefinition definition, ProjectContext context)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(context);

            var simContext = new SimulationContext(context.Simulation, context.Project);
            string outputDirectory = Path.Combine(simContext.WorkingDirectory, "smoke-test");

            SolverStage[] stages;
            try
            {
                stages = definition.CreateStages(context);
            }
            catch
            {
                stages = Array.Empty<SolverStage>();
            }

            float effectiveVoxelResolution = VoxelResolutionAdvisor.Resolve(context.Simulation, context.Project, stages);
            IGeometryKernel kernel = definition.CreateKernel();

            SmokeTestReport? report = null;
            kernel.RunHosted(effectiveVoxelResolution, () =>
            {
                report = Execute(definition, context, outputDirectory, Console.Out);
            });

            return report?.Passed ?? false;
        }

        public static SmokeTestReport Execute(
            IProjectDefinition definition,
            ProjectContext context,
            string outputDirectory,
            TextWriter? logWriter = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory must not be empty.", nameof(outputDirectory));

            Directory.CreateDirectory(outputDirectory);

            var simContext = new SimulationContext(context.Simulation, context.Project, outputDirectory);
            ParameterSpace space = ParameterSpace.Create(simContext);
            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IGeometryConstraintEvaluator? constraintEvaluator = definition.CreateGeometryConstraints(context);

            int d = space.Dimension;
            var probeDefs = new List<(string Label, Dictionary<string, float> Parameters)>
            {
                ("Base", space.Clamp(context.Project.BaseParameters)),
                ("Min", d > 0 ? space.FromUnit(Enumerable.Repeat(0.0, d).ToArray()) : space.Clamp(context.Project.BaseParameters)),
                ("Mid", d > 0 ? space.FromUnit(Enumerable.Repeat(0.5, d).ToArray()) : space.Clamp(context.Project.BaseParameters)),
                ("Max", d > 0 ? space.FromUnit(Enumerable.Repeat(1.0, d).ToArray()) : space.Clamp(context.Project.BaseParameters))
            };

            if (d > 0)
            {
                double[][] rands = LatinHypercube.Sample(2, d, new Random(RandomProbeSeed));
                probeDefs.Add(("Rand1", space.FromUnit(rands[0])));
                probeDefs.Add(("Rand2", space.FromUnit(rands[1])));
            }
            else
            {
                probeDefs.Add(("Rand1", space.Clamp(context.Project.BaseParameters)));
                probeDefs.Add(("Rand2", space.Clamp(context.Project.BaseParameters)));
            }

            var probes = new List<SmokeTestProbeResult>(probeDefs.Count);
            for (int i = 0; i < probeDefs.Count; i++)
            {
                var (label, prms) = probeDefs[i];
                string probeDir = Path.Combine(outputDirectory, $"probe_{i + 1}_{SanitizeName(label)}");
                probes.Add(EvaluateProbe(i + 1, label, prms, probeDir, geometry, constraintEvaluator, context));
            }

            SmokeTestProbeResult midProbe = probes[2]; // Mid
            var sensitivities = new List<SmokeTestSensitivityResult>(d);
            for (int idx = 0; idx < d; idx++)
            {
                string paramName = space.FreeNames[idx];
                double[] perturbedUnit = Enumerable.Repeat(0.5, d).ToArray();
                double step = perturbedUnit[idx] + SensitivityStepFraction <= 1.0
                    ? SensitivityStepFraction
                    : -SensitivityStepFraction;
                perturbedUnit[idx] += step;

                Dictionary<string, float> perturbedParams = space.FromUnit(perturbedUnit);
                float baseVal = midProbe.Parameters[paramName];
                float pertVal = perturbedParams[paramName];

                string sensDir = Path.Combine(outputDirectory, $"sens_{idx + 1}_{SanitizeName(paramName)}");
                SmokeTestProbeResult pertProbe = EvaluateProbe(
                    100 + idx + 1,
                    $"Sens_{paramName}",
                    perturbedParams,
                    sensDir,
                    geometry,
                    constraintEvaluator: null,
                    context);

                sensitivities.Add(CompareSensitivity(paramName, baseVal, pertVal, midProbe, pertProbe));
            }

            var warnings = new List<string>();
            var constrainedProbes = probes.Where(p => p.HasConstraints).ToList();
            if (constrainedProbes.Count > 0 && constrainedProbes.All(p => !p.ConstraintsSatisfied))
            {
                warnings.Add(
                    $"0 of {probes.Count} probe points satisfy pre-CFD geometry constraints; check ParameterBounds vs constraint limits.");
            }

            bool allProbesPassed = probes.All(p => p.Passed);
            bool allSensitivitiesPassed = sensitivities.All(s => s.GeometryValid && (s.Effective || s.IsFlowParameter));
            bool passed = allProbesPassed && allSensitivitiesPassed;

            int validProbes = probes.Count(p => p.Passed);
            int effectiveParams = sensitivities.Count(s => s.Effective || s.IsFlowParameter);
            string summary = passed
                ? $"PASS ({validProbes}/{probes.Count} probes valid, {effectiveParams}/{sensitivities.Count} parameter checks passed)"
                : $"FAIL ({validProbes}/{probes.Count} probes valid, {effectiveParams}/{sensitivities.Count} parameter checks passed)";

            var report = new SmokeTestReport
            {
                ProjectName = context.Name,
                OutputDirectory = outputDirectory,
                Probes = probes,
                Sensitivities = sensitivities,
                Warnings = warnings,
                Passed = passed,
                Summary = summary
            };

            if (logWriter != null)
            {
                PrintReport(report, logWriter);
            }

            return report;
        }

        private static SmokeTestProbeResult EvaluateProbe(
            int index,
            string label,
            Dictionary<string, float> parameters,
            string subDir,
            IGeometryGenerator geometry,
            IGeometryConstraintEvaluator? constraintEvaluator,
            ProjectContext context)
        {
            try
            {
                Directory.CreateDirectory(subDir);

                RubberBandScaler scaler = RubberBandScaler.Create(
                    context.Simulation.UseRubberBandScaler,
                    parameters,
                    context.Project.DimensionalParameters,
                    context.Simulation.TargetPicoGkSize);

                GeometryResult geoResult = geometry.GenerateAndExport(
                    0, index, scaler.ShrunkParameters, subDir, context.Simulation);

                string path = geoResult.GeometryPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return FailedProbe(label, parameters, path ?? "", geoResult.Kind, "Geometry file was not created.");
                }

                long fileSize = new FileInfo(path).Length;
                if (fileSize <= 0)
                {
                    return FailedProbe(label, parameters, path, geoResult.Kind, "Geometry file is empty (0 bytes).");
                }

                string sha256 = ComputeSha256(path);

                var record = new ModelRecord
                {
                    Iteration = 0,
                    Variant = index,
                    ActiveParameters = new Dictionary<string, float>(parameters),
                    StlPath = path
                };
                record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;

                var metrics = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var kvp in geoResult.Metrics)
                {
                    float restored = geoResult.ScalingFor(kvp.Key) switch
                    {
                        MetricScaling.Volume => scaler.RestoreVolume(kvp.Value),
                        MetricScaling.Area => scaler.RestoreArea(kvp.Value),
                        MetricScaling.Linear => scaler.RestoreLength(kvp.Value),
                        _ => kvp.Value
                    };
                    if (!float.IsFinite(restored))
                    {
                        return FailedProbe(label, parameters, path, geoResult.Kind,
                            $"Reported metric '{kvp.Key}' is non-finite ({restored}).");
                    }
                    record.PassiveParameters[kvp.Key] = restored;
                    metrics[kvp.Key] = restored;
                }

                bool is2D = geoResult.Kind == GeometryKind.Contour2D
                    || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

                int elementCount;
                int openEdges = 0;
                int nonManifoldEdges = 0;
                (Vector3 Min, Vector3 Max) bounds;

                if (is2D)
                {
                    ContourFile2D contourFile = Shape2D.LoadContour(path);
                    if (contourFile.Contours == null || contourFile.Contours.Count == 0
                        || !contourFile.Contours.Any(c => c.Points != null && c.Points.Count >= 3))
                    {
                        return FailedProbe(label, parameters, path, GeometryKind.Contour2D,
                            "2D contour JSON contains no valid closed loops.");
                    }

                    Shape2D shape = Shape2D.FromContourFile(path);
                    ContourMeasurement2D m = shape.Measure();
                    if (!float.IsFinite(m.Area) || m.Area <= 0f
                        || !float.IsFinite(m.Chord) || m.Chord <= 0f
                        || !float.IsFinite(m.Perimeter) || m.Perimeter <= 0f
                        || m.Bounds.IsEmpty)
                    {
                        return FailedProbe(label, parameters, path, GeometryKind.Contour2D,
                            $"Invalid 2D contour measurements (Area={m.Area}, Chord={m.Chord}, Perimeter={m.Perimeter}).");
                    }

                    elementCount = m.PointCount;
                    float invScale = scaler.ShrinkFactor > 0f ? 1.0f / scaler.ShrinkFactor : 1.0f;
                    bounds = (
                        new Vector3(m.Bounds.Min.X * invScale, m.Bounds.Min.Y * invScale, 0f),
                        new Vector3(m.Bounds.Max.X * invScale, m.Bounds.Max.Y * invScale, 0f));

                    metrics.TryAdd("Area", scaler.RestoreArea(m.Area));
                    metrics.TryAdd("Perimeter", scaler.RestoreLength(m.Perimeter));
                    metrics.TryAdd("Chord", scaler.RestoreLength(m.Chord));
                    metrics.TryAdd("Thickness", scaler.RestoreLength(m.Thickness));
                }
                else
                {
                    IReadOnlyList<StlTriangle> triangles = StlWriter.ReadTriangles(path);
                    if (triangles.Count == 0)
                    {
                        return FailedProbe(label, parameters, path, GeometryKind.SurfaceStl,
                            "3D STL contains 0 triangles or could not be parsed.");
                    }

                    MeshMeasurement m = MeshMetrics.Measure(triangles);
                    if (!float.IsFinite(m.Volume) || m.Volume <= 0f
                        || !float.IsFinite(m.SurfaceArea) || m.SurfaceArea <= 0f
                        || !float.IsFinite(m.FrontalProjectedArea) || m.Bounds.IsEmpty)
                    {
                        return FailedProbe(label, parameters, path, GeometryKind.SurfaceStl,
                            $"Invalid 3D mesh measurements (Volume={m.Volume}, SurfaceArea={m.SurfaceArea}, Frontal={m.FrontalProjectedArea}).");
                    }

                    (openEdges, nonManifoldEdges) = MeshMetrics.CountBoundaryAndNonManifoldEdges(triangles);
                    if (openEdges != 0 || nonManifoldEdges != 0)
                    {
                        return new SmokeTestProbeResult
                        {
                            Label = label,
                            Parameters = new Dictionary<string, float>(parameters),
                            GeometryPath = path,
                            Kind = GeometryKind.SurfaceStl,
                            FileSizeBytes = fileSize,
                            FileSha256 = sha256,
                            Passed = false,
                            ErrorMessage = $"Non-watertight STL: {openEdges} open edge(s), {nonManifoldEdges} non-manifold edge(s).",
                            ElementCount = triangles.Count,
                            OpenEdges = openEdges,
                            NonManifoldEdges = nonManifoldEdges,
                            Metrics = metrics,
                            Bounds = (
                                new Vector3(scaler.RestoreLength(m.Bounds.Min.X), scaler.RestoreLength(m.Bounds.Min.Y), scaler.RestoreLength(m.Bounds.Min.Z)),
                                new Vector3(scaler.RestoreLength(m.Bounds.Max.X), scaler.RestoreLength(m.Bounds.Max.Y), scaler.RestoreLength(m.Bounds.Max.Z)))
                        };
                    }

                    elementCount = triangles.Count;
                    bounds = (
                        new Vector3(scaler.RestoreLength(m.Bounds.Min.X), scaler.RestoreLength(m.Bounds.Min.Y), scaler.RestoreLength(m.Bounds.Min.Z)),
                        new Vector3(scaler.RestoreLength(m.Bounds.Max.X), scaler.RestoreLength(m.Bounds.Max.Y), scaler.RestoreLength(m.Bounds.Max.Z)));

                    metrics.TryAdd("Volume", scaler.RestoreVolume(m.Volume));
                    metrics.TryAdd("SurfaceArea", scaler.RestoreArea(m.SurfaceArea));
                    metrics.TryAdd("FrontalProjectedArea", scaler.RestoreArea(m.FrontalProjectedArea));
                }

                bool hasConstraints = false;
                bool constraintsSatisfied = true;
                var constraintList = new List<ConstraintEvaluation>();
                if (constraintEvaluator != null)
                {
                    IReadOnlyList<ConstraintEvaluation>? evaluated = constraintEvaluator.EvaluateGeometryConstraints(record, context.Project);
                    if (evaluated != null && evaluated.Count > 0)
                    {
                        hasConstraints = true;
                        constraintList.AddRange(evaluated);
                        constraintsSatisfied = evaluated.All(c => c.IsSatisfied);
                    }
                }

                return new SmokeTestProbeResult
                {
                    Label = label,
                    Parameters = new Dictionary<string, float>(parameters),
                    GeometryPath = path,
                    Kind = is2D ? GeometryKind.Contour2D : GeometryKind.SurfaceStl,
                    FileSizeBytes = fileSize,
                    FileSha256 = sha256,
                    Passed = true,
                    ElementCount = elementCount,
                    OpenEdges = openEdges,
                    NonManifoldEdges = nonManifoldEdges,
                    Metrics = metrics,
                    Bounds = bounds,
                    HasConstraints = hasConstraints,
                    ConstraintsSatisfied = constraintsSatisfied,
                    Constraints = constraintList
                };
            }
            catch (Exception ex)
            {
                return FailedProbe(label, parameters, "", GeometryKind.SurfaceStl, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static SmokeTestProbeResult FailedProbe(
            string label, Dictionary<string, float> parameters, string path, GeometryKind kind, string error) =>
            new()
            {
                Label = label,
                Parameters = new Dictionary<string, float>(parameters),
                GeometryPath = path,
                Kind = kind,
                Passed = false,
                ErrorMessage = error
            };

        private static SmokeTestSensitivityResult CompareSensitivity(
            string paramName,
            float baseVal,
            float pertVal,
            SmokeTestProbeResult mid,
            SmokeTestProbeResult pert)
        {
            bool isFlow = IsSolverFlowParameter(paramName);

            if (!mid.Passed)
            {
                return new SmokeTestSensitivityResult
                {
                    ParameterName = paramName,
                    BaseValue = baseVal,
                    PerturbedValue = pertVal,
                    Effective = false,
                    IsFlowParameter = isFlow,
                    GeometryValid = false,
                    Status = "FAIL (Mid baseline invalid)",
                    ErrorMessage = mid.ErrorMessage
                };
            }

            if (!pert.Passed)
            {
                return new SmokeTestSensitivityResult
                {
                    ParameterName = paramName,
                    BaseValue = baseVal,
                    PerturbedValue = pertVal,
                    Effective = false,
                    IsFlowParameter = isFlow,
                    GeometryValid = false,
                    Status = "FAIL (geometry error)",
                    ErrorMessage = pert.ErrorMessage
                };
            }

            double maxRelDelta = 0.0;
            foreach (var (key, midValue) in mid.Metrics)
            {
                if (pert.Metrics.TryGetValue(key, out float pertValue))
                {
                    double rel = Math.Abs(pertValue - midValue) / Math.Max(1.0, Math.Abs(midValue));
                    if (rel > maxRelDelta) maxRelDelta = rel;
                }
            }

            double maxBBoxDelta = MaxAbsDiff(mid.Bounds.Min, pert.Bounds.Min);
            maxBBoxDelta = Math.Max(maxBBoxDelta, MaxAbsDiff(mid.Bounds.Max, pert.Bounds.Max));

            bool shaChanged = !string.Equals(mid.FileSha256, pert.FileSha256, StringComparison.OrdinalIgnoreCase);
            bool effective = maxRelDelta > 1e-6 || maxBBoxDelta > 1e-5 || shaChanged;

            string status = effective
                ? "EFFECTIVE"
                : isFlow
                    ? "FLOW / SOLVER PARAMETER"
                    : "DEAD / INEFFECTIVE";

            return new SmokeTestSensitivityResult
            {
                ParameterName = paramName,
                BaseValue = baseVal,
                PerturbedValue = pertVal,
                Effective = effective,
                IsFlowParameter = isFlow,
                GeometryValid = true,
                MaxMetricRelativeDelta = maxRelDelta,
                MaxBBoxDelta = maxBBoxDelta,
                Sha256Changed = shaChanged,
                Status = status
            };
        }

        private static bool IsSolverFlowParameter(string name)
            => name.Equals("AngleOfAttackDeg", StringComparison.OrdinalIgnoreCase)
               || name.Equals("AngleOfAttack", StringComparison.OrdinalIgnoreCase);

        private static double MaxAbsDiff(Vector3 a, Vector3 b)
            => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }

        private static string SanitizeName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static void PrintReport(SmokeTestReport report, TextWriter writer)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            writer.WriteLine();
            writer.WriteLine("==================================================");
            writer.WriteLine($"  SMOKE TEST: {report.ProjectName}");
            writer.WriteLine($"  Output:     {JsonConfigLoader.Label(report.OutputDirectory)}");
            writer.WriteLine("==================================================");
            writer.WriteLine();
            writer.WriteLine("Geometry Probes:");
            writer.WriteLine($"  {"Probe",-8} {"Status",-6} {"Kind",-10} {"Elements",10} {"Size(B)",10} {"Constraints",-20} Details");
            writer.WriteLine($"  {new string('-', 8)} {new string('-', 6)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 20)} {new string('-', 28)}");

            foreach (SmokeTestProbeResult p in report.Probes)
            {
                string status = p.Passed ? "PASS" : "FAIL";
                string kind = p.Kind == GeometryKind.Contour2D ? "2D-JSON" : "3D-STL";
                string conStr = !p.HasConstraints
                    ? "(none)"
                    : p.ConstraintsSatisfied
                        ? "OK"
                        : "VIOLATED (" + string.Join(", ", p.Constraints.Where(c => !c.IsSatisfied).Select(c => $"{c.Name}:{c.Margin.ToString("0.##", inv)}")) + ")";

                string details = p.Passed
                    ? (p.Kind == GeometryKind.Contour2D
                        ? $"Area={p.Metrics.GetValueOrDefault("Area").ToString("G4", inv)} Chord={p.Metrics.GetValueOrDefault("Chord").ToString("G4", inv)}"
                        : $"Vol={p.Metrics.GetValueOrDefault("Volume").ToString("G4", inv)} Open={p.OpenEdges} NonMan={p.NonManifoldEdges}")
                    : (p.ErrorMessage ?? "Unknown error");

                writer.WriteLine($"  {p.Label,-8} {status,-6} {kind,-10} {p.ElementCount,10} {p.FileSizeBytes,10} {conStr,-20} {details}");
            }

            if (report.Sensitivities.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Parameter Sensitivity (+5% from Mid):");
                writer.WriteLine($"  {"Parameter",-22} {"Mid",10} {"Perturbed",10} {"MaxRelDelta",12} {"SHA256",8} Status");
                writer.WriteLine($"  {new string('-', 22)} {new string('-', 10)} {new string('-', 10)} {new string('-', 12)} {new string('-', 8)} {new string('-', 24)}");

                foreach (SmokeTestSensitivityResult s in report.Sensitivities)
                {
                    string sha = s.Sha256Changed ? "changed" : "same";
                    writer.WriteLine(
                        $"  {s.ParameterName,-22} {s.BaseValue.ToString("G5", inv),10} {s.PerturbedValue.ToString("G5", inv),10} "
                        + $"{s.MaxMetricRelativeDelta.ToString("0.000E+0", inv),12} {sha,-8} {s.Status}");
                }
            }

            foreach (string w in report.Warnings)
            {
                writer.WriteLine();
                writer.WriteLine($"{OutputConventions.WarningTag} {w}");
            }

            writer.WriteLine();
            writer.WriteLine($"Result: {report.Summary}");
        }
    }
}
