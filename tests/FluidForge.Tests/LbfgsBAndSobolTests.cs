using System;
using System.Collections.Generic;
using System.Linq;
using FluidForge.Core;
using Xunit;

namespace FluidForge.Tests
{
    public class LbfgsBAndSobolTests
    {
        [Theory]
        [InlineData(1e-6)]
        [InlineData(0.001)]
        [InlineData(0.02425)]
        [InlineData(0.1)]
        [InlineData(0.5)]
        [InlineData(0.841344746)]
        [InlineData(0.975)]
        [InlineData(0.999)]
        [InlineData(1.0 - 1e-6)]
        public void NormalDistribution_InverseCdf_Matches_Cdf_Across_Range(double p)
        {
            double z = NormalDistribution.InverseCdf(p);
            double roundTrip = NormalDistribution.Cdf(z);
            Assert.True(double.IsFinite(z));
            Assert.True(Math.Abs(roundTrip - p) < 1e-6, $"p={p}, z={z}, roundTrip={roundTrip}");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(-5.0)]
        [InlineData(2.5)]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(double.PositiveInfinity)]
        public void NormalDistribution_InverseCdf_Never_Returns_NaN_Or_Infinity(double p)
        {
            double z = NormalDistribution.InverseCdf(p);
            Assert.True(double.IsFinite(z), $"Expected finite InverseCdf({p}), got {z}");
        }

        [Fact]
        public void SobolSequence_Is_Uniform_Reproducible_And_Works_Up_To_High_Dimensions()
        {
            double[][] a = SobolSequence.Generate(256, 32);
            double[][] b = SobolSequence.Generate(256, 32);

            Assert.Equal(256, a.Length);
            Assert.All(a, row =>
            {
                Assert.Equal(32, row.Length);
                Assert.All(row, v => Assert.InRange(v, 1e-12, 1.0 - 1e-12));
            });
            Assert.Equal(a.SelectMany(r => r), b.SelectMany(r => r));

            // Check Halton fallback beyond dimension 64
            double[][] highDim = SobolSequence.Generate(16, 70);
            Assert.Equal(16, highDim.Length);
            Assert.All(highDim, row =>
            {
                Assert.Equal(70, row.Length);
                Assert.All(row, v => Assert.InRange(v, 0.0, 1.0));
            });

            // Shifted & normal samples reproducibility
            double[][] shiftedA = SobolSequence.GenerateShifted(64, 8, new Random(17));
            double[][] shiftedB = SobolSequence.GenerateShifted(64, 8, new Random(17));
            Assert.Equal(shiftedA.SelectMany(r => r), shiftedB.SelectMany(r => r));

            double[][] normals = SobolSequence.GenerateNormalSamples(128, 4, new Random(9));
            double meanDim0 = normals.Average(r => r[0]);
            Assert.True(Math.Abs(meanDim0) < 0.25, $"Unexpected mean {meanDim0}");
            Assert.All(normals, row => Assert.All(row, v => Assert.True(double.IsFinite(v))));
        }

        [Fact]
        public void SobolSequence_Achieves_Lower_Integration_Error_Than_PseudoRandom()
        {
            // Smooth 4D integral over [0, 1]^4: prod_{j=0..3} (1 + c_j * (x_j - 0.5)), exact integral = 1.0
            const int d = 4;
            const int n = 256;
            double[] weights = { 0.8, -0.6, 0.5, -0.4 };

            double Integrand(double[] x)
            {
                double prod = 1.0;
                for (int j = 0; j < d; j++) prod *= 1.0 + weights[j] * (x[j] - 0.5);
                return prod;
            }

            double[][] sobol = SobolSequence.Generate(n, d);
            double sobolMean = sobol.Average(Integrand);
            double sobolError = Math.Abs(sobolMean - 1.0);

            var rng = new Random(42);
            double prngSum = 0.0;
            for (int trial = 0; trial < 5; trial++)
            {
                double trialSum = 0.0;
                for (int i = 0; i < n; i++)
                {
                    var p = new double[d];
                    for (int j = 0; j < d; j++) p[j] = rng.NextDouble();
                    trialSum += Integrand(p);
                }
                prngSum += Math.Abs(trialSum / n - 1.0);
            }
            double avgPrngError = prngSum / 5.0;

            Assert.True(sobolError < avgPrngError, $"Sobol error {sobolError:E3} >= PRNG avg error {avgPrngError:E3}");
            Assert.True(sobolError < 0.005, $"Sobol error too high: {sobolError:E3}");
        }

        [Fact]
        public void LbfgsB_Converges_On_Box_Constrained_Quadratic_Interior_And_Boundary()
        {
            // 1. Interior optimum at (2, -1)
            (double[] xInt, double fInt, _) = LbfgsB.Minimize(
                x => (
                    Math.Pow(x[0] - 2.0, 2) + 3.0 * Math.Pow(x[1] + 1.0, 2),
                    new[] { 2.0 * (x[0] - 2.0), 6.0 * (x[1] + 1.0) }),
                start: new[] { -3.0, 4.0 },
                lower: new[] { -5.0, -5.0 },
                upper: new[] { 5.0, 5.0 });

            Assert.Equal(2.0, xInt[0], 5);
            Assert.Equal(-1.0, xInt[1], 5);
            Assert.True(fInt < 1e-10);

            // 2. Boundary-constrained optimum: unconstrained min at (4, -4), box [0, 2] x [-2, 3] -> (2, -2)
            (double[] xBound, double fBound, _) = LbfgsB.Minimize(
                x => (
                    Math.Pow(x[0] - 4.0, 2) + Math.Pow(x[1] + 4.0, 2),
                    new[] { 2.0 * (x[0] - 4.0), 2.0 * (x[1] + 4.0) }),
                start: new[] { 0.5, 1.0 },
                lower: new[] { 0.0, -2.0 },
                upper: new[] { 2.0, 3.0 });

            Assert.Equal(2.0, xBound[0], 6);
            Assert.Equal(-2.0, xBound[1], 6);
            Assert.Equal(8.0, fBound, 6);
        }

