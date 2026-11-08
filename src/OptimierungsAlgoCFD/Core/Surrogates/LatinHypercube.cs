using System;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Latin-Hypercube-Stichprobe im Einheitswürfel: In jeder Dimension liegt in jedem der n gleich
    /// breiten Intervalle genau ein Punkt (TODO-54). Das ist der Versuchsplan (DoE) für den Anfang.
    /// </summary>
    public static class LatinHypercube
    {
        /// <summary>Anzahl der Zufallspläne, aus denen der mit dem größten Mindestabstand bleibt.</summary>
        public const int MaximinCandidates = 20;

        /// <summary>
        /// <paramref name="n"/> Punkte in <paramref name="d"/> Dimensionen. Aus
        /// <see cref="MaximinCandidates"/> Zufallsplänen wird der mit dem größten Mindestabstand
        /// zwischen zwei Punkten behalten (Maximin).
        /// </summary>
        public static double[][] Sample(int n, int d, Random random)
        {
            if (n <= 0 || d <= 0) return Array.Empty<double[]>();

            double[][]? best = null;
            double bestMinDistance = double.NegativeInfinity;

            for (int attempt = 0; attempt < MaximinCandidates; attempt++)
            {
                double[][] plan = SampleOnce(n, d, random);
                double minDistance = MinimumDistance(plan);
                if (minDistance > bestMinDistance)
                {
                    bestMinDistance = minDistance;
                    best = plan;
                }
            }

            return best!;
        }

        private static double[][] SampleOnce(int n, int d, Random random)
        {
            var points = new double[n][];
            for (int i = 0; i < n; i++) points[i] = new double[d];

            var permutation = new int[n];
            for (int dim = 0; dim < d; dim++)
            {
                for (int i = 0; i < n; i++) permutation[i] = i;
                for (int i = n - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
                }

                for (int i = 0; i < n; i++)
                    points[i][dim] = (permutation[i] + random.NextDouble()) / n;
            }

            return points;
        }

        private static double MinimumDistance(double[][] points)
        {
            if (points.Length < 2) return double.PositiveInfinity;

            double min = double.PositiveInfinity;
            for (int i = 0; i < points.Length; i++)
            {
                for (int j = i + 1; j < points.Length; j++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < points[i].Length; k++)
                    {
                        double diff = points[i][k] - points[j][k];
                        sum += diff * diff;
                    }
                    if (sum < min) min = sum;
                }
            }
            return min;
        }
    }
}
