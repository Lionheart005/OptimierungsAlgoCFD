using System;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge
{
    /// <summary>
    /// Composition root. Resolves the project name, asks <see cref="ProjectRegistry"/> to locate
    /// the matching definition, loads the configuration, and delegates to the appropriate CLI
    /// handler in <see cref="FluidForge.Cli"/>.
    ///
    /// <para>
    /// Since TODO-23 there is <b>no</b> <c>switch</c> over project names here — a new project is
    /// simply a folder with an <see cref="IProjectDefinition"/>, and this file never needs to be
    /// touched for it again.
    /// </para>
    /// </summary>
    class Program
    {
        /// <summary>
        /// Usage: <c>&lt;ProjectName&gt; [ProjectDirectory] [--list-projects] [--print-config]
        /// [--smoke-test] [--calibrate] [--recheck[=Variant]] [--resume]</c>
        /// </summary>
        /// <returns>0 on success, 1 on failure.</returns>
        static int Main(string[] args)
        {
            try
            {
                CliArguments cli = CliArguments.Parse(args);
                var registry = ProjectRegistry.Discover();

                // Project-independent informational commands.
                if (cli.ListProjects)
                {
                    InfoCommands.PrintProjects(registry, cli.ProjectDirectoryArgument);
                    return 0;
                }

                if (cli.DumpPreset)
                {
                    InfoCommands.DumpPreset(cli.DumpPresetName, cli.OutDirectory);
                    return 0;
                }

                if (cli.ExportSchemas)
                {
                    InfoCommands.ExportSchemas(cli.ExportSchemasDirectory);
                    return 0;
                }

                string projectName = cli.ResolveProjectName(registry);
                IProjectDefinition definition = registry.Resolve(projectName);

                // Cross-check folders against definitions in both directions so mismatches never
                // fail silently. Skipped when src/projects/ is absent (TODO-24).
                registry.AssertFoldersMatchDefinitions(cli.ProjectDirectoryArgument);

                ProjectContext context = OptimizeCommand.LoadConfiguration(definition, cli.ProjectDirectoryArgument);

                // --print-config prints the effective configuration and exits without starting a run.
                if (cli.PrintConfig)
                {
                    try
                    {
                        definition.CreateStages(context);
                    }
                    catch
                    {
                        // Best-effort: surface solver options even if a stage requires external runtime tools.
                    }

                    InfoCommands.PrintConfiguration(context);
                    return 0;
                }

                if (cli.SmokeTest)
                {
                    return SmokeTestCommand.Run(definition, context) ? 0 : 1;
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
