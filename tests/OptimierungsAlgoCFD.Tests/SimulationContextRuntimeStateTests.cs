using System.Collections.Generic;
using Moq;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.MantaAuv;
using OptimierungsAlgoCFD.Solvers.Cfd;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// TODO-17: Arbeitspunkt und Mutationsstärke sind Laufzeitzustand und gehören in den
    /// <see cref="SimulationContext"/>. Die <see cref="ProjectConfig"/> kommt aus JSON und
    /// darf während des Laufs nicht verändert werden — sonst startet ein zweiter Lauf im
    /// selben Prozess mit den Endwerten des ersten.
    /// </summary>
    public class SimulationContextRuntimeStateTests
    {
        private static ProjectConfig Project() => new ProjectConfig
        {
            ProjectName = "Test",
            BaseParameters = new Dictionary<string, float> { { "Length", 50f }, { "Width", 40f } },
            MaxDeviations = new Dictionary<string, float> { { "Length", 5f }, { "Width", 4f } },
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Length", (30f, 100f) },
                { "Width", (20f, 80f) }
            }
        };

        private static IFitnessCalculator Fitness() => new Mock<IFitnessCalculator>().Object;

        [Fact]
        public void Context_Starts_As_A_Copy_Of_The_Project_Defaults()
        {
            var project = Project();

            var context = new SimulationContext(new SimulationConfig(), project);

            Assert.Equal(project.BaseParameters, context.CurrentBaseParameters);
            Assert.Equal(project.MaxDeviations, context.CurrentDeviations);

            // Kopie, keine geteilte Referenz
            context.CurrentBaseParameters["Length"] = 99f;
            Assert.Equal(50f, project.BaseParameters["Length"]);
        }

        /// <summary>
        /// Auch der Bayes-Optimierer und die IDW-Baseline fassen die ProjectConfig nicht an.
        /// </summary>
        [Fact]
        public void BayesAlgorithm_Does_Not_Mutate_The_ProjectConfig()
        {
            var project = Project();
            var context = new SimulationContext(new SimulationConfig(), project);
            var fitness = new Mock<IFitnessCalculator>();
            fitness.SetupGet(f => f.RequiredMetrics).Returns(System.Array.Empty<string>());
            var algorithm = new BayesOptimizationAlgorithm(fitness.Object, random: new System.Random(1));

            var first = algorithm.GenerateParameters(context, iteration: 1, variant: 1, maxVariants: 4);

            Assert.Equal(50f, first["Length"]);
            Assert.Equal(50f, project.BaseParameters["Length"]);
            Assert.Equal(40f, project.BaseParameters["Width"]);
            Assert.Equal(5f, project.MaxDeviations["Length"]);
        }

        [Fact]
        public void ResetRuntimeState_Restores_The_Project_Defaults()
        {
            var context = new SimulationContext(new SimulationConfig(), Project());
            context.CurrentBaseParameters["Length"] = 12f;
            context.CurrentDeviations["Length"] = 0.5f;

            context.ResetRuntimeState();

            Assert.Equal(50f, context.CurrentBaseParameters["Length"]);
            Assert.Equal(5f, context.CurrentDeviations["Length"]);
        }

        [Fact]
        public void EffectiveMeshRefinementFactor_Initializes_From_Config_And_Resets()
        {
            var config = new SimulationConfig { MeshRefinementFactor = 0.5f };
            var context = new SimulationContext(config, Project());

            Assert.Equal(0.5f, context.EffectiveMeshRefinementFactor);
            Assert.Equal("", context.CurrentRunTag);

            context.EffectiveMeshRefinementFactor = 0.25f;
            context.CurrentRunTag = "Kal1";

            context.ResetRuntimeState();

            Assert.Equal(0.5f, context.EffectiveMeshRefinementFactor);
            Assert.Equal("", context.CurrentRunTag);
        }

        /// <summary>
        /// Auch die IDW-Baseline sampelt um den Laufzeit-Arbeitspunkt und fasst die Konfiguration nicht an.
        /// </summary>
        [Fact]
        public void IdwBaselineAlgorithm_Reads_The_Runtime_Base_Parameters()
        {
            var project = Project();
            var context = new SimulationContext(new SimulationConfig(), project);
            context.CurrentBaseParameters["Length"] = 77f;

            var algorithm = new IdwBaselineAlgorithm(Fitness());

            var first = algorithm.GenerateParameters(context, iteration: 1, variant: 1, maxVariants: 10);

            Assert.Equal(77f, first["Length"]);
            Assert.Equal(50f, project.BaseParameters["Length"]);
        }
    }
}
