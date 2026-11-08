using System;
using System.Collections.Generic;
using System.Linq;
using MyPicoGkProject.Core;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-54: der mathematische Kern des Bayes-Optimierers. Feste Seeds, keine Zeitgrenzen (F22).
    /// </summary>
    public class GaussianProcessTests
    {
        // ------------------------------------------------------------------ Cholesky

        [Fact]
        public void Cholesky_Matches_Known_3x3_And_Solves_With_Tiny_Residual()
        {
            // A = [[4,12,-16],[12,37,-43],[-16,-43,98]]  →  L = [[2],[6,1],[-8,5,3]]
            double[][] a = { new[] { 4.0 }, new[] { 12.0, 37.0 }, new[] { -16.0, -43.0, 98.0 } };

            double[][] l = DenseCholesky.Factor(a, out double jitter);

            Assert.Equal(0.0, jitter);
            Assert.Equal(2.0, l[0][0], 12);
            Assert.Equal(6.0, l[1][0], 12);
            Assert.Equal(1.0, l[1][1], 12);
            Assert.Equal(-8.0, l[2][0], 12);
            Assert.Equal(5.0, l[2][1], 12);
            Assert.Equal(3.0, l[2][2], 12);

            double[] b = { 1.0, 2.0, 3.0 };
            double[] x = DenseCholesky.Solve(l, b);
            double[][] full = { new[] { 4.0, 12, -16 }, new[] { 12.0, 37, -43 }, new[] { -16.0, -43, 98 } };
            for (int i = 0; i < 3; i++)
            {
                double row = 0;
                for (int j = 0; j < 3; j++) row += full[i][j] * x[j];
                Assert.True(Math.Abs(row - b[i]) < 1e-10, $"Restfehler in Zeile {i}: {row - b[i]}");
            }

            // ln det A = ln((2·1·3)²) = ln 36
            Assert.Equal(Math.Log(36.0), DenseCholesky.LogDeterminant(l), 12);
        }

        [Fact]
        public void Cholesky_Inverse_Gives_Identity()
        {
            double[][] a = { new[] { 4.0 }, new[] { 12.0, 37.0 }, new[] { -16.0, -43.0, 98.0 } };
            double[][] l = DenseCholesky.Factor(a, out _);
            double[][] inv = DenseCholesky.Inverse(l);

            double[][] full = { new[] { 4.0, 12, -16 }, new[] { 12.0, 37, -43 }, new[] { -16.0, -43, 98 } };
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        double invKj = k >= j ? inv[k][j] : inv[j][k];
                        sum += full[i][k] * invKj;
                    }
                    Assert.True(Math.Abs(sum - (i == j ? 1.0 : 0.0)) < 1e-9, $"A·A⁻¹ [{i},{j}] = {sum}");
                }
            }
        }

        [Fact]
        public void Cholesky_Uses_The_Jitter_Ladder_For_A_Singular_Matrix()
        {
            // Zwei identische Punkte: exakt singulär.
            double[][] a = { new[] { 1.0 }, new[] { 1.0, 1.0 } };

            double[][] l = DenseCholesky.Factor(a, out double jitter);

            Assert.True(jitter > 0 && jitter <= 1e-4, $"Zuschlag {jitter}");
            Assert.True(l[1][1] > 0);
        }

        [Fact]
        public void Cholesky_Throws_A_Fit_Exception_For_A_Hopeless_Matrix()
        {
            double[][] a = { new[] { -1.0 } };
            Assert.Throws<SurrogateFitException>(() => DenseCholesky.Factor(a, out _));
        }

        // ------------------------------------------------------------------ Normalverteilung

        [Theory]
        [InlineData(0.0, 0.5)]
        [InlineData(1.0, 0.8413447460685429)]
        [InlineData(-1.0, 0.15865525393145707)]
        [InlineData(1.96, 0.9750021048517795)]
        [InlineData(-3.5, 0.00023262907903552504)]
        [InlineData(-6.0, 9.865876450376946e-10)]
        [InlineData(4.0, 0.9999683287581669)]
        public void Cdf_Is_Accurate(double z, double expected)
        {
            double actual = NormalDistribution.Cdf(z);
            Assert.True(Math.Abs(actual - expected) <= 1e-7 * Math.Max(expected, 1e-300) + 1e-15,
                $"Φ({z}) = {actual}, erwartet {expected}");
        }

        [Fact]
        public void Pdf_Peaks_At_Zero()
        {
            Assert.Equal(0.3989422804014327, NormalDistribution.Pdf(0), 12);
            Assert.Equal(NormalDistribution.Pdf(1.3), NormalDistribution.Pdf(-1.3), 15);
        }

        [Fact]
        public void Sample_Has_Mean_Zero_And_Unit_Variance()
        {
            var random = new Random(7);
            var values = Enumerable.Range(0, 20000).Select(_ => NormalDistribution.Sample(random)).ToList();
            double mean = values.Average();
            double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            Assert.True(Math.Abs(mean) < 0.03, $"Mittel {mean}");
            Assert.True(Math.Abs(variance - 1.0) < 0.05, $"Varianz {variance}");
        }

        // ------------------------------------------------------------------ Latin Hypercube

        [Fact]
        public void LatinHypercube_Puts_Exactly_One_Point_In_Each_Stratum_Of_Every_Dimension()
        {
            const int n = 13, d = 4;
            double[][] points = LatinHypercube.Sample(n, d, new Random(3));

            Assert.Equal(n, points.Length);
            for (int dim = 0; dim < d; dim++)
            {
                var strata = points.Select(p => (int)Math.Floor(p[dim] * n)).OrderBy(i => i).ToList();
                Assert.Equal(Enumerable.Range(0, n), strata);
            }
        }

        [Fact]
        public void LatinHypercube_Is_Reproducible_For_A_Fixed_Seed()
        {
            var a = LatinHypercube.Sample(8, 3, new Random(11));
            var b = LatinHypercube.Sample(8, 3, new Random(11));
            Assert.Equal(a.SelectMany(p => p), b.SelectMany(p => p));
        }

        // ------------------------------------------------------------------ Gauß-Prozess

        private static (List<double[]> X, List<double> Y) Sine1D(int n)
        {
            var x = new List<double[]>();
            var y = new List<double>();
            for (int i = 0; i < n; i++)
            {
                double xi = i / (double)(n - 1);
                x.Add(new[] { xi });
                y.Add(Math.Sin(6.0 * xi));
            }
            return (x, y);
        }

        [Fact]
        public void Gp_Learns_A_Sine_From_Eight_Points()
        {
            var (x, y) = Sine1D(8);
            var gp = new GaussianProcess(new Random(1));
            gp.Fit(x, y);

            double error = 0;
            const int checks = 50;
            for (int i = 0; i < checks; i++)
            {
                double xi = i / (checks - 1.0);
                error += Math.Abs(gp.Predict(new[] { xi }).Mean - Math.Sin(6.0 * xi));
            }
            error /= checks;

            Assert.True(error < 0.05, $"mittlerer Fehler {error}");
        }

        [Fact]
        public void Gp_Variance_Is_Small_At_Data_And_Large_Far_Away()
        {
            var x = new List<double[]> { new[] { 0.05 }, new[] { 0.1 }, new[] { 0.15 }, new[] { 0.2 }, new[] { 0.25 } };
            var y = new List<double> { 0.0, 0.2, 0.1, 0.3, 0.2 };
            var gp = new GaussianProcess(new Random(1));
            gp.Fit(x, y);

            double near = gp.Predict(new[] { 0.15 }).Variance;
            double far = gp.Predict(new[] { 0.95 }).Variance;

            Assert.True(far > 10 * near, $"nah {near}, fern {far}");
            // Weit weg nähert sich die Varianz der Signalvarianz (in Ausgabeeinheiten).
            double scale = Math.Sqrt(y.Sum(v => (v - y.Average()) * (v - y.Average())) / y.Count);
            Assert.InRange(far, 0.5 * gp.SignalVariance * scale * scale, 1.001 * gp.SignalVariance * scale * scale);
        }

        [Fact]
        public void Gp_Gradient_Matches_Finite_Differences_For_Every_Hyperparameter()
        {
            var random = new Random(5);
            var x = new List<double[]>();
            var y = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                var p = new[] { random.NextDouble(), random.NextDouble(), random.NextDouble() };
                x.Add(p);
                y.Add(Math.Sin(4 * p[0]) + 0.5 * p[1] * p[1] + 0.05 * (random.NextDouble() - 0.5));
            }
            double mean = y.Average();
            double sd = Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / y.Count);
            var ys = y.Select(v => (v - mean) / sd).ToList();

            double[] theta = { Math.Log(0.4), Math.Log(0.9), Math.Log(1.7), Math.Log(1.3), Math.Log(0.03) };
            var gradient = new double[theta.Length];
            GaussianProcess.LogMarginalLikelihood(x, ys, theta, gradient);

            for (int i = 0; i < theta.Length; i++)
            {
                const double h = 1e-5;
                var plus = (double[])theta.Clone(); plus[i] += h;
                var minus = (double[])theta.Clone(); minus[i] -= h;
                double numeric = (GaussianProcess.LogMarginalLikelihood(x, ys, plus, null)
                                  - GaussianProcess.LogMarginalLikelihood(x, ys, minus, null)) / (2 * h);

                double relative = Math.Abs(gradient[i] - numeric) / Math.Max(1e-8, Math.Abs(numeric));
                Assert.True(relative < 1e-4, $"Hyperparameter {i}: analytisch {gradient[i]}, numerisch {numeric}");
            }
        }

        [Fact]
        public void Gp_Ard_Gives_An_Irrelevant_Dimension_A_Much_Longer_Length_Scale()
        {
            var random = new Random(9);
            var x = new List<double[]>();
            var y = new List<double>();
            for (int i = 0; i < 30; i++)
            {
                var p = new[] { random.NextDouble(), random.NextDouble() };
                x.Add(p);
                y.Add(Math.Sin(6 * p[0]));
            }

            var gp = new GaussianProcess(new Random(2));
            gp.Fit(x, y);

            var lengths = gp.LengthScales!;
            Assert.True(lengths[1] >= 5 * lengths[0], $"ℓ = [{lengths[0]}, {lengths[1]}]");

            var relevance = gp.ParameterRelevance!;
            Assert.Equal(1.0, relevance[0], 9);
            Assert.True(relevance[1] < 0.2, $"Relevanz x2 = {relevance[1]}");
        }

        [Fact]
        public void Gp_Handles_Degenerate_Data()
        {
            // n = 1
            var one = new GaussianProcess(new Random(1));
            one.Fit(new List<double[]> { new[] { 0.5, 0.5 } }, new List<double> { 3.0 });
            Assert.Equal(3.0, one.Predict(new[] { 0.5, 0.5 }).Mean, 1);

            // n = 2
            var two = new GaussianProcess(new Random(1));
            two.Fit(new List<double[]> { new[] { 0.1 }, new[] { 0.9 } }, new List<double> { 1.0, 2.0 });
            var (mean, variance) = two.Predict(new[] { 0.5 });
            Assert.True(double.IsFinite(mean) && variance > 0);

            // konstante Ausgaben
            var constant = new GaussianProcess(new Random(1));
            constant.Fit(new List<double[]> { new[] { 0.1 }, new[] { 0.5 }, new[] { 0.9 } }, new List<double> { 7.0, 7.0, 7.0 });
            Assert.Equal(7.0, constant.Predict(new[] { 0.3 }).Mean, 3);

            // doppelte Punkte
            var duplicates = new GaussianProcess(new Random(1));
            duplicates.Fit(
                new List<double[]> { new[] { 0.2 }, new[] { 0.2 }, new[] { 0.8 }, new[] { 0.8 } },
                new List<double> { 1.0, 1.0, 2.0, 2.0 });
            Assert.True(double.IsFinite(duplicates.Predict(new[] { 0.5 }).Mean));
        }

        [Fact]
        public void Gp_Rejects_Non_Finite_Outputs_With_A_Clear_Message()
        {
            var gp = new GaussianProcess(new Random(1));
            var ex = Assert.Throws<ArgumentException>(() => gp.Fit(
                new List<double[]> { new[] { 0.1 }, new[] { 0.5 } }, new List<double> { 1.0, double.NaN }));
            Assert.Contains("nicht endlich", ex.Message);
        }

        [Fact]
        public void Gp_Is_Bitwise_Reproducible_With_A_Fixed_Seed()
        {
            var (x, y) = Sine1D(10);
            var a = new GaussianProcess(new Random(4));
            var b = new GaussianProcess(new Random(4));
            a.Fit(x, y);
            b.Fit(x, y);

            Assert.Equal(a.Predict(new[] { 0.37 }), b.Predict(new[] { 0.37 }));
            Assert.Equal(a.LengthScales, b.LengthScales);
        }

        [Fact]
        public void Pseudo_Observation_Shrinks_The_Variance_Without_Relearning()
        {
            var (x, y) = Sine1D(6);
            var gp = new GaussianProcess(new Random(1));
            gp.Fit(x, y);

            var lengthsBefore = gp.LengthScales!.ToArray();
            var probe = new[] { 0.55 };
            var (mean, varianceBefore) = gp.Predict(probe);

            gp.AddPseudoObservation(probe, mean);
            var (meanAfter, varianceAfter) = gp.Predict(probe);

            Assert.True(varianceAfter < varianceBefore * 0.2, $"{varianceBefore} → {varianceAfter}");
            Assert.Equal(mean, meanAfter, 3);
            Assert.Equal(lengthsBefore, gp.LengthScales!.ToArray());
            Assert.Equal(7, gp.Count);
        }

        [Fact]
        public void Gp_With_400_Points_In_20_Dimensions_Runs_Through()
        {
            // Rauchprobe ohne Zeitgrenze (F22).
            var random = new Random(21);
            var x = new List<double[]>();
            var y = new List<double>();
            for (int i = 0; i < 400; i++)
            {
                var p = Enumerable.Range(0, 20).Select(_ => random.NextDouble()).ToArray();
                x.Add(p);
                y.Add(p.Sum(v => (v - 0.3) * (v - 0.3)));
            }

            var gp = new GaussianProcess(new Random(1));
            gp.Fit(x, y);

            var (mean, variance) = gp.Predict(Enumerable.Repeat(0.3, 20).ToArray());
            Assert.True(double.IsFinite(mean) && variance > 0);
        }
    }
}
