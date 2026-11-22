using System;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge
{
    /// <summary>
    /// Composition Root. Bestimmt den Projektnamen, lässt die <see cref="ProjectRegistry"/> die
    /// passende Definition heraussuchen, lädt die Konfiguration und delegiert an den passenden
    /// CLI-Handler in <see cref="FluidForge.Cli"/>.
    ///
    /// <para>
    /// Seit TODO-23 steht hier <b>kein</b> <c>switch</c> über Projektnamen mehr — ein neues
    /// Projekt ist ein Ordner mit einer <see cref="IProjectDefinition"/>, und diese Datei wird
    /// dafür nie wieder angefasst.
    /// </para>
    /// </summary>
    class Program
    {
        /// <summary>
        /// Aufruf: <c>&lt;Projektname&gt; [Projektverzeichnis] [--list-projects] [--print-config]
        /// [--calibrate] [--recheck[=Variante]] [--resume]</c>
        /// </summary>
        /// <returns>0 bei Erfolg, 1 bei Abbruch.</returns>
        static int Main(string[] args)
        {
            try
            {
                CliArguments cli = CliArguments.Parse(args);
                var registry = ProjectRegistry.Discover();

                // --list-projects braucht kein Projekt und keinen Kernel.
                if (cli.ListProjects)
                {
                    InfoCommands.PrintProjects(registry, cli.ProjectDirectoryArgument);
                    return 0;
                }

                string projectName = cli.ResolveProjectName(registry);
                IProjectDefinition definition = registry.Resolve(projectName);

                // Ordner gegen Definitionen abgleichen — beide Richtungen, beide Fehler wären
                // sonst still. Ohne vorhandenes src/projects/ entfällt die Prüfung (TODO-24).
                registry.AssertFoldersMatchDefinitions(cli.ProjectDirectoryArgument);

                ProjectContext context = OptimizeCommand.LoadConfiguration(definition, cli.ProjectDirectoryArgument);

                // --print-config gibt die wirksame Konfiguration aus und startet nichts.
                if (cli.PrintConfig)
                {
                    InfoCommands.PrintConfiguration(context);
                    return 0;
                }

                if (cli.Calibrate)
                {
                    CalibrationCommand.RunCalibration(definition, context);
                    return 0;
                }

                if (cli.Recheck)
                {
                    RecheckCommand.RunRecheck(definition, context, cli.RecheckVariant, cli.RecheckFiner, cli.Unsteady);
                    return 0;
                }

                OptimizeCommand.RunOptimization(definition, context, args, resume: cli.Resume);
                return 0;
            }
            catch (Exception e)
            {
                OptimizeCommand.Report(e);
                return 1;
            }
        }

        internal static Gmsh2DMesherOptions ResolvePrecalibrationGmsh2DOptions(ProjectContext context)
            => CalibrationCommand.ResolvePrecalibrationGmsh2DOptions(context);

        internal static void PrepareUnsteadyRestart(string workingDirectory)
            => RecheckCommand.PrepareUnsteadyRestart(workingDirectory);

        internal static void CleanUnsteadyRestartFiles(string workingDirectory)
            => RecheckCommand.CleanUnsteadyRestartFiles(workingDirectory);
    }
}
