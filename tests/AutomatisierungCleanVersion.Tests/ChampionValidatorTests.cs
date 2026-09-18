using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-19 zu TODO-3: der Bouncer prüft die Fitness (nicht mehr "Drag") und ist
    /// Framework-Bestandteil. Geprüft werden Bestätigung, Disqualifikation, der Fallback
    /// wenn kein Kandidat stabil ist, und dass nie mehr als nötig re-simuliert wird.
    /// </summary>
    public class ChampionValidatorTests
    {
        private static ModelRecord Candidate(int variant, float fitness) => new ModelRecord
        {
            Iteration = 1,
            Variant = variant,
            Fitness = fitness,
            ActiveParameters = new Dictionary<string, float> { { "Length", 50f + variant } }
        };

        /// <summary>
        /// Fitness-Rechner, der jedem Re-Simulat einen festen Wert gibt. Der Validator
        /// ruft ihn für das Ergebnis der Re-Simulation auf — nicht für die Kandidaten.
        /// </summary>
        private static IFitnessCalculator FitnessReturning(float value)
        {
            var mock = new Mock<IFitnessCalculator>();
            mock.Setup(f => f.CalculateFitness(It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>()))
                .Callback<ModelRecord, SimulationConfig>((r, c) => r.Fitness = value);
            return mock.Object;
        }

        private static SimulationConfig Config(float tolerance = 0.2f) => new SimulationConfig
        {
            BouncerTolerance = tolerance,
            BouncerJitter = 0.01f
        };

        [Fact]
        public void Stable_Candidate_Is_Confirmed()
        {
            // Re-Simulation liefert 9.5 gegen Referenz 10.0 -> 5% Abweichung, innerhalb 20%
            var validator = new ChampionValidator(FitnessReturning(9.5f), new Random(1));
            var candidates = new List<ModelRecord> { Candidate(1, 8f), Candidate(2, 10f) };

            var winner = validator.ValidateChampion(candidates, Config(), _ => new ModelRecord());

            Assert.Equal(2, winner.Variant);
            Assert.Equal(10f, winner.Fitness);   // unverändert, nicht disqualifiziert
        }

        /// <summary>
        /// Weicht die Fitness zu stark ab, wird der Kandidat disqualifiziert (Fitness 0.0001)
        /// und der nächstbeste geprüft.
        /// </summary>
        [Fact]
        public void Unstable_Candidate_Is_Disqualified_And_The_Next_One_Is_Tested()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 10f);

            // Re-Simulat ist immer 10.0: fuer den 100er 90% Abweichung (raus),
            // fuer den 10er 0% (bestaetigt).
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { best, second }, Config(), _ => new ModelRecord());

            Assert.Equal(2, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);   // disqualifiziert
        }

        /// <summary>
        /// Ist kein Kandidat stabil, bricht der Lauf nicht ab — es gewinnt der Beste der Reste.
        /// </summary>
        [Fact]
        public void Falls_Back_To_The_Best_Of_The_Rest_When_Nothing_Is_Stable()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 50f);
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { second, best }, Config(), _ => new ModelRecord());

            // sortiert wird absteigend nach Fitness -> der 100er stand vorne
            Assert.Equal(1, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);
            Assert.Equal(0.0001f, second.Fitness, 6);
        }

        /// <summary>Bricht die Re-Simulation ab, gilt der Kandidat als instabil — keine Exception nach außen.</summary>
        [Fact]
        public void Failing_Resimulation_Disqualifies_Instead_Of_Throwing()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 10f);
            int calls = 0;

            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { best, second },
                Config(),
                _ =>
                {
                    calls++;
                    if (calls == 1) throw new InvalidOperationException("Solver abgestürzt");
                    return new ModelRecord();
                });

            Assert.Equal(2, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);
        }

        /// <summary>
        /// Kandidaten unter der Prüfschwelle (0.1) werden nicht re-simuliert — sonst würde
        /// das Framework Rechenzeit in offensichtlich unbrauchbare Modelle stecken.
        /// </summary>
        [Fact]
        public void Candidates_Below_The_Test_Threshold_Are_Not_Resimulated()
        {
            int calls = 0;
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { Candidate(1, 0.05f), Candidate(2, 0.0001f) },
                Config(),
                _ => { calls++; return new ModelRecord(); });

            Assert.Equal(0, calls);
            Assert.Equal(1, winner.Variant);   // Bester der Reste
        }

        /// <summary>Der bestätigte Champion kostet genau eine Re-Simulation.</summary>
        [Fact]
        public void Confirmed_Champion_Costs_Exactly_One_Resimulation()
        {
            int calls = 0;
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            validator.ValidateChampion(
                new List<ModelRecord> { Candidate(1, 10f), Candidate(2, 9f) },
                Config(),
                _ => { calls++; return new ModelRecord(); });

            Assert.Equal(1, calls);
        }

        /// <summary>Der Jitter verstellt genau einen Parameter um genau den konfigurierten Betrag.</summary>
        [Fact]
        public void Jitter_Changes_Exactly_One_Parameter_By_The_Configured_Amount()
        {
            var candidate = new ModelRecord
            {
                Variant = 1,
                Fitness = 10f,
                ActiveParameters = new Dictionary<string, float>
                {
                    { "Length", 50f }, { "Width", 40f }, { "TailTaper", 1f }
                }
            };

            Dictionary<string, float>? seen = null;
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(42));

            validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                Config(),
                p => { seen = p; return new ModelRecord(); });

            Assert.NotNull(seen);
            var changed = seen!.Where(kvp => candidate.ActiveParameters[kvp.Key] != kvp.Value).ToList();
            Assert.Single(changed);
            Assert.Equal(0.01f, Math.Abs(changed[0].Value - candidate.ActiveParameters[changed[0].Key]), 5);

            // Der Kandidat selbst bleibt unangetastet — der Jitter läuft auf einer Kopie.
            Assert.Equal(50f, candidate.ActiveParameters["Length"]);
        }

        [Fact]
        public void Empty_Candidate_List_Throws()
        {
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            Assert.Throws<InvalidOperationException>(
                () => validator.ValidateChampion(new List<ModelRecord>(), Config(), _ => new ModelRecord()));
        }
    }
}
