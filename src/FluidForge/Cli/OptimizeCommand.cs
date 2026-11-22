using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Cli
{
    /// <summary>
    /// Lädt die Projektkonfiguration, führt den Optimierungslauf aus und formatiert Fehlerberichte.
    /// </summary>
    internal static class OptimizeCommand
    {
        /// <summary>Lädt Framework- und Projektkonfiguration und baut daraus den Kontext.</summary>
        public static ProjectContext LoadConfiguration(IProjectDefinition definition, string? projectDirectoryArgument)
        {
            string name = definition.Name;
            string? projectDirectory = ProjectPaths.ResolveProjectDirectory(name, projectDirectoryArgument);

            if (projectDirectory == null)
                throw new InvalidOperationException(
                    $"[PROJEKT] Zum Projekt '{name}' wurde kein Ordner "
                    + $"{ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{name}/ gefunden. "
                    + "Dort liegen project.json, simulation.json und solvers/*.json. Gesucht wurde "
                    + $"aufwärts von {JsonConfigLoader.Label(Environment.CurrentDirectory)}; ein anderer Ort "
                    + $"lässt sich über die Umgebungsvariable {ProjectPaths.ProjectsDirectoryVariable} "
                    + "oder als zweites Aufrufargument angeben.");

            Console.WriteLine($"[CONFIG] Projektordner: {JsonConfigLoader.Label(projectDirectory)}");

            var simulation = JsonConfigLoader.LoadForProject(SimulationConfig.CreateDefault(), "simulation.json", projectDirectory);
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

        /// <summary>Verdrahtet die Bausteine des Projekts und startet den Lauf im Geometrie-Kernel.</summary>
        public static void RunOptimization(IProjectDefinition definition, ProjectContext context, string[] args, bool resume)
        {
            Console.WriteLine(resume ? $"[SYSTEM] Setze abgebrochenen Lauf fort: {context.Name}" : $"[SYSTEM] Starte Projekt: {context.Name}");

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
                Console.WriteLine("[WIEDERAUFNAHME] Der Lauf ist bereits abgeschlossen, nichts zu tun.");
                return;
            }

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed), context.Project.ParameterBounds);
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            (optimizer as IOptimizationPreflight)?.Preflight(simulationContext);

            var controller = new WorkflowController(
                geometry, stages, validator, optimizer, simulationContext, fitness.RequiredMetrics, journal)
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
                    Console.WriteLine("  AUTOMATED CFD OPTIMIZATION FRAMEWORK");
                    Console.WriteLine($"  Projekt:          {context.Name}");
                    Console.WriteLine($"  Geometrie-Kernel: {kernel.Name}");
                    Console.WriteLine($"  Ergebnisse:       {JsonConfigLoader.Label(simulationContext.WorkingDirectory)}");
                    Console.WriteLine($"  Konfiguration:    {JsonConfigLoader.Label(effectiveConfig)}");
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
                Console.WriteLine($"[WIEDERAUFNAHME] {plan.StoredSimulations} Rechnung(en) gesichert, Zufalls-Seed {seed} aus dem Journal.");
                RunInfoWriter.AddResume(workingDirectory, plan.StoredSimulations);
                return true;
            }

            seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            journal = RunJournal.StartNew(workingDirectory, RunResume.CreateHeader(context.Name, seed, configurationSections, source), out string? archived);
            if (archived != null) Console.WriteLine($"[SYSTEM] Vorheriges Journal archiviert: {JsonConfigLoader.Label(archived)}");

            int copied = RunInfoWriter.CopyProjectSources(context.ProjectDirectory, workingDirectory);
            Console.WriteLine($"[SYSTEM] Quellstand gesichert: {copied} Datei(en) nach {RunInfoWriter.SourceSnapshotDirectoryName}/");

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
                Console.WriteLine($"Netzfaktor: {context.Simulation.MeshRefinementFactor.ToString("0.00", CultureInfo.InvariantCulture)} (MANUELL aus simulation.json)");
                return;
            }

            var (decision, reportRelPath) = CalibrationCommand.ExecuteCalibration(
                definition, context, simulationContext, controller, fitness, validator, optimizer, journal);

            if (decision.Success && decision.SelectedStage != null)
            {
                simulationContext.EffectiveMeshRefinementFactor = decision.SelectedStage.Factor;
                if (string.IsNullOrEmpty(simulationContext.MeshRefinementSource)
                    || simulationContext.MeshRefinementSource.StartsWith("MANUELL", StringComparison.OrdinalIgnoreCase))
                {
                    simulationContext.MeshRefinementSource = $"KALIBRIERT am Laufanfang, Toleranz {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %, Stufe {decision.SelectedStage.LevelIndex} von {decision.AllStages.Count}, Bericht: {reportRelPath}";
                }
                simulationContext.CalibrationReportPath = reportRelPath;
                Console.WriteLine($"Netzfaktor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} ({simulationContext.MeshRefinementSource})");
            }
            else if (decision.FallbackStage != null)
            {
                var effectiveStage = decision.FallbackStage;
                Console.WriteLine($"[WARNUNG] Automatische Netzkalibrierung: {decision.SummaryMessage}");
                Console.WriteLine($"[WARNUNG] Nutze feinste validierte Rückfall-Stufe {effectiveStage.LevelIndex} (Faktor {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}) für den Optimierungslauf.");
                simulationContext.EffectiveMeshRefinementFactor = effectiveStage.Factor;
                simulationContext.MeshRefinementSource = $"KALIBRIERT (Rückfall auf Stufe {effectiveStage.LevelIndex}, Faktor {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}, Toleranz {context.Simulation.MeshCalibration.Tolerance * 100:0.0} % an Stufen-/Zellgrenze nicht ganz erreicht), Bericht: {reportRelPath}";
                simulationContext.CalibrationReportPath = reportRelPath;
                Console.WriteLine($"Netzfaktor: {effectiveStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} ({simulationContext.MeshRefinementSource})");
            }
            else
            {
                throw new InvalidOperationException(
                    $"[KALIBRIERUNG] Automatische Netzkalibrierung fehlgeschlagen: {decision.SummaryMessage}");
            }
        }

        /// <summary>Repo-Wurzel zum Projektordner (dort liegen .git bzw. deploy-info.json).</summary>
        private static string? RepositoryRootOf(ProjectContext context)
        {
            string? projectsRoot = string.IsNullOrEmpty(context.ProjectDirectory)
                ? null
                : Path.GetDirectoryName(context.ProjectDirectory);
            return ProjectPaths.ResolveRepositoryRoot(projectsRoot);
        }

        /// <summary>Trennt den Bedienfehler vom Absturz.</summary>
        public static void Report(Exception exception)
        {
            bool isUserError = exception is InvalidOperationException
                               && exception.Message.StartsWith("[", StringComparison.Ordinal);
            Console.WriteLine();
            Console.WriteLine(isUserError ? exception.Message : $"[KRITISCHER FEHLER] Programmabbruch:\n{exception}");
        }
    }
}
