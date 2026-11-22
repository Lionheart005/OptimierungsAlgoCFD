using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Standard normal distribution helpers for Expected Improvement acquisition.
    /// </summary>
    public static class NormalDistribution
    {
        private static readonly double InvSqrt2Pi = 1.0 / Math.Sqrt(2.0 * Math.PI);

        /// <summary>Probability density function φ(z).</summary>
        public static double Pdf(double z) => InvSqrt2Pi * Math.Exp(-0.5 * z * z);

        /// <summary>
        /// Cumulative distribution function Φ(z) = ½·erfc(−z/√2). Relative error below 1e-7 for |z| ≤ 8.
        /// </summary>
        public static double Cdf(double z)
        {
            if (double.IsNaN(z)) return double.NaN;
            return 0.5 * Erfc(-z / Math.Sqrt(2.0));
        }

        /// <summary>Draws a standard normal random sample (Box-Muller).</summary>
        public static double Sample(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = 1.0 - random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        /// <summary>
        /// Complementary error function. Uses Taylor series for |x| &lt; 3 and continued
        /// fraction expansion above for fast convergence and accurate tails.
        /// </summary>
        public static double Erfc(double x)
        {
            if (double.IsNaN(x)) return double.NaN;
            if (x < 0.0) return 2.0 - Erfc(-x);
            if (x > 27.0) return 0.0;

            if (x < 3.0) return 1.0 - ErfSeries(x);

            // erfc(x) = exp(-x²)/√π · 1/(x + (1/2)/(x + 1/(x + (3/2)/(x + 2/(x + ...)))))
            double f = x;
            for (int k = 60; k >= 1; k--) f = x + (k / 2.0) / f;
            return Math.Exp(-x * x) / (Math.Sqrt(Math.PI) * f);
        }

        private static double ErfSeries(double x)
        {
            // erf(x) = 2/√π · Σ (-1)ⁿ x^(2n+1) / (n!·(2n+1))
            double sum = x;
            double term = x;
            double x2 = x * x;
            for (int n = 1; n < 200; n++)
            {
                term *= -x2 / n;
                double add = term / (2 * n + 1);
                sum += add;
                if (Math.Abs(add) < 1e-17 * Math.Abs(sum)) break;
            }
            return 2.0 / Math.Sqrt(Math.PI) * sum;
        }
    }
}
