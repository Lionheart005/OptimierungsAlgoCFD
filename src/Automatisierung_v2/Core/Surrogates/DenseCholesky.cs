using System;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Cholesky-Zerlegung für kleine, dichte, symmetrisch positiv definite Matrizen (TODO-54, F10).
    /// Bewusst eigener, kurzer Code statt einer Bibliothek: mehr braucht ein Gauß-Prozess nicht.
    ///
    /// <para>
    /// Matrizen werden als untere Dreiecksmatrix in "gezackten" Feldern abgelegt: Zeile <c>i</c> hat
    /// <c>i + 1</c> Einträge (<c>a[i][j]</c> mit <c>j ≤ i</c>). Das halbiert den Speicher und erlaubt, eine
    /// Zeile anzuhängen, ohne alles neu zu zerlegen.
    /// </para>
    /// </summary>
    public static class DenseCholesky
    {
        /// <summary>Die Leiter der Diagonalzuschläge, wenn die Zerlegung ohne Zuschlag scheitert.</summary>
        private static readonly double[] JitterLadder = { 0.0, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4 };

        /// <summary>
        /// Zerlegt <c>A + jitter·I = L·Lᵀ</c>. Scheitert die Zerlegung, wird der Zuschlag von 1e-8 in
        /// Zehnerschritten bis 1e-4 erhöht.
        /// </summary>
        /// <param name="lowerA">Untere Hälfte der Matrix (wird nicht verändert).</param>
        /// <param name="jitterUsed">Der schließlich benötigte Zuschlag (0 = keiner).</param>
        /// <exception cref="SurrogateFitException">Auch mit dem größten Zuschlag nicht zerlegbar.</exception>
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
                "Die Kovarianzmatrix ist selbst mit einem Diagonalzuschlag von 1e-4 nicht positiv definit "
                + "(doppelte Punkte oder nicht endliche Werte).");
        }

        /// <summary>Ein einzelner Zerlegungsversuch mit festem Zuschlag.</summary>
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

        /// <summary>Löst <c>L·x = b</c> (Vorwärtseinsetzen).</summary>
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

        /// <summary>Löst <c>Lᵀ·x = y</c> (Rückwärtseinsetzen).</summary>
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

        /// <summary>Löst <c>A·x = b</c> mit <c>A = L·Lᵀ</c>.</summary>
        public static double[] Solve(double[][] l, double[] b) => SolveUpper(l, SolveLower(l, b));

        /// <summary><c>ln det A = 2·Σ ln L_ii</c>.</summary>
        public static double LogDeterminant(double[][] l)
        {
            double sum = 0.0;
            for (int i = 0; i < l.Length; i++) sum += Math.Log(l[i][i]);
            return 2.0 * sum;
        }

        /// <summary>
        /// Die Inverse <c>A⁻¹</c> als untere Hälfte (symmetrisch). Gebraucht für den Gradienten der
        /// Log-Likelihood.
        /// </summary>
        public static double[][] Inverse(double[][] l)
        {
            int n = l.Length;

            // L⁻¹ (untere Dreiecksmatrix)
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
