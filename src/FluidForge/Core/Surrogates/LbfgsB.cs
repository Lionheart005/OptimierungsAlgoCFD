using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Box-constrained Projected L-BFGS (L-BFGS-B style) minimizer in pure C#.
    /// Uses a two-loop L-BFGS inverse-Hessian approximation with active-bound projection,
    /// projected Armijo backtracking line search (robust against NaN/+Infinity evaluations),
    /// and a strict curvature condition for secant history updates.
    /// </summary>
    public static class LbfgsB
    {
        private const double BoundTolerance = 1e-12;
        private const double ArmijoC1 = 1e-4;
        private const double WolfeC2 = 0.9;
        private const int MaxLineSearchSteps = 28;
        private const double MaxStepExpansion = 64.0;

        /// <summary>
        /// Minimizes <paramref name="objective"/> subject to <c>lower[i] &lt;= x[i] &lt;= upper[i]</c>.
        /// Never returns a point worse than a valid clamped <paramref name="start"/>.
        /// </summary>
        public static (double[] BestX, double BestValue, int Iterations) Minimize(
            Func<double[], (double Value, double[] Gradient)> objective,
            double[] start,
            double[] lower,
            double[] upper,
            int maxIterations = 60,
            int memory = 8,
            double gradTol = 1e-6)
        {
            ArgumentNullException.ThrowIfNull(objective);
            ArgumentNullException.ThrowIfNull(start);
            ArgumentNullException.ThrowIfNull(lower);
            ArgumentNullException.ThrowIfNull(upper);

            int n = start.Length;
            if (n == 0) throw new ArgumentException("Start vector must not be empty.", nameof(start));
            if (lower.Length != n || upper.Length != n)
                throw new ArgumentException("Bounds length must match start vector length.");

            for (int i = 0; i < n; i++)
            {
                if (lower[i] > upper[i])
                    throw new ArgumentException($"Lower bound exceeds upper bound at index {i} ({lower[i]} > {upper[i]}).");
            }

            double[] x = Clamp(start, lower, upper);
            (double f, double[] g) = SafeEvaluate(objective, x, n);

            if (!double.IsFinite(f))
            {
                return ((double[])x.Clone(), f, 0);
            }

            double[] bestX = (double[])x.Clone();
            double bestF = f;

            var sHistory = new List<double[]>(Math.Max(1, memory));
            var yHistory = new List<double[]>(Math.Max(1, memory));
            var rhoHistory = new List<double>(Math.Max(1, memory));

            int iterations = 0;
            for (int iter = 0; iter < maxIterations; iter++)
            {
                double[] pg = ProjectedGradient(x, g, lower, upper);
                if (MaxAbs(pg) <= gradTol)
                {
                    break;
                }

                double[] dir = ComputeProjectedDirection(x, g, pg, lower, upper, sHistory, yHistory, rhoHistory);
                double slope = Dot(dir, g);

                if (!(slope < -1e-15))
                {
                    sHistory.Clear();
                    yHistory.Clear();
                    rhoHistory.Clear();
                    dir = Negate(pg);
                    slope = Dot(dir, g);
                    if (!(slope < -1e-15))
                    {
                        break;
                    }
                }

                bool accepted = TryLineSearch(
                    objective, x, f, g, dir, lower, upper, n,
                    out double[] xNext, out double fNext, out double[] gNext);

                if (!accepted && sHistory.Count > 0)
                {
                    // Reset quasi-Newton history and retry with projected steepest descent.
                    sHistory.Clear();
                    yHistory.Clear();
                    rhoHistory.Clear();
                    dir = Negate(pg);
                    accepted = TryLineSearch(
                        objective, x, f, g, dir, lower, upper, n,
                        out xNext, out fNext, out gNext);
                }

                if (!accepted)
                {
                    break;
                }

                iterations++;

                if (fNext < bestF)
                {
                    bestF = fNext;
                    bestX = (double[])xNext.Clone();
                }

                double[] s = new double[n];
                double[] y = new double[n];
                for (int i = 0; i < n; i++)
                {
                    s[i] = xNext[i] - x[i];
                    y[i] = gNext[i] - g[i];
                }

                double sy = Dot(s, y);
                double normS = Norm(s);
                double normY = Norm(y);

                if (sy > 1e-10 * normS * normY && normS > 0.0 && normY > 0.0)
                {
                    if (memory > 0)
                    {
                        if (sHistory.Count == memory)
                        {
                            sHistory.RemoveAt(0);
                            yHistory.RemoveAt(0);
                            rhoHistory.RemoveAt(0);
                        }
                        sHistory.Add(s);
                        yHistory.Add(y);
                        rhoHistory.Add(1.0 / sy);
                    }
                }

                x = xNext;
                f = fNext;
                g = gNext;

                if (normS <= 1e-14 * Math.Max(1.0, Norm(x)))
                {
                    break;
                }
            }

            return (bestX, bestF, iterations);
        }

        private static bool TryLineSearch(
            Func<double[], (double Value, double[] Gradient)> objective,
            double[] x,
            double f,
            double[] g,
            double[] dir,
            double[] lower,
            double[] upper,
            int n,
            out double[] xNext,
            out double fNext,
            out double[] gNext)
        {
            double alpha = 1.0;
            for (int ls = 0; ls < MaxLineSearchSteps; ls++)
            {
                double[] trial = new double[n];
                double expectedDescent = 0.0;
                bool anyMoved = false;

                for (int i = 0; i < n; i++)
                {
                    trial[i] = Math.Clamp(x[i] + alpha * dir[i], lower[i], upper[i]);
                    double step = trial[i] - x[i];
                    if (step != 0.0) anyMoved = true;
                    expectedDescent += g[i] * step;
                }

                if (!anyMoved)
                {
                    break;
                }

                if (expectedDescent < 0.0)
                {
                    (double fTrial, double[] gTrial) = SafeEvaluate(objective, trial, n);
                    if (double.IsFinite(fTrial) && fTrial <= f + ArmijoC1 * expectedDescent)
                    {
                        // When the initial alpha = 1.0 step passes Armijo but curvature is still negative
                        // along a flat valley, expand alpha so secant pairs (s, y) satisfy s^T y > 0.
                        if (ls == 0)
                        {
                            double slope0 = Dot(g, dir);
                            double slopeT = Dot(gTrial, dir);
                            while (slopeT < WolfeC2 * slope0 && alpha < MaxStepExpansion)
                            {
                                double nextAlpha = alpha * 2.0;
                                double[] nextTrial = new double[n];
                                double nextExpected = 0.0;
                                bool changed = false;

                                for (int i = 0; i < n; i++)
                                {
                                    nextTrial[i] = Math.Clamp(x[i] + nextAlpha * dir[i], lower[i], upper[i]);
                                    if (nextTrial[i] != trial[i]) changed = true;
                                    nextExpected += g[i] * (nextTrial[i] - x[i]);
                                }

                                if (!changed || nextExpected >= 0.0) break;

                                (double fNextTrial, double[] gNextTrial) = SafeEvaluate(objective, nextTrial, n);
                                if (!double.IsFinite(fNextTrial) || fNextTrial > f + ArmijoC1 * nextExpected || fNextTrial >= fTrial)
                                    break;

                                alpha = nextAlpha;
                                trial = nextTrial;
                                fTrial = fNextTrial;
                                gTrial = gNextTrial;
                                slopeT = Dot(gTrial, dir);
                            }
                        }

                        xNext = trial;
                        fNext = fTrial;
                        gNext = gTrial;
                        return true;
                    }
                }

                alpha *= 0.5;
            }

            xNext = x;
            fNext = f;
            gNext = g;
            return false;
        }

        private static double[] ComputeProjectedDirection(
            double[] x,
            double[] g,
            double[] pg,
            double[] lower,
            double[] upper,
            List<double[]> sHistory,
            List<double[]> yHistory,
            List<double> rhoHistory)
        {
            int n = x.Length;
            if (sHistory.Count == 0)
            {
                return Negate(pg);
            }

            int m = sHistory.Count;
            double[] q = (double[])pg.Clone();
            var alphas = new double[m];

            for (int k = m - 1; k >= 0; k--)
            {
                alphas[k] = rhoHistory[k] * Dot(sHistory[k], q);
                double[] yk = yHistory[k];
                for (int i = 0; i < n; i++)
                {
                    q[i] -= alphas[k] * yk[i];
                }
            }

            double[] sLast = sHistory[m - 1];
            double[] yLast = yHistory[m - 1];
            double yy = Dot(yLast, yLast);
            double sy = Dot(sLast, yLast);
            double gamma = yy > 1e-20 ? Math.Clamp(sy / yy, 1e-8, 1e8) : 1.0;

            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                r[i] = gamma * q[i];
            }

            for (int k = 0; k < m; k++)
            {
                double beta = rhoHistory[k] * Dot(yHistory[k], r);
                double diff = alphas[k] - beta;
                double[] sk = sHistory[k];
                for (int i = 0; i < n; i++)
                {
                    r[i] += sk[i] * diff;
                }
            }

            var dir = new double[n];
            for (int i = 0; i < n; i++)
            {
                double d = -r[i];
                if ((x[i] <= lower[i] + BoundTolerance && (g[i] > 0.0 || d <= 0.0)) ||
                    (x[i] >= upper[i] - BoundTolerance && (g[i] < 0.0 || d >= 0.0)))
                {
                    d = 0.0;
                }
                dir[i] = d;
            }

            return dir;
        }

        private static double[] ProjectedGradient(double[] x, double[] g, double[] lower, double[] upper)
        {
            var pg = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                if ((x[i] <= lower[i] + BoundTolerance && g[i] > 0.0) ||
                    (x[i] >= upper[i] - BoundTolerance && g[i] < 0.0))
                {
                    pg[i] = 0.0;
                }
                else
                {
                    pg[i] = g[i];
                }
            }
            return pg;
        }

        private static (double Value, double[] Gradient) SafeEvaluate(
            Func<double[], (double Value, double[] Gradient)> objective, double[] x, int n)
        {
            (double value, double[]? gradient) = objective(x);
            if (!double.IsFinite(value) || gradient == null || gradient.Length != n || !AllFinite(gradient))
            {
                return (double.PositiveInfinity, new double[n]);
            }
            return (value, gradient);
        }

        private static bool AllFinite(double[] v)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if (!double.IsFinite(v[i])) return false;
            }
            return true;
        }

        private static double[] Clamp(double[] x, double[] lower, double[] upper)
        {
            var clamped = new double[x.Length];
            for (int i = 0; i < x.Length; i++)
            {
                clamped[i] = Math.Clamp(x[i], lower[i], upper[i]);
            }
            return clamped;
        }

        private static double[] Negate(double[] v)
        {
            var neg = new double[v.Length];
            for (int i = 0; i < v.Length; i++) neg[i] = -v[i];
            return neg;
        }

        private static double Dot(double[] a, double[] b)
        {
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }

        private static double Norm(double[] v) => Math.Sqrt(Dot(v, v));

        private static double MaxAbs(double[] v)
        {
            double max = 0.0;
            for (int i = 0; i < v.Length; i++)
            {
                double a = Math.Abs(v[i]);
                if (a > max) max = a;
            }
            return max;
        }
    }
}
