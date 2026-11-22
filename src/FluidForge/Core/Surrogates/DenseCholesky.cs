using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Cholesky factorization for small, dense, symmetric positive-definite matrices.
    /// Intentionally self-contained without external linear-algebra dependencies.
    ///
    /// <para>
    /// Matrices are stored as lower-triangular jagged arrays: row <c>i</c> has <c>i + 1</c>
    /// entries (<c>a[i][j]</c> with <c>j ≤ i</c>). This halves memory usage and allows
    /// appending a row incrementally without re-factoring from scratch.
    /// </para>
    /// </summary>
    public static class DenseCholesky
    {
        /// <summary>Escalating diagonal jitter ladder if factorization without jitter fails.</summary>
        private static readonly double[] JitterLadder = { 0.0, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4 };

        /// <summary>
        /// Factors <c>A + jitter·I = L·Lᵀ</c>. If factorization fails, diagonal jitter is
        /// increased in powers of 10 from 1e-8 up to 1e-4.
        /// </summary>
        /// <param name="lowerA">Lower triangle of the matrix (not modified).</param>
        /// <param name="jitterUsed">Diagonal jitter ultimately required (0 = none).</param>
        /// <exception cref="SurrogateFitException">Matrix is not positive-definite even with maximum jitter.</exception>
        public static double[][] Factor(double[][] lowerA, out double jitterUsed)
        {
            foreach (double jitter in JitterLadder)
            {
                if (TryFactor(lowerA, jitter, out double[][]? l))
                {
                    jitterUsed = jitter;
                    return l!;
                }
            }

            throw new SurrogateFitException(
                "Covariance matrix is not positive-definite even with 1e-4 diagonal jitter "
                + "(duplicate points or non-finite values).");
        }

        /// <summary>Single factorization attempt with a fixed diagonal jitter.</summary>
        public static bool TryFactor(double[][] lowerA, double jitter, out double[][]? l)
        {
            int n = lowerA.Length;
            var result = new double[n][];

            for (int i = 0; i < n; i++)
            {
                var row = new double[i + 1];
                for (int j = 0; j <= i; j++)
                {
                    double sum = lowerA[i][j];
                    if (i == j) sum += jitter;

                    double[] rj = j < i ? result[j] : row;
                    for (int k = 0; k < j; k++) sum -= row[k] * rj[k];

                    if (i == j)
                    {
                        if (!(sum > 0.0) || double.IsNaN(sum) || double.IsInfinity(sum))
                        {
                            l = null;
                            return false;
                        }
                        row[j] = Math.Sqrt(sum);
                    }
                    else
                    {
                        row[j] = sum / result[j][j];
                    }
                }
                result[i] = row;
            }

            l = result;
            return true;
        }

        /// <summary>Solves <c>L·x = b</c> (forward substitution).</summary>
        public static double[] SolveLower(double[][] l, double[] b)
        {
            int n = l.Length;
            var x = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = b[i];
                double[] row = l[i];
                for (int k = 0; k < i; k++) sum -= row[k] * x[k];
                x[i] = sum / row[i];
            }
            return x;
        }

        /// <summary>Solves <c>Lᵀ·x = y</c> (backward substitution).</summary>
        public static double[] SolveUpper(double[][] l, double[] y)
        {
            int n = l.Length;
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = y[i];
                for (int k = i + 1; k < n; k++) sum -= l[k][i] * x[k];
                x[i] = sum / l[i][i];
            }
            return x;
        }

        /// <summary>Solves <c>A·x = b</c> where <c>A = L·Lᵀ</c>.</summary>
        public static double[] Solve(double[][] l, double[] b) => SolveUpper(l, SolveLower(l, b));

        /// <summary><c>ln det A = 2·Σ ln L_ii</c>.</summary>
        public static double LogDeterminant(double[][] l)
        {
            double sum = 0.0;
            for (int i = 0; i < l.Length; i++) sum += Math.Log(l[i][i]);
            return 2.0 * sum;
        }

        /// <summary>
        /// Computes <c>A⁻¹</c> as a lower-triangular jagged array (symmetric).
        /// Used for the log-marginal-likelihood gradient.
        /// </summary>
        public static double[][] Inverse(double[][] l)
        {
            int n = l.Length;

            // L⁻¹ (lower triangular)
            var linv = new double[n][];
            for (int i = 0; i < n; i++) linv[i] = new double[i + 1];

            for (int j = 0; j < n; j++)
            {
                linv[j][j] = 1.0 / l[j][j];
                for (int i = j + 1; i < n; i++)
                {
                    double sum = 0.0;
                    double[] li = l[i];
                    for (int k = j; k < i; k++) sum += li[k] * linv[k][j];
                    linv[i][j] = -sum / li[i];
                }
            }

            // A⁻¹ = L⁻ᵀ·L⁻¹
            var inverse = new double[n][];
            for (int i = 0; i < n; i++) inverse[i] = new double[i + 1];

            for (int k = 0; k < n; k++)
            {
                double[] lk = linv[k];
                for (int i = 0; i <= k; i++)
                {
                    double a = lk[i];
                    if (a == 0.0) continue;
                    double[] target = inverse[i];
                    for (int j = 0; j <= i; j++) target[j] += a * lk[j];
                }
            }

            return inverse;
        }
    }
}
