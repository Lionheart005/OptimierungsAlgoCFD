using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Cli
{
    /// <summary>
    /// Loads project configuration, runs the optimization loop, and formats error reports.
    /// </summary>
    internal static class OptimizeCommand
    {
        /// <summary>Loads framework and project configuration and builds the <see cref="ProjectContext"/>.</summary>
        public static ProjectContext LoadConfiguration(IProjectDefinition definition, string? projectDirectoryArgument)
        {
            string name = definition.Name;
            string? projectDirectory = ProjectPaths.ResolveProjectDirectory(name, projectDirectoryArgument);

            if (projectDirectory == null)
                throw new InvalidOperationException(
                    $"[PROJECT] No folder {ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{name}/ "
                    + $"was found for project '{name}'. That folder holds project.json, simulation.json, and solvers/*.json. "
                    + $"Searched upwards from {JsonConfigLoader.Label(Environment.CurrentDirectory)}; another location "
                    + $"can be specified via the {ProjectPaths.ProjectsDirectoryVariable} environment variable "
                    + "or as the second positional argument.");

            Console.WriteLine($"{OutputConventions.ConfigTag} Project directory: {JsonConfigLoader.Label(projectDirectory)}");

            string? requestedPreset = JsonConfigLoader.PeekProjectStringKey("Preset", "simulation.json", projectDirectory);
            PresetDefinition? preset = SimulationPresets.Resolve(requestedPreset);
            if (preset != null)
            {
                Console.WriteLine($"{OutputConventions.ConfigTag} Active preset (Layer 1.5): {preset.Name}");
                if (preset.IsPlaceholder)
                {
                    Console.WriteLine(
                        $"{OutputConventions.ConfigTag} Notice: '{preset.Name}' is a Phase 3 placeholder (OpenFOAM); "
                        + "only budget/convergence defaults apply.");
                }
            }

            var simulation = JsonConfigLoader.LoadForProjectWithPreset(
                SimulationConfig.CreateDefault(),
                "simulation.json",
                projectDirectory,
                preset?.Name,
                preset?.SimulationOverrides);
            simulation.Report.PrintMachineOverrideWarning(name);

            var project = JsonConfigLoader.LoadForProject(
                ProjectConfigDto.FromProjectConfig(definition.CreateDefaults()), ProjectPaths.ManifestFileName, projectDirectory);
            project.Report.PrintMachineOverrideWarning(name);

            ProjectConfig projectConfig = project.Value.ToProjectConfig();
            projectConfig.ProjectName = name;

            var context = new ProjectContext(name, projectDirectory, null, simulation.Value, projectConfig);
            context.Record("simulation.json", simulation);
            context.Record(ProjectPaths.ManifestFileName, project);
            return context;
        }

        /// <summary>Wires the project components and launches the run inside the geometry kernel.</summary>
        public static void RunOptimization(IProjectDefinition definition, ProjectContext context, string[] args, bool resume)
        {
            Console.WriteLine(resume
                ? $"{OutputConventions.SystemTag} Resuming aborted run: {context.Name}"
                : $"{OutputConventions.SystemTag} Starting project: {context.Name}");

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();
            SolverStage[] stages = definition.CreateStages(context);

            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            var simulationReport = context.Loaded.FirstOrDefault(l => l.Name == "simulation.json")?.Report;
            MeshCalibration.ValidateConfig(context.Simulation, simulationReport, context.Name);
            MeshCalibration.ValidatePrecalibrationSupport(context.Simulation, definition);
            MeshCalibration.ValidateYPlusConsistency(
                context, calibrationActive: context.Simulation.MeshCalibration.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase));

            string workingDirectory = simulationContext.WorkingDirectory;
            if (!TryPrepareJournal(context, args, workingDirectory, resume, out int seed, out RunJournal? journal) || journal == null)
            {
                Console.WriteLine($"{OutputConventions.ResumeTag} Run is already completed; nothing to do.");
                return;
            }

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed), context.Project.ParameterBounds);
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            (optimizer as IOptimizationPreflight)?.Preflight(simulationContext);

            IGeometryConstraintEvaluator? geometryConstraints = definition.CreateGeometryConstraints(context);
            var controller = new WorkflowController(
                geometry, stages, validator, optimizer, simulationContext, fitness.RequiredMetrics, journal, geometryConstraints)
            {
                MeshDimensions = CalibrationCommand.ResolveMeshDimensions(context)
            };

            float effectiveVoxelResolution = VoxelResolutionAdvisor.Resolve(context.Simulation, context.Project, stages);

            kernel.RunHosted(effectiveVoxelResolution, () =>
            {
                try
                {
                    ApplyMeshCalibration(definition, context, simulationContext, controller, fitness, validator, optimizer, journal);

                    string effectiveConfig = EffectiveConfigWriter.Write(simulationContext.WorkingDirectory, context, simulationContext);
                    Console.WriteLine("==================================================");
                    Console.WriteLine("  FLUIDFORGE CFD OPTIMIZATION FRAMEWORK");
                    Console.WriteLine($"  Project:         {context.Name}");
                    Console.WriteLine($"  Geometry Kernel: {kernel.Name}");
                    Console.WriteLine($"  Results:         {JsonConfigLoader.Label(simulationContext.WorkingDirectory)}");
                    Console.WriteLine($"  Configuration:   {JsonConfigLoader.Label(effectiveConfig)}");
                    Console.WriteLine("==================================================");

                    controller.RunOptimization();
                    journal.MarkCompleted();
                    RunInfoWriter.Finish(workingDirectory, RunInfo.StatusCompleted);
                }
                catch (Exception)
                {
                    RunInfoWriter.Finish(workingDirectory, RunInfo.StatusAborted);
                    throw;
                }
            });
        }

        private static bool TryPrepareJournal(
            ProjectContext context, string[] args, string workingDirectory, bool resume, out int seed, out RunJournal? journal)
        {
            SortedDictionary<string, string> configurationSections = ConfigFingerprint.Sections(context);
            RunInfoSource source = RunInfoWriter.ReadSourceState(RepositoryRootOf(context));

            if (resume)
            {
                ResumePlan plan = RunResume.Prepare(workingDirectory, context.Name, configurationSections, context.Simulation.RandomSeed, source);
                if (plan.AlreadyCompleted || plan.Journal == null) { seed = 0; journal = null; return false; }

                seed = plan.Seed;
                journal = plan.Journal;
                Console.WriteLine($"{OutputConventions.ResumeTag} {plan.StoredSimulations} evaluation(s) restored, random seed {seed} from journal.");
                RunInfoWriter.AddResume(workingDirectory, plan.StoredSimulations);
                return true;
            }

            seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            journal = RunJournal.StartNew(workingDirectory, RunResume.CreateHeader(context.Name, seed, configurationSections, source), out string? archived);
            if (archived != null) Console.WriteLine($"{OutputConventions.SystemTag} Previous journal archived: {JsonConfigLoader.Label(archived)}");

            int copied = RunInfoWriter.CopyProjectSources(context.ProjectDirectory, workingDirectory);
            Console.WriteLine($"{OutputConventions.SystemTag} Source snapshot saved: {copied} file(s) to {RunInfoWriter.SourceSnapshotDirectoryName}/");

            Dictionary<string, string> toolVersions = ToolVersionProbe.Probe(context.Simulation);
            string? mpiWarning = ToolVersionProbe.MpiWarning(toolVersions, context.Simulation.MpiRunPath);
            if (mpiWarning != null) SimulationLog.Warning("[MPI] " + mpiWarning);

            RunInfo info = RunInfoWriter.Create(context.Name, seed, args, source, toolVersions, ConfigFingerprint.Combine(configurationSections));
            RunInfoWriter.Write(workingDirectory, info);
            return true;
        }

        private static void ApplyMeshCalibration(
            IProjectDefinition definition, ProjectContext context, SimulationContext simulationContext,
            WorkflowController controller, IFitnessCalculator fitness, IModelValidator validator,
            IOptimizationAlgorithm optimizer, RunJournal journal)
        {
            if (!context.Simulation.MeshCalibration.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                MeshCalibration.CheckManualCalibrationHints(context, simulationContext.WorkingDirectory);
                Console.WriteLine($"Mesh factor: {context.Simulation.MeshRefinementFactor.ToString("0.00", CultureInfo.InvariantCulture)} ({OutputConventions.ManualMeshSource})");
                return;
            }

            var (decision, reportRelPath) = CalibrationCommand.ExecuteCalibration(
                definition, context, simulationContext, controller, fitness, validator, optimizer, journal);

            if (decision.Success && decision.SelectedStage != null)
            {
                simulationContext.EffectiveMeshRefinementFactor = decision.SelectedStage.Factor;
                if (string.IsNullOrEmpty(simulationContext.MeshRefinementSource)
                    || simulationContext.MeshRefinementSource.StartsWith("MANUAL", StringComparison.OrdinalIgnoreCase))
                {
                    simulationContext.MeshRefinementSource = $"CALIBRATED at run start, tolerance {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %, level {decision.SelectedStage.LevelIndex} of {decision.AllStages.Count}, report: {reportRelPath}";
                }
                simulationContext.CalibrationReportPath = reportRelPath;
                Console.WriteLine($"Mesh factor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} ({simulationContext.MeshRefinementSource})");
            }
            else if (decision.FallbackStage != null)
            {
                var effectiveStage = decision.FallbackStage;
                Console.WriteLine($"{OutputConventions.WarningTag} Automatic mesh calibration: {decision.SummaryMessage}");
                Console.WriteLine($"{OutputConventions.WarningTag} Using finest validated fallback level {effectiveStage.LevelIndex} (factor {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}) for the optimization run.");
                simulationContext.EffectiveMeshRefinementFactor = effectiveStage.Factor;
                simulationContext.MeshRefinementSource = $"CALIBRATED (fallback to level {effectiveStage.LevelIndex}, factor {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}, tolerance {context.Simulation.MeshCalibration.Tolerance * 100:0.0} % not quite reached at stage/cell limit), report: {reportRelPath}";
                simulationContext.CalibrationReportPath = reportRelPath;
                Console.WriteLine($"Mesh factor: {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} ({simulationContext.MeshRefinementSource})");
            }
            else
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.CalibrationTag} Automatic mesh calibration failed: {decision.SummaryMessage}");
            }
        }

        /// <summary>Repository root for the project folder (contains .git or deploy-info.json).</summary>
        private static string? RepositoryRootOf(ProjectContext context)
        {
            string? projectsRoot = string.IsNullOrEmpty(context.ProjectDirectory)
                ? null
                : Path.GetDirectoryName(context.ProjectDirectory);
            return ProjectPaths.ResolveRepositoryRoot(projectsRoot);
        }

        /// <summary>Distinguishes user configuration/CLI errors from unexpected crashes.</summary>
        public static void Report(Exception exception)
        {
            if (exception == null) return;

            Console.WriteLine();

            if (exception is InvalidOperationException
                && exception.Message.StartsWith("[", StringComparison.Ordinal))
            {
                Console.WriteLine(exception.Message);
                return;
            }

            if (TryFindException<DllNotFoundException>(exception, out var dllNotFound) && dllNotFound != null)
            {
                bool isPicoGk = dllNotFound.Message.Contains("picogk", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"{OutputConventions.ErrorTag} Native library could not be loaded: {dllNotFound.Message}");
                Console.WriteLine(isPicoGk
                    ? $"{OutputConventions.ErrorTag} Fix: Ensure libpicogk.so (or libpicogk.26.2.so / PicoGK runtime) is present in '{AppContext.BaseDirectory}' or on LD_LIBRARY_PATH. "
                      + "Run './scripts/sim.ps1 doctor' (or 'src/FluidForge/setup_server.sh' on Ubuntu) to verify native dependencies."
                    : $"{OutputConventions.ErrorTag} Fix: Ensure the required native shared library is present in '{AppContext.BaseDirectory}' or your library path. "
                      + "Run './scripts/sim.ps1 doctor' (or 'src/FluidForge/setup_server.sh' on Ubuntu) to check environment dependencies.");
                return;
            }

            if (TryFindException<Win32Exception>(exception, out var win32Ex) && win32Ex != null)
            {
                Console.WriteLine($"{OutputConventions.ErrorTag} External executable failed to start: {win32Ex.Message}");
                Console.WriteLine(
                    $"{OutputConventions.ErrorTag} Fix: Check that external solver/mesher tools (e.g. gmsh, SU2_CFD, mpirun, simpleFoam) are installed and on PATH, "
                    + "or configure explicit paths (GmshPath, Su2Path, MpiRunPath, OpenFoamPath) in 'config/simulation.local.json'. "
                    + "Run './scripts/sim.ps1 doctor' to diagnose missing binaries.");
                return;
            }

            if (TryFindException<FileNotFoundException>(exception, out var fileNotFound) && fileNotFound != null)
            {
                string target = string.IsNullOrWhiteSpace(fileNotFound.FileName) ? string.Empty : $" ({fileNotFound.FileName})";
                Console.WriteLine($"{OutputConventions.ErrorTag} Required file not found{target}: {fileNotFound.Message}");
                Console.WriteLine(
                    $"{OutputConventions.ErrorTag} Fix: Verify that the project folder, input geometry/config paths, and tool paths in 'simulation.json' or 'config/simulation.local.json' exist.");
                return;
            }

            if (TryFindException<DirectoryNotFoundException>(exception, out var dirNotFound) && dirNotFound != null)
            {
                Console.WriteLine($"{OutputConventions.ErrorTag} Required directory not found: {dirNotFound.Message}");
                Console.WriteLine(
                    $"{OutputConventions.ErrorTag} Fix: Verify that the repository root, project directory, and configured working/tool directories exist.");
                return;
            }

            Console.WriteLine($"{OutputConventions.ErrorTag} [CRITICAL] Program aborted:\n{exception}");
        }

        private static bool TryFindException<T>(Exception root, out T? match) where T : Exception
        {
            if (root is T direct)
            {
                match = direct;
                return true;
            }

            if (root is AggregateException agg)
            {
                foreach (Exception inner in agg.InnerExceptions)
                {
                    if (TryFindException(inner, out match))
                        return true;
                }
            }

            if (root.InnerException != null && TryFindException(root.InnerException, out match))
            {
                return true;
            }

            match = null;
            return false;
        }
    }
}