        [Fact]
        public void LbfgsB_Minimizes_Rosenbrock_Function()
        {
            (double[] bestX, double bestF, _) = LbfgsB.Minimize(
                x =>
                {
                    double t1 = 1.0 - x[0];
                    double t2 = x[1] - x[0] * x[0];
                    double val = t1 * t1 + 100.0 * t2 * t2;
                    double g0 = -2.0 * t1 - 400.0 * x[0] * t2;
                    double g1 = 200.0 * t2;
                    return (val, new[] { g0, g1 });
                },
                start: new[] { -1.2, 1.0 },
                lower: new[] { -2.0, -2.0 },
                upper: new[] { 2.0, 2.0 },
                maxIterations: 80,
                gradTol: 1e-7);

            Assert.True(bestF < 1e-8, $"Rosenbrock did not converge: f={bestF}, x=({bestX[0]}, {bestX[1]})");
            Assert.Equal(1.0, bestX[0], 3);
            Assert.Equal(1.0, bestX[1], 3);
        }

        [Fact]
        public void Gp_LogMarginalPosterior_Gradient_Matches_Finite_Differences()
        {
            var random = new Random(13);
            var x = new List<double[]>();
            var y = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                var p = new[] { random.NextDouble(), random.NextDouble(), random.NextDouble() };
                x.Add(p);
                y.Add(Math.Sin(4.0 * p[0]) + 0.3 * p[1] * p[1] + 0.1 * p[2]);
            }

            double mean = y.Average();
            double sd = Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / y.Count);
            var ys = y.Select(v => (v - mean) / sd).ToList();

            double[] theta = { Math.Log(0.35), Math.Log(1.1), Math.Log(2.2), Math.Log(1.4), Math.Log(0.02) };
            var gradient = new double[theta.Length];
            GaussianProcess.LogMarginalPosterior(x, ys, theta, gradient);

            for (int i = 0; i < theta.Length; i++)
            {
                const double h = 1e-5;
                var plus = (double[])theta.Clone(); plus[i] += h;
                var minus = (double[])theta.Clone(); minus[i] -= h;
                double numeric = (GaussianProcess.LogMarginalPosterior(x, ys, plus, null)
                                  - GaussianProcess.LogMarginalPosterior(x, ys, minus, null)) / (2.0 * h);

                double relative = Math.Abs(gradient[i] - numeric) / Math.Max(1e-8, Math.Abs(numeric));
                Assert.True(relative < 1e-4, $"Hyperparameter {i}: analytic {gradient[i]}, numeric {numeric}");
            }
        }

        [Fact]
        public void Gp_With_LbfgsB_And_MapPriors_Is_Stable_Small_Large_And_Calibrated()
        {
            // Small n = 8: no ARD length-scale collapse
            var sobolSmall = SobolSequence.Generate(8, 2);
            var xSmall = sobolSmall.ToList();
            var ySmall = xSmall.Select(p => Math.Sin(5.0 * p[0]) + 0.5 * p[1]).ToList();
            var gpSmall = new GaussianProcess(new Random(3));
            gpSmall.Fit(xSmall, ySmall);
            Assert.All(gpSmall.LengthScales!, l => Assert.InRange(l, 0.05, 20.0));

            // Large n = 180 (> 150) multi-start refit remains accurate
            var rng = new Random(7);
            var xLarge = new List<double[]>();
            var yLarge = new List<double>();
            const double noiseStd = 0.15;
            for (int i = 0; i < 180; i++)
            {
                double[] p = { rng.NextDouble(), rng.NextDouble() };
                xLarge.Add(p);
                double signal = Math.Sin(4.0 * p[0]) + Math.Cos(3.0 * p[1]);
                yLarge.Add(signal + noiseStd * NormalDistribution.Sample(rng));
            }

            var gpLarge = new GaussianProcess(new Random(5));
            gpLarge.Fit(xLarge, yLarge);
            Assert.True(gpLarge.ObservationNoiseVariance > 0.005);

            // Check 95% predictive band coverage of true noiseless function on 200 test points
            int covered = 0;
            const int testPoints = 200;
            double[][] testGrid = SobolSequence.Generate(testPoints, 2, skip: 300);
            foreach (double[] p in testGrid)
            {
                double truth = Math.Sin(4.0 * p[0]) + Math.Cos(3.0 * p[1]);
                var (mean, latentVar) = gpLarge.Predict(p);
                double sd = Math.Sqrt(latentVar);
                if (truth >= mean - 2.2 * sd && truth <= mean + 2.2 * sd)
                {
                    covered++;
                }
            }

            double coverage = (double)covered / testPoints;
            Assert.True(coverage >= 0.80, $"Expected calibrated uncertainty coverage >= 80%, got {coverage:P1}");
        }
    }
}
