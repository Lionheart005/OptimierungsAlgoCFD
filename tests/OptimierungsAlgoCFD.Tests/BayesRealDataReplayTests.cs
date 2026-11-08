using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.Naca2D;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Spielt echte Rechnungen des Serververgleichs vom 08.11.2026 (Naca2D, 10 × 10, Bayes) nach und prüft,
    /// was der Optimierer danach vorschlägt.
    ///
    /// <para>
    /// Anlass: Im Lauf rechnete Bayes nach sehr guten Ergebnissen ganze Runden Unsinn (Anstellwinkel 15 bis 30°,
    /// Dicke und Wölbung an den Rändern; das Modell selbst sagte dafür Fitness 0 bis 20 voraus). Ursache war die
    /// Ausweichregel „größte Unsicherheit im ganzen Raum", sobald die erwartete Verbesserung überall null war.
    /// </para>
    /// </summary>
    public class BayesRealDataReplayTests
    {
        private readonly ITestOutputHelper _output;

        public BayesRealDataReplayTests(ITestOutputHelper output) => _output = output;

        /// <summary>Alle Rechnungen bis zu einer Runde; der Optimierer bekommt sie Runde für Runde, wie im Lauf.</summary>
        private sealed record Replay(List<ModelRecord> Records, int ThroughIteration, Naca2DFitnessCalculator Fitness)
        {
            public double BestFitness => Records.Where(r => !r.SimulationFailed && !r.Disqualified).Max(r => r.Fitness);
        }

        private static Replay Load(string csv, int throughIteration)
        {
            ProjectConfig project = new Naca2DProject().CreateDefaults();
            var config = new SimulationConfig { VariantsPerIteration = 10, MaxIterations = 10 };
            var fitness = new Naca2DFitnessCalculator(project);

            string[] names = { "MaxCamber", "CamberPosition", "MaxThickness", "AngleOfAttackDeg" };
            var records = new List<ModelRecord>();

            foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] c = line.Split(';');
                int iteration = int.Parse(c[0], CultureInfo.InvariantCulture);
                if (iteration > throughIteration) continue;

                float F(int i) => float.Parse(c[i], CultureInfo.InvariantCulture);
                var record = new ModelRecord
                {
                    Iteration = iteration,
                    Variant = int.Parse(c[1], CultureInfo.InvariantCulture),
                    SimulationFailed = c[10] == "1",
                    Disqualified = c[11] == "1"
                };
                for (int i = 0; i < names.Length; i++) record.ActiveParameters[names[i]] = F(2 + i);
                record.PassiveParameters["Drag"] = F(6);
                record.PassiveParameters["Lift"] = F(7);
                record.PassiveParameters["Uncertainty_Drag"] = F(8);
                record.PassiveParameters["Uncertainty_Lift"] = F(9);
                fitness.CalculateFitness(record, config);
                records.Add(record);
            }

            return new Replay(records, throughIteration, fitness);
        }

        /// <summary>
        /// Spielt die Runden 1 bis N nach (der Optimierer lernt dabei wie im Lauf Runde für Runde weiter, seine
        /// eigenen Vorschläge dieser Runden werden verworfen) und liefert dann die Vorschläge für Runde N + 1
        /// samt der vom Modell dafür vorhergesagten Fitness.
        /// </summary>
        private static (List<double> Predicted, List<Dictionary<string, float>> Parameters, int Fallbacks) Propose(Replay replay, int optimizerSeed)
        {
            ProjectConfig project = new Naca2DProject().CreateDefaults();
            var config = new SimulationConfig { VariantsPerIteration = 10, MaxIterations = 10 };
            string dir = Path.Combine(Path.GetTempPath(), "bayes_replay_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var context = new SimulationContext(config, project, dir);

            var optimizer = new BayesOptimizationAlgorithm(replay.Fitness, random: new Random(optimizerSeed));
            optimizer.Preflight(context);

            for (int round = 1; round <= replay.ThroughIteration; round++)
            {
                List<ModelRecord> current = replay.Records.Where(r => r.Iteration == round).ToList();
                context.History.AddRange(current);
                ModelRecord best = context.History.Where(r => !r.SimulationFailed && !r.Disqualified).OrderByDescending(r => r.Fitness).First();
                optimizer.PrepareNextIteration(context, current, best);
            }

            int next = replay.ThroughIteration + 1;
            var proposals = new List<ModelRecord>();
            for (int v = 1; v <= 10; v++)
            {
                var record = new ModelRecord { Iteration = next, Variant = v };
                record.ActiveParameters = optimizer.GenerateParameters(context, next, v, 10);
                proposals.Add(record);
            }

            optimizer.EvaluateAndSelectBest(proposals, context);

            var predicted = proposals
                .Select(p => p.PassiveParameters.TryGetValue(BayesOptimizationAlgorithm.PredictedFitnessKey, out float f) ? (double)f : double.NaN)
                .ToList();
            return (predicted, proposals.Select(p => p.ActiveParameters).ToList(), optimizer.FallbackSelections);
        }

        public static IEnumerable<object[]> Situations()
        {
            // Vor genau diesen Runden schlug das Modell im Lauf Unsinn vor.
            yield return new object[] { BayesRealDataFixtures.S1, 5 };   // Seed 1, Runde 6
            yield return new object[] { BayesRealDataFixtures.S1, 7 };   // Seed 1, Runde 8
            yield return new object[] { BayesRealDataFixtures.S2, 8 };   // Seed 2, Runde 9
            yield return new object[] { BayesRealDataFixtures.S3, 6 };   // Seed 3, Runde 7
        }

        [Theory]
        [MemberData(nameof(Situations))]
        public void After_Good_Results_The_Next_Round_Is_Not_Filled_With_Nonsense(string csv, int throughIteration)
        {
            Replay replay = Load(csv, throughIteration);
            double best = replay.BestFitness;

            int garbageTotal = 0, fallbackTotal = 0;
            const int seeds = 4;
            for (int seed = 1; seed <= seeds; seed++)
            {
                var (predicted, parameters, fallbacks) = Propose(replay, seed);
                int garbage = predicted.Count(p => !(p >= 0.5 * best));
                garbageTotal += garbage;
                fallbackTotal += fallbacks;
                _output.WriteLine($"Seed {seed}: Ausweichregel {fallbacks}x | vorhergesagt: " + string.Join(" ", predicted.Select(p => p.ToString("0", CultureInfo.InvariantCulture))));
            }

            _output.WriteLine($"Beste bisher {best:0.0}; Unsinn-Vorschläge (vorhergesagt < 50 % des Besten): {garbageTotal} von {seeds * 10}; Ausweichregel {fallbackTotal}x");

            // Höchstens der Erkunder-Platz je Runde darf schlecht vorhergesagt sein.
            Assert.True(garbageTotal <= seeds * 2,
                $"{garbageTotal} von {seeds * 10} Vorschlägen liegen weit unter dem bisher Besten ({best:0.0}).");
        }
    }

    /// <summary>
    /// Gleiche Lage wie im Serverlauf, aber kontrollierbar: ein Naca2D-ähnliches Problem mit Drag und Lift
    /// (Modus „Metrics"), dessen Optimum an einer Parametergrenze liegt und das keine Streuung hat. Nach einigen
    /// Runden ist nirgends mehr eine Verbesserung zu erwarten. Bis zum 08.11.2026 wich die Auswahl dann auf
    /// „größte Unsicherheit im ganzen Raum" aus und verbrauchte ganze Runden an den Rändern.
    /// </summary>
    public class BayesConvergedPhaseTests : TempDirectoryTest
    {
        private readonly ITestOutputHelper _output;

        public BayesConvergedPhaseTests(ITestOutputHelper output) => _output = output;

        private static BayesHarness.Problem NacaLike() => new()
        {
            Names = new[] { "MaxCamber", "CamberPosition", "MaxThickness", "AngleOfAttackDeg" },
            Bounds = new[] { (0f, 8f), (15f, 75f), (6f, 24f), (-10f, 30f) },
            Start = new[] { 0f, 40f, 12f, 0f },
            Solve = r =>
            {
                float c = r.ActiveParameters["MaxCamber"], p = r.ActiveParameters["CamberPosition"];
                float t = r.ActiveParameters["MaxThickness"], a = r.ActiveParameters["AngleOfAttackDeg"];

                float lift = 0.11f * a + 0.12f * c;
                if (a > 12f) lift *= MathF.Exp(-0.35f * (a - 12f));            // Strömungsabriss
                float drag = 0.006f + 0.0004f * t + 0.0000005f * (p - 40f) * (p - 40f) + 0.0006f * a * a
                             + (a > 12f ? 0.02f * (a - 12f) : 0f);

                r.PassiveParameters["Drag"] = drag;
                r.PassiveParameters["Lift"] = lift;
            },
            Fitness = () => new Naca2DFitnessCalculator(new ProjectConfig { ProjectName = "NacaLike" }),
            Iterations = 10,
            Variants = 10
        };

        [Fact]
        public void When_Nothing_Can_Improve_Any_More_The_Rounds_Stay_Near_The_Best()
        {
            int total = 0, near = 0, fallbacks = 0;

            for (int seed = 1; seed <= 4; seed++)
            {
                var (context, optimizer) = BayesHarness.Run(NacaLike(), "Bayes", seed, Sub("s" + seed));
                fallbacks += ((BayesOptimizationAlgorithm)optimizer).FallbackSelections;

                double best = 0.0;
                foreach (var round in context.History.Where(r => r.Variant != 99).GroupBy(r => r.Iteration).OrderBy(g => g.Key))
                {
                    if (round.Key >= 5)
                    {
                        foreach (ModelRecord r in round)
                        {
                            total++;
                            if (!r.SimulationFailed && r.Fitness >= 0.5 * best) near++;
                        }
                    }
                    best = Math.Max(best, round.Where(r => !r.SimulationFailed).Max(r => r.Fitness));
                }
            }

            double share = (double)near / total;
            _output.WriteLine($"Anteil der Varianten ab Runde 5 mit mindestens 50 % des bisher Besten: {share:P0} ({near} von {total}); Ausweichregel {fallbacks}x");
            Assert.True(share >= 0.7, $"Nur {share:P0} der späten Varianten liegen in der Nähe des Besten.");
        }
    }
}
