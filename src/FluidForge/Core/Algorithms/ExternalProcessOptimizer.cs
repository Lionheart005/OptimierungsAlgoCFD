using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace FluidForge.Core
{
    /// <summary>
    /// Transport abstraction for external optimizer workers (subprocess JSON exchange or test doubles).
    /// </summary>
    public interface IExternalOptimizerTransport
    {
        ExternalOptimizerResponse Ask(
            ExternalOptimizerRequest request,
            SimulationContext context,
            string requestFilePath,
            string responseFilePath);

        bool CheckAvailable(SimulationContext context, out string details);
    }

    /// <summary>
    /// Default subprocess transport invoking <c>&lt;python&gt; &lt;worker.py&gt; --request ... --response ...</c>.
    /// </summary>
    public sealed class ProcessExternalOptimizerTransport : IExternalOptimizerTransport
    {
        public const string InstallHint = "conda env create -f scripts/optimizers/environment_opt.yml";

        private readonly string _algorithm;

        public ProcessExternalOptimizerTransport(string algorithm = "BoTorch")
        {
            _algorithm = string.IsNullOrWhiteSpace(algorithm) ? "BoTorch" : algorithm.Trim();
        }

        public bool CheckAvailable(SimulationContext context, out string details)
        {
            ExternalOptimizerConfig cfg = context.Config.ExternalOptimizer ?? new ExternalOptimizerConfig();

            string? python = ResolvePythonExecutable(cfg);
            if (string.IsNullOrWhiteSpace(python))
            {
                details = $"Python executable not found. Install via: {InstallHint}";
                return false;
            }

            string? worker = ResolveWorkerScript(cfg, _algorithm);
            if (string.IsNullOrWhiteSpace(worker) || !File.Exists(worker))
            {
                details = $"Worker script for '{_algorithm}' not found (looked for {DefaultWorkerRelativePath(_algorithm)}).";
                return false;
            }

            try
            {
                int timeoutMs = Math.Clamp(cfg.TimeoutSeconds, 5, 30) * 1000;
                var (exitCode, stdout, stderr) = RunProcess(python, $"\"{worker}\" --check", timeoutMs);
                if (exitCode == 0)
                {
                    details = string.IsNullOrWhiteSpace(stdout)
                        ? $"{python} ({Path.GetFileName(worker)}) OK"
                        : stdout.Trim();
                    return true;
                }

                string errDetail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                details = $"Worker check failed (exit {exitCode}): {errDetail.Trim()}. Install/update via: {InstallHint}";
                return false;
            }
            catch (Exception ex)
            {
                details = $"Failed to execute '{python} {worker} --check': {ex.Message}. Install via: {InstallHint}";
                return false;
            }
        }

        public ExternalOptimizerResponse Ask(
            ExternalOptimizerRequest request,
            SimulationContext context,
            string requestFilePath,
            string responseFilePath)
        {
            ExternalOptimizerConfig cfg = context.Config.ExternalOptimizer ?? new ExternalOptimizerConfig();

            string? python = ResolvePythonExecutable(cfg);
            if (string.IsNullOrWhiteSpace(python))
            {
                throw new InvalidOperationException(
                    $"Python executable not found for external optimizer '{_algorithm}'. Install via: {InstallHint}");
            }

            string? worker = ResolveWorkerScript(cfg, string.IsNullOrWhiteSpace(request.Algorithm) ? _algorithm : request.Algorithm);
            if (string.IsNullOrWhiteSpace(worker) || !File.Exists(worker))
            {
                throw new FileNotFoundException(
                    $"External optimizer worker script not found for '{_algorithm}'.", worker ?? DefaultWorkerRelativePath(_algorithm));
            }

            string? reqDir = Path.GetDirectoryName(requestFilePath);
            if (!string.IsNullOrEmpty(reqDir)) Directory.CreateDirectory(reqDir);
            string? respDir = Path.GetDirectoryName(responseFilePath);
            if (!string.IsNullOrEmpty(respDir)) Directory.CreateDirectory(respDir);

            File.WriteAllText(requestFilePath, ExternalOptimizerProtocol.SerializeRequest(request), new UTF8Encoding(false));
            if (File.Exists(responseFilePath)) File.Delete(responseFilePath);

            int timeoutMs = Math.Max(5, cfg.TimeoutSeconds) * 1000;
            string args = $"\"{worker}\" --request \"{requestFilePath}\" --response \"{responseFilePath}\"";
            var (exitCode, stdout, stderr) = RunProcess(python, args, timeoutMs);

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                foreach (string line in stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.Contains("FutureWarning", StringComparison.OrdinalIgnoreCase)
                        || trimmed.Contains("UserWarning", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    SimulationLog.Warning($"[EXTERNAL-OPT] {trimmed}");
                }
            }

            ExternalOptimizerResponse? response = null;
            if (File.Exists(responseFilePath))
            {
                string respJson = File.ReadAllText(responseFilePath, Encoding.UTF8);
                response = ExternalOptimizerProtocol.DeserializeResponse(respJson);
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                string? jsonLine = ExtractJsonPayload(stdout);
                if (jsonLine != null)
                    response = ExternalOptimizerProtocol.DeserializeResponse(jsonLine);
            }

            if (exitCode != 0)
            {
                string workerErr = response?.Error ?? (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                throw new InvalidOperationException(
                    $"External optimizer '{_algorithm}' failed (exit code {exitCode}): {workerErr}".Trim());
            }

            if (response == null)
            {
                throw new InvalidOperationException(
                    $"External optimizer '{_algorithm}' produced no valid response JSON at '{responseFilePath}'.");
            }

            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                throw new InvalidOperationException(
                    $"External optimizer '{_algorithm}' reported an error: {response.Error}");
            }

            return response;
        }

        public static string? ResolvePythonExecutable(ExternalOptimizerConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.PythonExecutable))
            {
                string expanded = ExecutableResolver.ExpandHome(cfg.PythonExecutable.Trim());
                if ((expanded.Contains('/') || expanded.Contains('\\')) && File.Exists(expanded))
                    return expanded;
                return ExecutableResolver.FindInPath(expanded) ?? (File.Exists(expanded) ? expanded : null);
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string[] linuxCandidates =
                {
                    Path.Combine(home, "miniconda", "envs", "opt-py", "bin", "python"),
                    Path.Combine(home, "miniconda3", "envs", "opt-py", "bin", "python"),
                    Path.Combine(home, "miniforge3", "envs", "opt-py", "bin", "python"),
                    Path.Combine(home, "anaconda3", "envs", "opt-py", "bin", "python"),
                    "/opt/conda/envs/opt-py/bin/python"
                };
                foreach (string candidate in linuxCandidates)
                {
                    if (File.Exists(candidate)) return candidate;
                }

                return ExecutableResolver.FindInPath("python3") ?? ExecutableResolver.FindInPath("python");
            }
            else
            {
                string[] winCandidates =
                {
                    Path.Combine(home, "miniconda3", "envs", "opt-py", "python.exe"),
                    Path.Combine(home, "miniconda", "envs", "opt-py", "python.exe"),
                    Path.Combine(home, "miniforge3", "envs", "opt-py", "python.exe"),
                    Path.Combine(home, "anaconda3", "envs", "opt-py", "python.exe")
                };
                foreach (string candidate in winCandidates)
                {
                    if (File.Exists(candidate)) return candidate;
                }

                return ExecutableResolver.FindInPath("python") ?? ExecutableResolver.FindInPath("python3");
            }
        }

        public static string? ResolveWorkerScript(ExternalOptimizerConfig cfg, string algorithm)
        {
            string relOrExplicit = !string.IsNullOrWhiteSpace(cfg.WorkerScript)
                ? ExecutableResolver.ExpandHome(cfg.WorkerScript.Trim())
                : DefaultWorkerRelativePath(algorithm);

            if (Path.IsPathRooted(relOrExplicit))
                return File.Exists(relOrExplicit) ? relOrExplicit : null;

            string normalized = relOrExplicit
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            string?[] roots =
            {
                Directory.GetCurrentDirectory(),
                ProjectPaths.ResolveRepositoryRoot(),
                AppContext.BaseDirectory
            };

            foreach (string? root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                DirectoryInfo? dir = new DirectoryInfo(root);
                for (int depth = 0; depth <= 6 && dir != null; depth++)
                {
                    string candidate = Path.Combine(dir.FullName, normalized);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                    dir = dir.Parent;
                }
            }

            return null;
        }

        private static string DefaultWorkerRelativePath(string algorithm) =>
            string.Equals(algorithm, "Pymoo", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine("scripts", "optimizers", "pymoo_worker.py")
                : Path.Combine("scripts", "optimizers", "botorch_worker.py");

        private static string? ExtractJsonPayload(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
                return text.Substring(start, end - start + 1);
            return null;
        }

        private static (int ExitCode, string Stdout, string Stderr) RunProcess(string executable, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["PYTHONWARNINGS"] = "ignore";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            using var process = new Process { StartInfo = psi };
            var stdoutSb = new StringBuilder();
            var stderrSb = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdoutSb.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrSb.AppendLine(e.Data);
            };

            if (!process.Start())
                throw new InvalidOperationException($"Could not start process '{executable}'.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"Process '{executable} {arguments}' exceeded timeout of {timeoutMs / 1000}s.");
            }

            // Ensure async stdout/stderr callbacks have drained after WaitForExit(timeoutMs).
            process.WaitForExit();
            return (process.ExitCode, stdoutSb.ToString(), stderrSb.ToString());
        }
    }

    /// <summary>
    /// Hybrid optimization bridge delegating Ask proposals to external Python workers
    /// (<c>BoTorch</c>, <c>Pymoo</c>, or custom <c>ExternalPython</c>) while keeping
    /// evaluation, constraints, bounds clamping, Pareto export, and optional fallback to
    /// built-in C# <see cref="BayesOptimizationAlgorithm"/> in pure Core.
    /// </summary>
    public sealed class ExternalProcessOptimizer : IOptimizationAlgorithm, IOptimizationPreflight
    {
        private readonly IFitnessCalculator _fitnessCalculator;
        private readonly string _algorithm;
        private readonly IExternalOptimizerTransport _transport;
        private readonly Random _random;

        private ParameterSpace? _space;
        private bool _initialized;
        private bool _usingFallback;
        private BayesOptimizationAlgorithm? _fallback;
        private readonly Queue<Dictionary<string, float>> _batchQueue = new();

        public ExternalProcessOptimizer(
            IFitnessCalculator fitnessCalculator,
            string algorithm = "BoTorch",
            IExternalOptimizerTransport? transport = null,
            Random? random = null)
        {
            _fitnessCalculator = fitnessCalculator ?? throw new ArgumentNullException(nameof(fitnessCalculator));
            _algorithm = string.IsNullOrWhiteSpace(algorithm) ? "BoTorch" : algorithm.Trim();
            _transport = transport ?? new ProcessExternalOptimizerTransport(_algorithm);
            _random = random ?? new Random();
        }

        public string Name => _usingFallback && _fallback != null
            ? $"External ({_algorithm}) -> Fallback ({_fallback.Name})"
            : $"External Optimizer ({_algorithm})";

        public bool IsUsingFallback => _usingFallback;

        public ExternalOptimizerResponse? LastResponse { get; private set; }

        public IReadOnlyDictionary<string, double> LastArdImportances =>
            LastResponse?.ArdImportances ?? (IReadOnlyDictionary<string, double>)new Dictionary<string, double>();

        public void Preflight(SimulationContext context) => EnsureInitialized(context);

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            EnsureInitialized(context);

            if (_usingFallback)
            {
                return _fallback!.GenerateParameters(context, iteration, variant, maxVariants);
            }

            if (variant == 1 || _batchQueue.Count == 0)
            {
                int batchSize = Math.Max(1, maxVariants);
                if (!TryRequestBatch(context, iteration, batchSize))
                {
                    return _fallback!.GenerateParameters(context, iteration, variant, maxVariants);
                }
            }

            if (_batchQueue.Count > 0)
            {
                return new Dictionary<string, float>(_batchQueue.Dequeue());
            }

            return _space!.Clamp(context.CurrentBaseParameters);
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context)
        {
            if (_usingFallback)
            {
                return _fallback!.EvaluateAndSelectBest(records, context);
            }

            foreach (ModelRecord record in records)
            {
                _fitnessCalculator.CalculateFitness(record, context.Config);
            }

            // Combine History and any not-yet-appended records so Pareto export stays up-to-date
            // both when called per variant and at the end of an iteration.
            var combined = new List<ModelRecord>(context.History.Count + records.Count);
            var seen = new HashSet<ModelRecord>(ReferenceEqualityComparer.Instance);
            foreach (ModelRecord r in context.History)
            {
                if (seen.Add(r)) combined.Add(r);
            }
            foreach (ModelRecord r in records)
            {
                if (seen.Add(r)) combined.Add(r);
            }

            var nonCoarse = combined.Where(r => !FidelityLevels.IsCoarse(r.FidelityLevel)).ToList();
            var paretoSource = nonCoarse.Any(r => r.Objectives.Count >= 2) ? nonCoarse : combined;
            if (paretoSource.Any(r => r.Objectives.Count >= 2))
            {
                ParetoFrontCalculator.ExportParetoSummary(context.WorkingDirectory, paretoSource, copyStls: true);
            }

            return WorkflowController.SelectBestRecord(records) ?? records.First();
        }

        public void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord)
        {
            if (_usingFallback)
            {
                _fallback!.PrepareNextIteration(context, currentRecords, bestRecord);
                return;
            }

            _batchQueue.Clear();
        }

        private void EnsureInitialized(SimulationContext context)
        {
            if (_initialized) return;

            ParameterSpace space = ParameterSpace.Create(context);
            if (space.Dimension == 0)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} All parameters are fixed (Min == Max in ParameterBounds). "
                    + $"External optimizer '{_algorithm}' has nothing to optimize.");
            }

            _space = space;
            _initialized = true;

            ExternalOptimizerConfig cfg = context.Config.ExternalOptimizer ?? new ExternalOptimizerConfig();
            if (!_transport.CheckAvailable(context, out string details))
            {
                if (cfg.FallbackToBuiltInBayes)
                {
                    ActivateFallback(context, $"External optimizer '{_algorithm}' unavailable ({details}).");
                    return;
                }

                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} External optimizer '{_algorithm}' is unavailable: {details}. "
                    + $"Install via: {ProcessExternalOptimizerTransport.InstallHint}");
            }

            Console.WriteLine($"[EXTERNAL-OPT] {_algorithm} ready ({details}), {space.Dimension} free parameter(s).");
        }

        private bool TryRequestBatch(SimulationContext context, int iteration, int batchSize)
        {
            ExternalOptimizerConfig cfg = context.Config.ExternalOptimizer ?? new ExternalOptimizerConfig();
            ExternalOptimizerRequest request = BuildRequest(context, iteration, batchSize);

            string exchangeDir = Path.Combine(context.WorkingDirectory, OutputConventions.AnalysisDirectory, "external_opt");
            string requestPath = Path.Combine(exchangeDir, $"request_iter_{iteration:D3}.json");
            string responsePath = Path.Combine(exchangeDir, $"response_iter_{iteration:D3}.json");

            try
            {
                ExternalOptimizerResponse response = _transport.Ask(request, context, requestPath, responsePath);
                if (response.Candidates == null || response.Candidates.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"External optimizer '{_algorithm}' returned 0 candidates for iteration {iteration}.");
                }

                _batchQueue.Clear();
                for (int i = 0; i < batchSize; i++)
                {
                    ExternalCandidate cand = response.Candidates[i % response.Candidates.Count];
                    Dictionary<string, float> converted = ValidateAndClampCandidate(cand, context);
                    _batchQueue.Enqueue(converted);
                }

                LastResponse = response;
                LogResponseDiagnostics(response);
                return true;
            }
            catch (Exception ex) when (cfg.FallbackToBuiltInBayes)
            {
                ActivateFallback(context, $"External optimizer '{_algorithm}' Ask failed at iteration {iteration}: {ex.Message}");
                return false;
            }
            finally
            {
                if (!cfg.KeepExchangeFiles)
                {
                    try { if (File.Exists(requestPath)) File.Delete(requestPath); } catch { }
                    try { if (File.Exists(responsePath)) File.Delete(responsePath); } catch { }
                }
            }
        }

        private void ActivateFallback(SimulationContext context, string reason)
        {
            if (_usingFallback && _fallback != null) return;

            SimulationLog.Warning(
                $"{reason} Falling back to built-in C# Bayes optimizer. "
                + $"To enable Python backend, run: {ProcessExternalOptimizerTransport.InstallHint}");

            _usingFallback = true;
            _batchQueue.Clear();
            _fallback = new BayesOptimizationAlgorithm(_fitnessCalculator, random: _random);
            _fallback.Preflight(context);

            // Replay existing history into fallback queue preparation if mid-run.
            if (context.History.Count > 0)
            {
                ModelRecord? best = WorkflowController.SelectBestRecord(context.History);
                if (best != null)
                {
                    _fallback.PrepareNextIteration(context, new List<ModelRecord>(), best);
                }
            }
        }

        private ExternalOptimizerRequest BuildRequest(SimulationContext context, int iteration, int batchSize)
        {
            ExternalOptimizerConfig cfg = context.Config.ExternalOptimizer ?? new ExternalOptimizerConfig();
            ParameterSpace space = _space!;

            var parameters = new List<ExternalParameterSpec>(space.Dimension);
            foreach (string name in space.FreeNames)
            {
                var (min, max) = context.Project.ParameterBounds[name];
                double baseVal = context.CurrentBaseParameters.TryGetValue(name, out float bv)
                    ? Math.Clamp(bv, min, max)
                    : 0.5 * (min + max);
                parameters.Add(new ExternalParameterSpec
                {
                    Name = name,
                    Min = min,
                    Max = max,
                    BaseValue = baseVal
                });
            }

            var objectiveSpecs = new List<ExternalObjectiveSpec>();
            var seenObj = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModelRecord record in context.History)
            {
                foreach (ObjectiveValue obj in record.Objectives)
                {
                    if (seenObj.Add(obj.Name))
                    {
                        objectiveSpecs.Add(new ExternalObjectiveSpec
                        {
                            Name = obj.Name,
                            Direction = obj.Direction.ToString()
                        });
                    }
                }
            }

            bool useScalarFitnessFallback = objectiveSpecs.Count == 0 && context.History.Any(r => !r.SimulationFailed && float.IsFinite(r.Fitness));
            if (useScalarFitnessFallback)
            {
                objectiveSpecs.Add(new ExternalObjectiveSpec
                {
                    Name = "Fitness",
                    Direction = nameof(OptimizationDirection.Maximize)
                });
            }

            var constraintSpecs = new List<ExternalConstraintSpec>();
            var seenCon = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModelRecord record in context.History)
            {
                foreach (ConstraintEvaluation con in record.Constraints)
                {
                    if (seenCon.Add(con.Name))
                    {
                        constraintSpecs.Add(new ExternalConstraintSpec
                        {
                            Name = con.Name,
                            IsPreSolve = con.IsPreSolve
                        });
                    }
                }
            }

            var historyObs = new List<ExternalObservation>(context.History.Count);
            var fidelities = new SortedSet<double> { 1.0 };
            float coarseMeshFactor = context.Config.MultiFidelity?.CoarseMeshFactor ?? 2.0f;

            foreach (ModelRecord record in context.History)
            {
                if (record.Variant == SurrogateTrainingSet.ControlRunVariant) continue;

                string fidelityLevel = string.IsNullOrWhiteSpace(record.FidelityLevel)
                    ? FidelityLevels.High
                    : record.FidelityLevel;
                double fidelity = record.PassiveParameters.TryGetValue("Fidelity", out float fVal) && float.IsFinite(fVal) && fVal > 0f
                    ? fVal
                    : FidelityLevels.ToNumeric(fidelityLevel, coarseMeshFactor);
                fidelities.Add(fidelity);

                var paramDict = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (string name in space.FreeNames)
                {
                    if (record.ActiveParameters.TryGetValue(name, out float pVal) && float.IsFinite(pVal))
                    {
                        paramDict[name] = pVal;
                    }
                }

                var objDict = new Dictionary<string, double>(StringComparer.Ordinal);
                if (record.Objectives.Count > 0)
                {
                    foreach (ObjectiveValue obj in record.Objectives)
                    {
                        if (float.IsFinite(obj.Value))
                            objDict[obj.Name] = obj.Value;
                    }
                }
                else if (useScalarFitnessFallback && !record.SimulationFailed && float.IsFinite(record.Fitness))
                {
                    objDict["Fitness"] = record.Fitness;
                }

                var conDict = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (ConstraintEvaluation con in record.Constraints)
                {
                    if (float.IsFinite(con.Violation))
                        conDict[con.Name] = con.Violation;
                }

                historyObs.Add(new ExternalObservation
                {
                    Iteration = record.Iteration,
                    Variant = record.Variant,
                    Status = MapStatus(record),
                    Fidelity = fidelity,
                    FidelityLevel = fidelityLevel,
                    Fitness = float.IsFinite(record.Fitness) ? record.Fitness : null,
                    Parameters = paramDict,
                    Objectives = objDict,
                    ConstraintViolations = conDict
                });
            }

            bool twoStageEnabled = MultiFidelitySelector.IsTwoStageEnabled(context.Config.MultiFidelity);
            if (twoStageEnabled)
            {
                fidelities.Add(FidelityLevels.ToNumeric(FidelityLevels.Coarse, coarseMeshFactor));
            }

            string acquisition = string.IsNullOrWhiteSpace(cfg.Acquisition) ? "Auto" : cfg.Acquisition.Trim();
            bool multiFidelity = string.Equals(acquisition, "qMFKG", StringComparison.OrdinalIgnoreCase)
                                 || fidelities.Count > 1
                                 || twoStageEnabled;

            return new ExternalOptimizerRequest
            {
                ProtocolVersion = ExternalOptimizerProtocol.CurrentProtocolVersion,
                Algorithm = _algorithm,
                Acquisition = acquisition,
                Iteration = iteration,
                BatchSize = batchSize,
                Seed = _random.Next(1, int.MaxValue),
                MultiFidelity = multiFidelity,
                AvailableFidelities = fidelities.ToArray(),
                Parameters = parameters,
                Objectives = objectiveSpecs,
                Constraints = constraintSpecs,
                History = historyObs
            };
        }

        private Dictionary<string, float> ValidateAndClampCandidate(ExternalCandidate candidate, SimulationContext context)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var kv in context.CurrentBaseParameters)
            {
                string key = kv.Key;
                double raw = candidate.Parameters != null && candidate.Parameters.TryGetValue(key, out double proposed) && double.IsFinite(proposed)
                    ? proposed
                    : kv.Value;

                if (context.Project.ParameterBounds.TryGetValue(key, out var bounds))
                {
                    float min = Math.Min(bounds.Min, bounds.Max);
                    float max = Math.Max(bounds.Min, bounds.Max);
                    result[key] = Math.Clamp((float)raw, min, max);
                }
                else
                {
                    result[key] = (float)raw;
                }
            }
            return result;
        }

        private static string MapStatus(ModelRecord record)
        {
            if (record.Disqualified) return "Disqualified";
            if (record.SimulationFailed) return "Failed";
            if (record.IsPreConstraintInfeasible) return "InfeasiblePreSolve";
            if (!record.Feasible) return "InfeasiblePostSolve";
            return "Feasible";
        }

        private static void LogResponseDiagnostics(ExternalOptimizerResponse response)
        {
            int paretoCount = response.ParetoMask?.Count(m => m) ?? 0;
            string hvStr = response.Hypervolume.HasValue
                ? $", hypervolume={response.Hypervolume.Value.ToString("G5", CultureInfo.InvariantCulture)}"
                : string.Empty;

            if (paretoCount > 0 || response.Hypervolume.HasValue)
            {
                Console.WriteLine($"[EXTERNAL-OPT] {response.Worker}: {response.Candidates.Count} candidate(s), Pareto={paretoCount}{hvStr}");
            }

            if (response.ArdImportances != null && response.ArdImportances.Count > 0)
            {
                string ardSummary = string.Join(", ", response.ArdImportances
                    .OrderByDescending(kv => kv.Value)
                    .Take(6)
                    .Select(kv => $"{kv.Key}={kv.Value.ToString("P1", CultureInfo.InvariantCulture)}"));
                Console.WriteLine($"[EXTERNAL-OPT] ARD importances: {ardSummary}");
            }
        }
    }
}
