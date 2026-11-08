using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Moq;
using Xunit;

using OptimierungsAlgoCFD.Composition;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Schwankende Rechnungen (Wirbelablösung, <c>Uncertainty_*</c>) dürfen nicht als Instabilität oder Netzabhängigkeit
    /// gewertet werden, und ein paar Aufräumpunkte aus dem Durchgang vom 08.11.2026.
    /// </summary>
    public class UncertaintyAndHygieneTests : TempDirectoryTest
    {
        // ------------------------------------------------------------------ gemeinsamer Baustein

        [Fact]
        public void The_Allowed_Deviation_Grows_With_The_Uncertainty_Of_Both_Runs()
        {
            Assert.Equal(0.05f, MetricTolerance.WithUncertainty(0.05f, 0.001, 0.001));      // ruhige Rechnung: unverändert
            Assert.Equal(0.1131f, MetricTolerance.WithUncertainty(0.05f, 0.04, 0.04), 3);   // 2 · √(0,04² + 0,04²)
        }

        // ------------------------------------------------------------------ Türsteher

        private sealed class DragFitness : IFitnessCalculator
        {
            private readonly float _value;
            public DragFitness(float value) => _value = value;
            public IReadOnlyCollection<string> RequiredMetrics => new[] { "Drag" };
            public void CalculateFitness(ModelRecord record, SimulationConfig config) => record.Fitness = _value;
        }

        private static ModelRecord Record(float fitness, float uncertainty)
        {
            var r = new ModelRecord { Iteration = 1, Variant = 1, Fitness = fitness };
            r.ActiveParameters["Length"] = 50f;
            if (uncertainty > 0) r.PassiveParameters[MetricUncertainty.KeyFor("Drag")] = uncertainty;
            return r;
        }

        [Fact]
        public void A_Fluctuating_Champion_Is_Not_Rejected_For_A_Deviation_Within_Its_Own_Noise()
        {
            // Referenz 100, Wiederholung 80: 20 % Abweichung, Toleranz 10 %. Beide Rechnungen schwanken um 10 %.
            var validator = new ChampionValidator(new DragFitness(80f), new Random(1));
            var candidate = Record(100f, 0.10f);

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                new SimulationConfig { BouncerTolerance = 0.10f },
                _ => Record(80f, 0.10f));

            Assert.False(candidate.Disqualified);
            Assert.Same(candidate, winner);
        }

        [Fact]
        public void The_Same_Deviation_Without_Noise_Still_Rejects_The_Champion()
        {
            var validator = new ChampionValidator(new DragFitness(80f), new Random(1));
            var candidate = Record(100f, 0f);

            validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                new SimulationConfig { BouncerTolerance = 0.10f },
                _ => Record(80f, 0f));

            Assert.True(candidate.Disqualified);
        }

        // ------------------------------------------------------------------ Feinprüfung und Kalibrierung

        [Fact]
        public void The_Fine_Verification_Counts_Noise_As_Allowed_Deviation()
        {
            var normal = new ModelRecord(); normal.PassiveParameters["Drag"] = 0.100f; normal.PassiveParameters["Uncertainty_Drag"] = 0.08f;
            var fine = new ModelRecord(); fine.PassiveParameters["Drag"] = 0.088f; fine.PassiveParameters["Uncertainty_Drag"] = 0.08f;   // 12 % Abstand

            float noisy = ChampionFineVerifier.ComputeMaxNormalizedDeviation(normal, fine, new[] { "Drag" }, 0.05f, null);
            Assert.True(noisy <= 1f, $"normiert {noisy}");

            normal.PassiveParameters.Remove("Uncertainty_Drag");
            fine.PassiveParameters.Remove("Uncertainty_Drag");
            float calm = ChampionFineVerifier.ComputeMaxNormalizedDeviation(normal, fine, new[] { "Drag" }, 0.05f, null);
            Assert.True(calm > 1f, $"normiert {calm}");
        }

        [Fact]
        public void The_Mesh_Calibration_Counts_Noise_As_Allowed_Deviation()
        {
            static CalibrationStageData Stage(int level, float drag, float u) => new(
                level, 1f, 1000, 1.0,
                new Dictionary<string, float> { ["Drag"] = drag }, 1f, true, 1f, true,
                u > 0 ? new Dictionary<string, float> { ["Drag"] = u } : null);

            var reference = Stage(2, 0.100f, 0.08f);
            var coarse = Stage(1, 0.088f, 0.08f);
            var calm = ComputeNormalized(coarse with { Uncertainty = null }, reference with { Uncertainty = null });
            var noisy = ComputeNormalized(coarse, reference);

            Assert.True(calm > 1f);
            Assert.True(noisy <= 1f);

            static float ComputeNormalized(CalibrationStageData stage, CalibrationStageData reference) =>
                CalibrationEvaluator.ComputeDeviations(new[] { stage }, reference, new[] { "Drag" }, 0.05f, null)
                    .Normalized[stage.LevelIndex]["Drag"];
        }

        // ------------------------------------------------------------------ MPI

        [Fact]
        public void OpenMpi_Triggers_A_Warning_And_Mpich_Does_Not()
        {
            var open = new Dictionary<string, string> { ["MPI-Variante"] = ToolVersionProbe.OpenMpi };
            var mpich = new Dictionary<string, string> { ["MPI-Variante"] = ToolVersionProbe.Mpich };
            var unknown = new Dictionary<string, string> { ["MPI-Variante"] = ToolVersionProbe.Unknown };

            string? warning = ToolVersionProbe.MpiWarning(open, "mpirun");
            Assert.NotNull(warning);
            Assert.Contains("MPICH", warning);
            Assert.Null(ToolVersionProbe.MpiWarning(mpich, "mpiexec"));
            Assert.Null(ToolVersionProbe.MpiWarning(unknown, "mpirun"));
        }

        // ------------------------------------------------------------------ Ergebnistabelle

        private static SimulationContext ContextWithAngleOfAttackAsParameterAndMetric(string directory)
        {
            var project = new ProjectConfig { ProjectName = "T" };
            var context = new SimulationContext(new SimulationConfig(), project, directory);
            var record = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 12.5f };
            record.ActiveParameters["AngleOfAttackDeg"] = 3.5f;
            record.PassiveParameters["AngleOfAttackDeg"] = 3.5f;   // der Solver schreibt ihn zusätzlich als Kennwert
            record.PassiveParameters["Drag"] = 0.0125f;
            context.History.Add(record);
            return context;
        }

        [Fact]
        public void The_Results_Table_Has_No_Duplicate_Column_Names()
        {
            SimulationContext context = ContextWithAngleOfAttackAsParameterAndMetric(Sub("csv1"));
            context.ExportToCsv();

            string header = File.ReadLines(Path.Combine(context.WorkingDirectory, "Simulation_Results.csv")).First().TrimStart('﻿');
            string[] columns = header.Split(';');

            Assert.Equal(columns.Length, columns.Distinct().Count());
            Assert.Single(columns, c => c == "AngleOfAttackDeg");
        }

        [Fact]
        public void The_Results_Table_Uses_A_Decimal_Point_Whatever_The_Computer_Language_Is()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                SimulationContext context = ContextWithAngleOfAttackAsParameterAndMetric(Sub("csv2"));
                context.ExportToCsv();

                string data = File.ReadLines(Path.Combine(context.WorkingDirectory, "Simulation_Results.csv")).Skip(1).First();
                Assert.Contains("3.500", data);
                Assert.Contains("12.50000", data);
                Assert.DoesNotContain("3,500", data);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
