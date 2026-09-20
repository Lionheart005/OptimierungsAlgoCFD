using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using MyPicoGkProject.Core;

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

        /// <summary>
        /// Aufruf: <c>&lt;Projektname&gt; [Projektverzeichnis] [--list-projects] [--print-config]</c>
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

                var positional = args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
                                     .ToArray();

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

                RunOptimization(definition, context);
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

        /// <summary>Verdrahtet die Bausteine des Projekts und startet den Lauf im Geometrie-Kernel.</summary>
        private static void RunOptimization(IProjectDefinition definition, ProjectContext context)
        {
            Console.WriteLine($"[SYSTEM] Starte Projekt: {context.Name}");

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

            string effectiveConfig = EffectiveConfigWriter.Write(simulationContext.WorkingDirectory, context);

            // Der Bouncer ist Framework-Bestandteil und prüft die Fitness, nicht eine
            // projektspezifische Metrik (Entscheidung 6).
            IModelValidator validator = new ChampionValidator(fitness);

            // Optimierungsverfahren: steht als "OptimizationAlgorithm" in der simulation.json.
            IOptimizationAlgorithm optimizer =
                OptimizationAlgorithmFactory.Create(context.Simulation.OptimizationAlgorithm, fitness);

            // Die Pflicht-Metriken gehen als reine Namensliste hinein, nicht als Rechner:
            // der Controller bewertet nichts selbst (TODO-13).
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics);

            Console.WriteLine("==================================================");
            Console.WriteLine("  AUTOMATED CFD OPTIMIZATION FRAMEWORK");
            Console.WriteLine($"  Projekt:          {context.Name}");
            Console.WriteLine($"  Geometrie-Kernel: {kernel.Name}");
            Console.WriteLine($"  Ergebnisse:       {JsonConfigLoader.Label(simulationContext.WorkingDirectory)}");
            Console.WriteLine($"  Konfiguration:    {JsonConfigLoader.Label(effectiveConfig)}");
            Console.WriteLine("==================================================");

            kernel.RunHosted(context.Simulation.BaseVoxelResolution, controller.RunOptimization);
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
