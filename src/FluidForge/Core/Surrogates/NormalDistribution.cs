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
        /// Inverse standard normal CDF Φ⁻¹(p) via Peter J. Acklam's rational approximation
        /// (relative error &lt; 1.15e-9 over (0, 1)). Inputs are safely clamped to [1e-12, 1 - 1e-12]
        /// so NaN and Infinity are never returned.
        /// </summary>
        public static double InverseCdf(double p)
        {
            if (double.IsNaN(p)) return 0.0;
            p = Math.Clamp(p, 1e-12, 1.0 - 1e-12);

            const double a1 = -3.969683028665376e+01;
            const double a2 =  2.209460984245205e+02;
            const double a3 = -2.759285104469687e+02;
            const double a4 =  1.383577518672690e+02;
            const double a5 = -3.066479806614716e+01;
            const double a6 =  2.506628277459239e+00;

            const double b1 = -5.447609879822406e+01;
            const double b2 =  1.615858368580409e+02;
            const double b3 = -1.556989798598866e+02;
            const double b4 =  6.680131188771972e+01;
            const double b5 = -1.328068155288572e+01;

            const double c1 = -7.784894002430293e-03;
            const double c2 = -3.223964580411365e-01;
            const double c3 = -2.400758277161838e+00;
            const double c4 = -2.549732539343734e+00;
            const double c5 =  4.374664141464968e+00;
            const double c6 =  2.938163982698783e+00;

            const double d1 =  7.784695709041462e-03;
            const double d2 =  3.224671290700398e-01;
            const double d3 =  2.445134137142996e+00;
            const double d4 =  3.754408661907416e+00;

            const double pLow = 0.02425;
            const double pHigh = 1.0 - pLow;

            if (p < pLow)
            {
                double q = Math.Sqrt(-2.0 * Math.Log(p));
                return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
                     / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
            }

            if (p <= pHigh)
            {
                double q = p - 0.5;
                double r = q * q;
                return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q
                     / (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
            }

            {
                double q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
                return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6)
                      / ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
            }
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
