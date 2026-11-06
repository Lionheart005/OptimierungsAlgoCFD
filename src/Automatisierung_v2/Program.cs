using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.RegularExpressions;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace MyPicoGkProject
{
    /// <summary>
    /// Composition Root. Bestimmt den Projektnamen, lässt die <see cref="ProjectRegistry"/> die
    /// passende Definition heraussuchen, lädt die Konfiguration und startet den Kernel.
    ///
    /// <para>
    /// Seit TODO-23 steht hier <b>kein</b> <c>switch</c> über Projektnamen mehr — ein neues
    /// Projekt ist ein Ordner mit einer <see cref="IProjectDefinition"/>, und diese Datei wird
    /// dafür nie wieder angefasst.
    /// </para>
    /// </summary>
    class Program
    {
        /// <summary>Umgebungsvariable, die <c>scripts/sim-runner.sh</c> setzt.</summary>
        private const string ProjectVariable = "SIM_PROJECT";

        private const string ListProjectsFlag = "--list-projects";
        private const string PrintConfigFlag = "--print-config";
        private const string CalibrateFlag = "--calibrate";
        private const string RecheckFlag = "--recheck";
        private const string ResumeFlag = "--resume";

        /// <summary>
        /// Aufruf: <c>&lt;Projektname&gt; [Projektverzeichnis] [--list-projects] [--print-config]
        /// [--calibrate] [--recheck[=Variante]] [--resume]</c>
        /// </summary>
        /// <returns>
        /// 0 bei Erfolg, 1 bei Abbruch. Vor TODO-23 fing diese Methode jede Ausnahme ab und
        /// endete trotzdem mit 0 — <c>sim-runner.sh</c> schrieb diese 0 in <c>.last_exit</c> und
        /// meldete einen abgestürzten Lauf als erfolgreich.
        /// </returns>
        static int Main(string[] args)
        {
            try
            {
                // Flags von Stellungsargumenten trennen, damit die Reihenfolge egal ist.
                var flags = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
                                .Select(argument => argument.Trim().ToLowerInvariant())
                                .ToHashSet();

                bool isCalibrate = false;
                bool isRecheck = false;
                string? recheckVariant = null;
                int recheckFiner = 1;
                var positionalList = new List<string>();

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].Trim();
                    if (a.Equals(CalibrateFlag, StringComparison.OrdinalIgnoreCase))
                    {
                        isCalibrate = true;
                    }
                    else if (a.Equals(RecheckFlag, StringComparison.OrdinalIgnoreCase))
                    {
                        isRecheck = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            recheckVariant = args[++i].Trim();
                        }
                    }
                    else if (a.StartsWith(RecheckFlag + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        isRecheck = true;
                        recheckVariant = a.Substring((RecheckFlag + "=").Length).Trim();
                    }
                    else if (a.Equals("--finer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 < args.Length && int.TryParse(args[i + 1].Trim(), out int fVal))
                        {
                            recheckFiner = fVal;
                            i++;
                        }
                    }
                    else if (a.StartsWith("--finer=", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(a.Substring("--finer=".Length).Trim(), out recheckFiner);
                    }
                    else if (!a.StartsWith("--", StringComparison.Ordinal))
                    {
                        positionalList.Add(a);
                    }
                }
                var positional = positionalList.ToArray();

                string? projectDirectoryArgument = positional.Length > 1 ? positional[1] : null;

                var registry = ProjectRegistry.Discover();

                // --list-projects braucht kein Projekt und keinen Kernel.
                if (flags.Contains(ListProjectsFlag))
                {
                    PrintProjects(registry, projectDirectoryArgument);
                    return 0;
                }

                string projectName = ResolveProjectName(positional, registry);
                IProjectDefinition definition = registry.Resolve(projectName);

                // Ordner gegen Definitionen abgleichen — beide Richtungen, beide Fehler wären
                // sonst still. Ohne vorhandenes src/projects/ entfällt die Prüfung (TODO-24).
                registry.AssertFoldersMatchDefinitions(projectDirectoryArgument);

                ProjectContext context = LoadConfiguration(definition, projectDirectoryArgument);

                // --print-config gibt die wirksame Konfiguration aus und startet nichts.
                // scripts/sim-runner.sh liest davon die Programmpfade, statt sie mit sed aus
                // einer JSON zu fischen, die gar nicht mehr die maßgebliche ist (TODO-28).
                if (flags.Contains(PrintConfigFlag))
                {
                    PrintConfiguration(context);
                    return 0;
                }

                if (isCalibrate)
                {
                    RunCalibration(definition, context);
                    return 0;
                }

                if (isRecheck)
                {
                    bool isUnsteady = flags.Contains("--unsteady");
                    RunRecheck(definition, context, recheckVariant, recheckFiner, isUnsteady);
                    return 0;
                }

                RunOptimization(definition, context, args, resume: flags.Contains(ResumeFlag));
                return 0;
            }
            catch (Exception e)
            {
                Report(e);
                return 1;
            }
        }

        /// <summary>
        /// Trennt den Bedienfehler vom Absturz. Ein unbekannter Projektname oder ein Tippfehler
        /// in einer JSON ist kein Programmfehler — der volle Stacktrace begräbt dort nur die
        /// eine Zeile, die der Nutzer lesen soll. Alles andere wird weiterhin vollständig
        /// ausgegeben, inklusive Stacktrace und innerer Ausnahmen.
        /// </summary>
        private static void Report(Exception exception)
        {
            // Unsere eigenen Meldungen tragen ein Präfix in eckigen Klammern ([PROJEKT], [CONFIG],
            // [KONFIGURATION]) und sind so formuliert, dass sie für sich allein stehen.
            bool isUserError = exception is InvalidOperationException
                               && exception.Message.StartsWith("[", StringComparison.Ordinal);

            Console.WriteLine();

            if (isUserError)
            {
                Console.WriteLine(exception.Message);
                return;
            }

            Console.WriteLine($"[KRITISCHER FEHLER] Programmabbruch:\n{exception}");
        }

        /// <summary>
        /// Projektname aus dem ersten Argument oder der Umgebungsvariablen. <b>Kein stiller
        /// Standard mehr:</b> ein Vertipper im Startskript würde sonst klaglos das falsche
        /// Projekt rechnen — stundenlang, mit plausibel aussehenden Ergebnissen.
        /// </summary>
        private static string ResolveProjectName(string[] positional, ProjectRegistry registry)
        {
            if (positional.Length > 0 && !string.IsNullOrWhiteSpace(positional[0]))
                return positional[0].Trim();

            string? fromEnvironment = Environment.GetEnvironmentVariable(ProjectVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                Console.WriteLine($"[SYSTEM] Projektname aus {ProjectVariable}.");
                return fromEnvironment.Trim();
            }

            throw new InvalidOperationException(
                $"[PROJEKT] Es wurde kein Projekt angegeben (weder als Argument noch über {ProjectVariable}). "
                + registry.AvailableProjects()
                + " Aufruf:  .\\scripts\\sim.ps1 run -Project <Projektname>");
        }

        /// <summary>Lädt Framework- und Projektkonfiguration und baut daraus den Kontext.</summary>
        private static ProjectContext LoadConfiguration(IProjectDefinition definition, string? projectDirectoryArgument)
        {
            string name = definition.Name;
            string? projectDirectory = ProjectPaths.ResolveProjectDirectory(name, projectDirectoryArgument);

            // Ohne Ordner gibt es keine Zahlen. Weiterzurechnen hiesse, stillschweigend mit den
            // Code-Standards zu arbeiten — stundenlang, mit plausibel aussehenden Ergebnissen,
            // die nichts mit den eingestellten Werten zu tun haben.
            if (projectDirectory == null)
                throw new InvalidOperationException(
                    $"[PROJEKT] Zum Projekt '{name}' wurde kein Ordner "
                    + $"{ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{name}/ gefunden. "
                    + "Dort liegen project.json, simulation.json und solvers/*.json. Gesucht wurde "
                    + $"aufwärts von {JsonConfigLoader.Label(Environment.CurrentDirectory)}; ein anderer Ort "
                    + $"lässt sich über die Umgebungsvariable {ProjectPaths.ProjectsDirectoryVariable} "
                    + "oder als zweites Aufrufargument angeben.");

            Console.WriteLine($"[CONFIG] Projektordner: {JsonConfigLoader.Label(projectDirectory)}");

            var simulation = JsonConfigLoader.LoadForProject(
                SimulationConfig.CreateDefault(), "simulation.json", projectDirectory);
            simulation.Report.PrintMachineOverrideWarning(name);

            var project = JsonConfigLoader.LoadForProject(
                ProjectConfigDto.FromProjectConfig(definition.CreateDefaults()),
                ProjectPaths.ManifestFileName, projectDirectory);
            project.Report.PrintMachineOverrideWarning(name);

            ProjectConfig projectConfig = project.Value.ToProjectConfig();

            // Der Ordnername ist der Projektname (Entscheidung 1) — die project.json führt das
            // Feld nicht mehr, also wird es hier gesetzt.
            projectConfig.ProjectName = name;

            var context = new ProjectContext(
                name, projectDirectory, null, simulation.Value, projectConfig);

            context.Record("simulation.json", simulation);
            context.Record(ProjectPaths.ManifestFileName, project);

            return context;
        }

        /// <summary>
        /// Verdrahtet die Bausteine des Projekts und startet den Lauf im Geometrie-Kernel.
        /// Mit <paramref name="resume"/> wird ein abgebrochener Lauf aus dem Journal fortgesetzt
        /// (TODO-52), sonst beginnt ein neuer Lauf mit neuem Journal (das alte wird archiviert).
        /// </summary>
        private static void RunOptimization(IProjectDefinition definition, ProjectContext context, string[] args, bool resume)
        {
            Console.WriteLine(resume
                ? $"[SYSTEM] Setze abgebrochenen Lauf fort: {context.Name}"
                : $"[SYSTEM] Starte Projekt: {context.Name}");

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();

            // CreateStages lädt die Solver-Optionen — muss also vor dem Schreiben der
            // effective-config.json laufen, sonst fehlen sie dort.
            SolverStage[] stages = definition.CreateStages(context);

            // Ergebnisse/<Projekt>/ statt eines gemeinsamen Topfes (TODO-25): zwei Projekte
            // überschreiben sich sonst gegenseitig die Simulation_Results.csv.
            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            // 1. Validierung der Kalibrierungskonfiguration (Regel 1 & TODO-48)
            var simulationReport = context.Loaded.FirstOrDefault(l => l.Name == "simulation.json")?.Report;
            MeshCalibration.ValidateConfig(context.Simulation, simulationReport, context.Name);
            MeshCalibration.ValidatePrecalibrationSupport(context.Simulation, definition);

            // Reproduzierbarkeit und Wiederaufnahme (TODO-50 bis TODO-52). Der Fingerabdruck
            // entsteht NACH CreateStages, sonst fehlen die Solver-Dateien darin.
            string workingDirectory = simulationContext.WorkingDirectory;
            SortedDictionary<string, string> configurationSections = ConfigFingerprint.Sections(context);
            RunInfoSource source = RunInfoWriter.ReadSourceState(RepositoryRootOf(context));

            int seed;
            RunJournal journal;

            if (resume)
            {
                ResumePlan plan = RunResume.Prepare(
                    workingDirectory, context.Name, configurationSections, context.Simulation.RandomSeed, source);

                if (plan.AlreadyCompleted || plan.Journal == null)
                {
                    Console.WriteLine("[WIEDERAUFNAHME] Der Lauf ist bereits abgeschlossen, nichts zu tun.");
                    return;
                }

                seed = plan.Seed;
                journal = plan.Journal;
                Console.WriteLine($"[WIEDERAUFNAHME] {plan.StoredSimulations} Rechnung(en) gesichert, Zufalls-Seed {seed} aus dem Journal.");
                RunInfoWriter.AddResume(workingDirectory, plan.StoredSimulations);
            }
            else
            {
                seed = RunSeed.Resolve(context.Simulation.RandomSeed);
                Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

                journal = RunJournal.StartNew(
                    workingDirectory,
                    RunResume.CreateHeader(context.Name, seed, configurationSections, source),
                    out string? archived);
                if (archived != null)
                    Console.WriteLine($"[SYSTEM] Vorheriges Journal archiviert: {JsonConfigLoader.Label(archived)}");

                int copied = RunInfoWriter.CopyProjectSources(context.ProjectDirectory, workingDirectory);
                Console.WriteLine($"[SYSTEM] Quellstand gesichert: {copied} Datei(en) nach {RunInfoWriter.SourceSnapshotDirectoryName}/");

                RunInfo info = RunInfoWriter.Create(
                    context.Name, seed, args, source,
                    ToolVersionProbe.Probe(context.Simulation),
                    ConfigFingerprint.Combine(configurationSections));
                RunInfoWriter.Write(workingDirectory, info);
            }

            // Der Bouncer ist Framework-Bestandteil und prüft die Fitness, nicht eine
            // projektspezifische Metrik (Entscheidung 6).
            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed));

            // Optimierungsverfahren: steht als "OptimizationAlgorithm" in der simulation.json.
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));

            // Die Pflicht-Metriken gehen als reine Namensliste hinein, nicht als Rechner:
            // der Controller bewertet nichts selbst (TODO-13).
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics,
                journal);

            kernel.RunHosted(context.Simulation.BaseVoxelResolution, () =>
            {
                try
                {
                    if (context.Simulation.MeshCalibration.Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        var (decision, reportRelPath) = ExecuteCalibration(
                            definition, context, simulationContext, controller, fitness, validator, optimizer, journal);

                        if (!decision.Success || decision.SelectedStage == null)
                        {
                            throw new InvalidOperationException(
                                $"[KALIBRIERUNG] Automatische Netzkalibrierung fehlgeschlagen: {decision.SummaryMessage}");
                        }

                        simulationContext.EffectiveMeshRefinementFactor = decision.SelectedStage.Factor;
                        if (string.IsNullOrEmpty(simulationContext.MeshRefinementSource)
                            || simulationContext.MeshRefinementSource.StartsWith("MANUELL", StringComparison.OrdinalIgnoreCase))
                        {
                            simulationContext.MeshRefinementSource = $"KALIBRIERT am Laufanfang, Toleranz {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %, Stufe {decision.SelectedStage.LevelIndex} von {decision.AllStages.Count}, Bericht: {reportRelPath}";
                        }
                        simulationContext.CalibrationReportPath = reportRelPath;

                        Console.WriteLine($"Netzfaktor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} ({simulationContext.MeshRefinementSource})");
                    }
                    else
                    {
                        MeshCalibration.CheckManualCalibrationHints(context, simulationContext.WorkingDirectory);
                        Console.WriteLine($"Netzfaktor: {context.Simulation.MeshRefinementFactor.ToString("0.00", CultureInfo.InvariantCulture)} (MANUELL aus simulation.json)");
                    }

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
                    // Ein harter Abbruch (Neustart, kill) kommt hier nie an; dann bleibt "laeuft"
                    // stehen, und status erkennt den Fall am Journal ohne Schlusszeile (TODO-52).
                    RunInfoWriter.Finish(workingDirectory, RunInfo.StatusAborted);
                    throw;
                }
            });
        }

        /// <summary>Repo-Wurzel zum Projektordner (dort liegen .git bzw. deploy-info.json).</summary>
        private static string? RepositoryRootOf(ProjectContext context)
        {
            string? projectsRoot = string.IsNullOrEmpty(context.ProjectDirectory)
                ? null
                : Path.GetDirectoryName(context.ProjectDirectory);
            return ProjectPaths.ResolveRepositoryRoot(projectsRoot);
        }

        /// <summary>
        /// Führt eine isolierte Netzkalibrierung auf der Startgeometrie durch (--calibrate, TODO-42 / TODO-48).
        /// </summary>
        private static void RunCalibration(IProjectDefinition definition, ProjectContext context)
        {
            Console.WriteLine($"[SYSTEM] Starte Kalibrierung für Projekt: {context.Name}");

            MeshCalibration.ValidatePrecalibrationSupport(context.Simulation, definition);

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();
            SolverStage[] stages = definition.CreateStages(context);

            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            EffectiveConfigWriter.Write(simulationContext.WorkingDirectory, context, simulationContext);

            int seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed));
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics);

            kernel.RunHosted(context.Simulation.BaseVoxelResolution, () =>
            {
                var (decision, reportRelPath) = ExecuteCalibration(
                    definition, context, simulationContext, controller, fitness, validator, optimizer);

                if (decision.Success && decision.SelectedStage != null)
                {
                    Console.WriteLine($"\nEmpfohlene Zeile für src/projects/{context.Name}/simulation.json:");
                    Console.WriteLine($"  \"MeshRefinementFactor\": {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            });
        }

        private static (CalibrationDecision Decision, string ReportRelativePath) ExecuteCalibration(
            IProjectDefinition definition,
            ProjectContext context,
            SimulationContext simulationContext,
            WorkflowController controller3D,
            IFitnessCalculator fitness,
            IModelValidator validator,
            IOptimizationAlgorithm optimizer,
            RunJournal? journal = null)
        {
            Func<float, string, ModelRecord> runStage3D = (factor, tag) =>
            {
                var record = controller3D.RunPipeline(
                    context.Project.BaseParameters,
                    1,
                    1,
                    customRefinementFactor: factor,
                    customTag: tag);
                fitness.CalculateFitness(record, context.Simulation);
                return record;
            };

            if (context.Simulation.MeshCalibration.Use2DPrecalibration && definition is IHasRepresentativeSection sectionProvider)
            {
                IGeometryGenerator section2D = sectionProvider.CreateSection2D(context);
                var gmsh2D = new Gmsh2DMesherOptions();
                var su2Options2D = context.LoadSolverOptions<Su2SolverOptions>("su2");
                su2Options2D.Dimensions = 2;
                FlowConditions flow2D = FlowConditions.From(context.Simulation, su2Options2D);
                var stages2D = new[]
                {
                    new SolverStage(new Gmsh2DMesher(gmsh2D, flow2D), new Su2Solver(su2Options2D, flow2D))
                };
                var controller2D = new WorkflowController(
                    section2D,
                    stages2D,
                    validator,
                    optimizer,
                    simulationContext,
                    fitness.RequiredMetrics,
                    journal);   // dieselbe Instanz wie der 3D-Controller (TODO-51)

                return MeshCalibration.RunWithPrecalibration2D(
                    context,
                    simulationContext,
                    (factor, tag) =>
                    {
                        var record = controller2D.RunPipeline(
                            context.Project.BaseParameters,
                            1,
                            1,
                            customRefinementFactor: factor,
                            customTag: tag);
                        fitness.CalculateFitness(record, context.Simulation);
                        return record;
                    },
                    runStage3D);
            }

            return MeshCalibration.Run(context, simulationContext, runStage3D);
        }

        /// <summary>
        /// Rechnet eine einzelne Variante aus Simulation_Results.csv fein nach (--recheck, TODO-43 / TODO-37).
        /// </summary>
        private static void RunRecheck(IProjectDefinition definition, ProjectContext context, string? variantTarget, int finer, bool isUnsteady = false)
        {
            Console.WriteLine($"[SYSTEM] Starte Recheck für Projekt: {context.Name} (Ziel: '{variantTarget ?? "letzte"}', Finer: {finer}, Unsteady: {isUnsteady})");

            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            string csvPath = Path.Combine(simulationContext.WorkingDirectory, "Simulation_Results.csv");
            if (!File.Exists(csvPath))
            {
                throw new InvalidOperationException(
                    $"[RECHECK] Die Datei '{csvPath}' existiert nicht. Bitte zuerst eine Simulation laufen lassen.");
            }

            string[] lines = File.ReadAllLines(csvPath);
            if (lines.Length <= 1)
            {
                throw new InvalidOperationException($"[RECHECK] '{csvPath}' enthält keine Datensätze.");
            }

            string[] headers = lines[0].Split(';');

            int targetIter = -1;
            int targetVar = -1;

            if (!string.IsNullOrWhiteSpace(variantTarget))
            {
                var match = Regex.Match(variantTarget, @"(?:Gen)?(?<iter>\d+)[_vVar]*(?<var>\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    targetIter = int.Parse(match.Groups["iter"].Value);
                    targetVar = int.Parse(match.Groups["var"].Value);
                }
            }

            string? selectedLine = null;
            if (targetIter > 0 && targetVar > 0)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split(';');
                    if (parts.Length > 1 && int.TryParse(parts[0], out int it) && int.TryParse(parts[1], out int vr))
                    {
                        if (it == targetIter && vr == targetVar)
                        {
                            selectedLine = lines[i];
                            break;
                        }
                    }
                }
                if (selectedLine == null)
                {
                    throw new InvalidOperationException($"[RECHECK] Variante Gen{targetIter}_Var{targetVar} wurde in '{csvPath}' nicht gefunden.");
                }
            }
            else
            {
                selectedLine = lines.Last(l => !string.IsNullOrWhiteSpace(l));
            }

            var cols = selectedLine.Split(';');
            targetIter = int.Parse(cols[0]);
            targetVar = int.Parse(cols[1]);

            var activeParams = new Dictionary<string, float>();
            float originalFitness = 0f;
            var originalMetrics = new Dictionary<string, float>();

            for (int i = 2; i < headers.Length; i++)
            {
                string header = headers[i].Trim();
                if (context.Project.BaseParameters.ContainsKey(header))
                {
                    if (float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        activeParams[header] = val;
                    }
                }
                else if (header.Equals("Fitness", StringComparison.OrdinalIgnoreCase))
                {
                    float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out originalFitness);
                }
                else
                {
                    if (float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float pVal))
                    {
                        originalMetrics[header] = pVal;
                    }
                }
            }

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();
            SolverStage[] stages = definition.CreateStages(context);
            if (isUnsteady)
            {
                foreach (var stage in stages)
                {
                    if (stage.Solver is Su2Solver su2)
                    {
                        su2.Options.TimeDomain.Enabled = true;
                    }
                }
            }

            int seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed));
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics);

            float fineFactor = context.Simulation.MeshRefinementFactor / MathF.Pow(MathF.Sqrt(2.0f), Math.Max(1, finer));

            Console.WriteLine($"[RECHECK] Gefundene Variante Gen{targetIter}_Var{targetVar}: Original-Fitness={originalFitness:F4}, Recheck-Netzfaktor={fineFactor:0.00}");

            kernel.RunHosted(context.Simulation.BaseVoxelResolution, () =>
            {
                var record = controller.RunPipeline(
                    activeParams,
                    targetIter,
                    targetVar,
                    customRefinementFactor: fineFactor,
                    customTag: "Recheck");

                fitness.CalculateFitness(record, context.Simulation);

                float relDev = Math.Abs(record.Fitness - originalFitness) / Math.Max(Math.Abs(originalFitness), 1e-7f);

                Console.WriteLine("\n==================================================");
                Console.WriteLine(" RECHECK ERGEBNIS");
                Console.WriteLine("==================================================");
                Console.WriteLine($"Variante:             Gen{targetIter}_Var{targetVar}");
                Console.WriteLine($"Original-Fitness:     {originalFitness:F4} (Faktor: {context.Simulation.MeshRefinementFactor:0.00})");
                Console.WriteLine($"Recheck-Fitness:      {record.Fitness:F4} (Faktor: {fineFactor:0.00})");
                Console.WriteLine($"Fitness-Abweichung:   {relDev * 100:0.1} %");

                foreach (var m in originalMetrics.Keys)
                {
                    if (record.PassiveParameters.TryGetValue(m, out float rVal))
                    {
                        float origVal = originalMetrics[m];
                        float mDev = Math.Abs(rVal - origVal) / Math.Max(Math.Abs(origVal), 1e-7f);
                        Console.WriteLine($"   {m}: Original={origVal:F5}, Recheck={rVal:F5} (Abweichung: {mDev * 100:0.1} %)");
                    }
                }
                Console.WriteLine("==================================================\n");
            });
        }

        /// <summary>
        /// Listet auf, was startbar ist — und was daneben liegt, ohne es zu sein. Ein Ordner ohne
        /// Definition ist der häufigste Fall beim Anlegen eines neuen Projekts.
        /// </summary>
        private static void PrintProjects(ProjectRegistry registry, string? projectDirectoryArgument)
        {
            Console.WriteLine("Startbare Projekte:");

            if (registry.Definitions.Count == 0)
                Console.WriteLine("  (keine)");

            foreach (IProjectDefinition definition in registry.Definitions)
                Console.WriteLine($"  {definition.Name}   ({definition.GetType().Name})");

            var folders = ProjectPaths.ListProjectNames(projectDirectoryArgument);
            var orphans = folders
                .Where(folder => !registry.Names.Contains(folder, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (orphans.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("Ordner ohne Projektdefinition (nicht startbar):");
            foreach (string folder in orphans)
                Console.WriteLine($"  {folder}");
        }

        /// <summary>
        /// Gibt die wirksame Framework-Konfiguration als <c>SCHLÜSSEL=WERT</c> aus, eine Zeile je
        /// Eintrag. Bewusst dieses stumpfe Format und kein JSON: <c>sim-runner.sh</c> soll den
        /// Wert mit <c>grep</c> herausziehen können, ohne einen JSON-Parser zu brauchen.
        /// </summary>
        private static void PrintConfiguration(ProjectContext context)
        {
            foreach (PropertyInfo property in typeof(SimulationConfig)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                object? value = property.GetValue(context.Simulation);
                Console.WriteLine($"{property.Name}={Format(value)}");
            }
        }

        /// <summary>Kultur-unabhängig formatieren — sonst steht auf einem deutschen Rechner 0,5 statt 0.5.</summary>
        private static string Format(object? value) => value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
