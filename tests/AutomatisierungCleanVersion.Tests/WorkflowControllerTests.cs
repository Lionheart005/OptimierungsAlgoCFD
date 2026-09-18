using System.Collections.Generic;
using MyPicoGkProject;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-15: der "ABSOLUTE CHAMPION" am Ende des Laufs muss der beste Record der
    /// gesamten History sein — vorher war es der Gewinner der letzten Iteration.
    /// </summary>
    public class WorkflowControllerChampionTests
    {
        private static ModelRecord Record(int iteration, int variant, float fitness) => new ModelRecord
        {
            Iteration = iteration,
            Variant = variant,
            Fitness = fitness
        };

        [Fact]
        public void Picks_Best_Fitness_Not_Last_Entry()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 5.0f),
                Record(1, 2, 42.0f),   // bester Lauf, liegt aber nicht am Ende
                Record(2, 1, 7.0f),
                Record(2, 2, 9.0f)     // Gewinner der letzten Iteration
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.NotNull(best);
            Assert.Equal(1, best!.Iteration);
            Assert.Equal(2, best.Variant);
            Assert.Equal(42.0f, best.Fitness);
        }

        /// <summary>
        /// Disqualifizierte (Bouncer) und fehlgeschlagene Modelle tragen eine sehr kleine
        /// Fitness — sie dürfen nie gewinnen, solange ein gültiges Modell existiert.
        /// </summary>
        [Fact]
        public void Ignores_Disqualified_And_Failed_Records()
        {
            var failed = Record(1, 1, 0.0001f);
            failed.SimulationFailed = true;

            var history = new List<ModelRecord>
            {
                failed,
                Record(1, 2, 0.0001f),  // vom ChampionValidator disqualifiziert
                Record(1, 3, 1.5f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(3, best!.Variant);
        }

        /// <summary>Bei Gleichstand gewinnt der frühere Eintrag — sonst wäre die Auswahl zufällig.</summary>
        [Fact]
        public void Ties_Go_To_The_Earlier_Record()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 3.0f),
                Record(2, 1, 3.0f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(1, best!.Iteration);
        }

        [Fact]
        public void Empty_History_Yields_Null()
        {
            Assert.Null(WorkflowController.SelectBestRecord(new List<ModelRecord>()));
        }
    }
}
