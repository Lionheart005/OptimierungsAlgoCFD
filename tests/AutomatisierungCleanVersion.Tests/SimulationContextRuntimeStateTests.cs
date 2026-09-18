using System.Collections.Generic;
using Moq;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
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
        /// Der EA schreibt den Gewinner als neuen Arbeitspunkt fort und passt Sigma an.
        /// Beides darf nur im Context landen.
        /// </summary>
        [Fact]
        public void EvolutionaryAlgorithm_Does_Not_Mutate_The_ProjectConfig()
        {
            var project = Project();
            var context = new SimulationContext(new SimulationConfig(), project);
            var algorithm = new EvolutionaryAlgorithm(Fitness());

            var records = new List<ModelRecord>
            {
                new ModelRecord
                {
                    Variant = 1,
                    Fitness = 1f,
                    ActiveParameters = new Dictionary<string, float> { { "Length", 50f }, { "Width", 40f } }
                },
                new ModelRecord
                {
                    Variant = 2,
                    Fitness = 100f,
                    ActiveParameters = new Dictionary<string, float> { { "Length", 61f }, { "Width", 44f } }
                }
            };

            algorithm.PrepareNextIteration(context, records, records[1]);

            // Laufzeitzustand ist fortgeschrieben ...
            Assert.Equal(61f, context.CurrentBaseParameters["Length"]);
            // ... mit Erfolgsquote 1/1 > 0.2 also Sigma * 1.2
            Assert.Equal(6f, context.CurrentDeviations["Length"], 3);

            // ... die Konfiguration nicht
            Assert.Equal(50f, project.BaseParameters["Length"]);
            Assert.Equal(40f, project.BaseParameters["Width"]);
            Assert.Equal(5f, project.MaxDeviations["Length"]);
        }

        /// <summary>
        /// Der eigentliche Punkt von TODO-17: ein zweiter Lauf mit derselben (geladenen)
        /// Konfiguration startet wieder bei den Vorgabewerten.
        /// </summary>
        [Fact]
        public void Second_Run_With_The_Same_Config_Starts_At_The_Defaults_Again()
        {
            var project = Project();
            var algorithm = new EvolutionaryAlgorithm(Fitness());

            var firstRun = new SimulationContext(new SimulationConfig(), project);
            algorithm.PrepareNextIteration(
                firstRun,
                new List<ModelRecord> { new ModelRecord { Variant = 1, Fitness = 1f } },
                new ModelRecord
                {
                    ActiveParameters = new Dictionary<string, float> { { "Length", 88f } }
                });

            Assert.Equal(88f, firstRun.CurrentBaseParameters["Length"]);

            var secondRun = new SimulationContext(new SimulationConfig(), project);

            Assert.Equal(50f, secondRun.CurrentBaseParameters["Length"]);
            Assert.Equal(5f, secondRun.CurrentDeviations["Length"]);
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

        /// <summary>
        /// Auch der RSM sampelt um den Laufzeit-Arbeitspunkt und fasst die Konfiguration nicht an.
        /// </summary>
        [Fact]
        public void RsmAlgorithm_Reads_The_Runtime_Base_Parameters()
        {
            var project = Project();
            var context = new SimulationContext(new SimulationConfig(), project);
            context.CurrentBaseParameters["Length"] = 77f;

            var algorithm = new RsmOptimizationAlgorithm(Fitness());

            var first = algorithm.GenerateParameters(context, iteration: 1, variant: 1, maxVariants: 10);

            Assert.Equal(77f, first["Length"]);
            Assert.Equal(50f, project.BaseParameters["Length"]);
        }
    }
}
