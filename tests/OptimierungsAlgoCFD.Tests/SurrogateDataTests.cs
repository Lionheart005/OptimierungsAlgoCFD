using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OptimierungsAlgoCFD.Core;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// TODO-53: Parameterraum und die zentrale Regel für Trainingsdaten (F13, F14).
    /// </summary>
    public class SurrogateDataTests : TempDirectoryTest
    {
        private static SimulationContext Context(string directory, Dictionary<string, (float Min, float Max)>? bounds = null)
        {
            var project = new ProjectConfig
            {
                ProjectName = "Raum",
                BaseParameters = new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 20f, ["Fix"] = 3f },
                MaxDeviations = new Dictionary<string, float> { ["Length"] = 10f, ["Width"] = 2f, ["Fix"] = 0f },
                ParameterBounds = bounds ?? new Dictionary<string, (float Min, float Max)>
                {
                    ["Length"] = (50f, 150f),
                    ["Width"] = (10f, 30f),
                    ["Fix"] = (3f, 3f)
                }
            };
            return new SimulationContext(new SimulationConfig(), project, directory);
        }

        private static ModelRecord Record(int variant, float length, float width, float fitness = 1f)
        {
            var record = new ModelRecord { Iteration = 1, Variant = variant, Fitness = fitness };
            record.ActiveParameters["Length"] = length;
            record.ActiveParameters["Width"] = width;
            record.ActiveParameters["Fix"] = 3f;
            return record;
        }

        // ---------------------------------------------------------------- ParameterSpace

        [Fact]
        public void The_Space_Normalises_Free_Parameters_And_Fills_In_Fixed_Ones()
        {
            var space = ParameterSpace.Create(Context(Root));

            Assert.Equal(new[] { "Length", "Width" }, space.FreeNames);
            Assert.Equal(2, space.Dimension);
            Assert.Equal(3f, space.FixedValues["Fix"]);

            double[] unit = space.ToUnit(new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 10f, ["Fix"] = 3f });
            Assert.Equal(0.5, unit[0], 9);
            Assert.Equal(0.0, unit[1], 9);

            var back = space.FromUnit(new[] { 0.25, 1.0 });
            Assert.Equal(75f, back["Length"], 3);
            Assert.Equal(30f, back["Width"], 3);
            Assert.Equal(3f, back["Fix"]);
            Assert.Equal(new[] { "Length", "Width", "Fix" }, back.Keys);   // Reihenfolge des Projekts
        }

        [Fact]
        public void The_Round_Trip_Survives()
        {
            var space = ParameterSpace.Create(Context(Root));
            var original = new Dictionary<string, float> { ["Length"] = 87.25f, ["Width"] = 14.5f, ["Fix"] = 3f };

            var roundTrip = space.FromUnit(space.ToUnit(original));

            Assert.Equal(87.25f, roundTrip["Length"], 3);
            Assert.Equal(14.5f, roundTrip["Width"], 3);
        }

        [Fact]
        public void Values_Outside_The_Bounds_Are_Clamped()
        {
            var space = ParameterSpace.Create(Context(Root));

            var back = space.FromUnit(new[] { -0.5, 2.0 });
            Assert.Equal(50f, back["Length"]);
            Assert.Equal(30f, back["Width"]);

            double[] unit = space.ToUnit(new Dictionary<string, float> { ["Length"] = 500f, ["Width"] = -9f });
            Assert.Equal(new[] { 1.0, 0.0 }, unit);
        }

        [Fact]
        public void A_Missing_Bound_Fails_And_Names_The_Parameter_And_The_File()
        {
            var bounds = new Dictionary<string, (float Min, float Max)> { ["Length"] = (50f, 150f), ["Fix"] = (3f, 3f) };

            var ex = Assert.Throws<InvalidOperationException>(() => ParameterSpace.Create(Context(Root, bounds)));

            Assert.Contains("Width", ex.Message);
            Assert.Contains("src/projects/Raum/project.json", ex.Message);
            Assert.Contains("ParameterBounds", ex.Message);
        }

        [Fact]
        public void Min_Greater_Than_Max_Fails()
        {
            var bounds = new Dictionary<string, (float Min, float Max)>
            {
                ["Length"] = (150f, 50f), ["Width"] = (10f, 30f), ["Fix"] = (3f, 3f)
            };

            var ex = Assert.Throws<InvalidOperationException>(() => ParameterSpace.Create(Context(Root, bounds)));
            Assert.Contains("Length", ex.Message);
        }

        // ---------------------------------------------------------------- Trainingsregel

        private static (SimulationContext Context, ParameterSpace Space) WithHistory(string directory, params ModelRecord[] records)
        {
            SimulationContext context = Context(directory);
            context.History.AddRange(records);
            return (context, ParameterSpace.Create(context));
        }

        [Fact]
        public void A_Plain_Record_Is_Usable()
        {
            var (context, space) = WithHistory(Root, Record(1, 100f, 20f));

            var set = SurrogateTrainingSet.Build(context, space);

            Assert.Single(set.Records);
            Assert.Single(set.Inputs);
            Assert.Empty(set.ExcludedPoints);
        }

        [Fact]
        public void Failed_Records_Are_Excluded_But_Reported_For_The_Repulsion()
        {
            var failed = Record(2, 60f, 12f);
            failed.SimulationFailed = true;
            var (context, space) = WithHistory(Root, Record(1, 100f, 20f), failed);

            var set = SurrogateTrainingSet.Build(context, space);

            Assert.Single(set.Records);
            var excluded = Assert.Single(set.ExcludedPoints);
            Assert.Equal(0.1, excluded[0], 9);
        }

        [Fact]
        public void Disqualified_Records_Are_Excluded_But_Reported_For_The_Repulsion()
        {
            var bounced = Record(2, 140f, 28f, fitness: 0.0001f);
            bounced.Disqualified = true;
            var (context, space) = WithHistory(Root, Record(1, 100f, 20f), bounced);

            var set = SurrogateTrainingSet.Build(context, space);

            Assert.Single(set.Records);
            Assert.Single(set.ExcludedPoints);
        }

        [Fact]
        public void Non_Finite_Fitness_Is_Excluded()
        {
            var (context, space) = WithHistory(Root,
                Record(1, 100f, 20f), Record(2, 90f, 18f, float.NaN), Record(3, 80f, 16f, float.PositiveInfinity));

            Assert.Single(SurrogateTrainingSet.Build(context, space).Records);
        }

        [Fact]
        public void Control_Runs_With_Variant_99_Are_Excluded()
        {
            var (context, space) = WithHistory(Root, Record(1, 100f, 20f), Record(99, 90f, 18f));

            var set = SurrogateTrainingSet.Build(context, space);

            Assert.Equal(1, set.Records[0].Variant);
            Assert.Single(set.Records);
        }

        [Fact]
        public void Only_Records_From_The_Runs_Mesh_Level_Are_Used_And_Records_Without_The_Key_Are_Accepted()
        {
            var coarse = Record(2, 90f, 18f);
            coarse.PassiveParameters["MeshRefinementFactor"] = 2.0f;
            var matching = Record(3, 80f, 16f);
            matching.PassiveParameters["MeshRefinementFactor"] = 1.0f;
            var without = Record(1, 100f, 20f);

            var (context, space) = WithHistory(Root, without, coarse, matching);
            context.EffectiveMeshRefinementFactor = 1.0f;

            var set = SurrogateTrainingSet.Build(context, space);

            Assert.Equal(new[] { 1, 3 }, set.Records.Select(r => r.Variant));
        }

        // ---------------------------------------------------------------- CSV-Spalte

        [Fact]
        public void The_Csv_Has_The_Disqualified_Column_At_The_End()
        {
            var bounced = Record(2, 90f, 18f);
            bounced.Disqualified = true;
            var (context, _) = WithHistory(Root, Record(1, 100f, 20f), bounced);

            context.ExportToCsv();

            string[] lines = File.ReadAllLines(Path.Combine(Root, "Simulation_Results.csv"));
            Assert.EndsWith(";SimulationFailed;Disqualified", lines[0]);
            Assert.EndsWith(";0;0", lines[1]);
            Assert.EndsWith(";0;1", lines[2]);
        }
    }
}
