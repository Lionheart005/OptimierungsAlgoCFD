using System;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Cli
{
    /// <summary>
    /// Handler for automatic and standalone mesh calibration (<c>--calibrate</c>).
    /// </summary>
    internal static class CalibrationCommand
    {
        /// <summary>
        /// Runs standalone mesh calibration on the baseline geometry (--calibrate, TODO-42 / TODO-48).
        /// </summary>
        public static void RunCalibration(IProjectDefinition definition, ProjectContext context)
        {
            Console.WriteLine($"{OutputConventions.SystemTag} Starting calibration for project: {context.Name}");

            MeshCalibration.ValidatePrecalibrationSupport(context.Simulation, definition);

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();
            SolverStage[] stages = definition.CreateStages(context);
            MeshCalibration.ValidateYPlusConsistency(context, calibrationActive: true);

            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            EffectiveConfigWriter.Write(simulationContext.WorkingDirectory, context, simulationContext);

            int seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed), context.Project.ParameterBounds);
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics);

            float effectiveVoxelResolution = VoxelResolutionAdvisor.Resolve(
                context.Simulation, context.Project, stages);

            kernel.RunHosted(effectiveVoxelResolution, () =>
            {
                var (decision, _) = ExecuteCalibration(
                    definition, context, simulationContext, controller, fitness, validator, optimizer);

                if (decision.Success && decision.SelectedStage != null)
                {
                    Console.WriteLine($"\nRecommended entry for src/projects/{context.Name}/simulation.json:");
                    Console.WriteLine($"  \"MeshRefinementFactor\": {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            });
        }

        /// <summary>
        /// Returns 2 for a 2D project (<c>Dimensions</c> = 2 in <c>su2.json</c>), otherwise 3.
        /// Reads only what <c>CreateStages</c> has already loaded and never loads anything twice.
        /// </summary>
        public static int ResolveMeshDimensions(ProjectContext context)
        {
            Su2SolverOptions? su2 = context.Loaded.Select(l => l.Value).OfType<Su2SolverOptions>().LastOrDefault();
            return su2 != null && su2.Dimensions == 2 ? 2 : 3;
        }

        /// <summary>
        /// Resolves the 2D mesher options for 2D precalibration:
        /// loads <c>solvers/gmsh2d.json</c> if present; otherwise copies compatible defaults
        /// from already loaded <see cref="GmshMesherOptions"/>.
        /// </summary>
        public static Gmsh2DMesherOptions ResolvePrecalibrationGmsh2DOptions(ProjectContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.ProjectDirectory))
            {
                string gmsh2dPath = Path.Combine(context.ProjectDirectory, "solvers", "gmsh2d.json");
                if (File.Exists(gmsh2dPath))
                {
                    return context.LoadSolverOptions<Gmsh2DMesherOptions>("gmsh2d");
                }
            }

            var options = new Gmsh2DMesherOptions();
            GmshMesherOptions? gmsh3d = context.Loaded.Select(l => l.Value).OfType<GmshMesherOptions>().LastOrDefault();
            if (gmsh3d != null)
            {
                options.TargetYPlus = gmsh3d.TargetYPlus;
                options.BoundaryLayerGrowthRate = gmsh3d.BoundaryLayerGrowthRate;
                options.DetailBoxLengthFactor = gmsh3d.DetailBoxLengthFactor;
            }
            return options;
        }

        public static (CalibrationDecision Decision, string ReportRelativePath) ExecuteCalibration(
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
                var gmsh2D = ResolvePrecalibrationGmsh2DOptions(context);
                var su2Options2D = context.LoadSolverOptions<Su2SolverOptions>("su2");
                su2Options2D.Dimensions = 2;
                // The 2D cross-section uses chord reference length rather than the 3D frontal area
                // (otherwise test project coefficients were ~8x too large, server test 06.11.2026).
                su2Options2D.ReferenceAreaMetric = "Chord";
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
                    journal);   // same instance as the 3D controller (TODO-51)

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
                    runStage3D,
                    durationProvider2D: () => controller2D.LastCallDurationSeconds,
                    durationProvider3D: () => controller3D.LastCallDurationSeconds);
            }

            // Dimension from the project's su2.json: 2D projects (e.g., Naca2D) scale cell counts with factor^2.
            int dimensions = context.LoadSolverOptions<Su2SolverOptions>("su2").Dimensions == 2 ? 2 : 3;
            return MeshCalibration.Run(
                context,
                simulationContext,
                runStage3D,
                dimensions: dimensions,
                durationProvider: () => controller3D.LastCallDurationSeconds);
        }
    }
}
