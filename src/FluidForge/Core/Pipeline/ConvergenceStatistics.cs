using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Shared statistical helpers for solver convergence assessment (batch-mean trend tests,
    /// tail-mean standard uncertainty, and detrended oscillation amplitude).
    /// </summary>
    public static class ConvergenceStatistics
    {
        /// <summary>Number of batches for batch-mean statistics (trend test and uncertainty).</summary>
        public const int Batches = 8;

        /// <summary>
        /// Batch-mean statistics: splits the window into <see cref="Batches"/> equal-length batches and fits
        /// a line through batch means. <paramref name="significant"/> is true if the slope exceeds 3x its
        /// standard error (statistically significant drift). <paramref name="standardError"/> is the standard
        /// error of the window mean from batch scatter around the trend line.
        /// </summary>
        public static bool BatchStatistics(IReadOnlyList<float> values, out double standardError, out bool significant)
        {
            standardError = double.NaN;
            significant = false;
            if (values == null) return false;

            int size = values.Count / Batches;
            if (size < 2) return false;

            var means = new double[Batches];
            int offset = values.Count - size * Batches; // Drop leading remainder (startup)
            for (int b = 0; b < Batches; b++)
            {
                double sum = 0;
                for (int i = 0; i < size; i++) sum += values[offset + b * size + i];
                means[b] = sum / size;
            }

            double meanX = (Batches - 1) / 2.0, meanY = means.Average();
            double sxy = 0, sxx = 0;
            for (int b = 0; b < Batches; b++)
            {
                sxy += (b - meanX) * (means[b] - meanY);
                sxx += (b - meanX) * (b - meanX);
            }
            double slope = sxy / sxx;

            double ss = 0;
            for (int b = 0; b < Batches; b++)
            {
                double r = means[b] - (meanY + slope * (b - meanX));
                ss += r * r;
            }
            double residualSd = Math.Sqrt(ss / (Batches - 2));

            // Floor against division by zero on exact linear or constant histories
            double floor = 1e-9 * Math.Max(Math.Abs(meanY), 1e-12);
            double slopeError = Math.Max(residualSd, floor) / Math.Sqrt(sxx);

            significant = Math.Abs(slope) > 3.0 * slopeError && Math.Abs(slope) * (Batches - 1) > floor * 10;
            standardError = residualSd / Math.Sqrt(Batches);
            return true;
        }

        /// <summary>Peak-to-peak range of residuals from the least-squares trend line.</summary>
        public static float DetrendedPeakToPeak(IReadOnlyList<float> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            int n = values.Count;
            if (n == 0) return 0f;

            double meanX = (n - 1) / 2.0;
            double meanY = 0;
            for (int i = 0; i < n; i++) meanY += values[i];
            meanY /= n;

            double sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++)
            {
                sxy += (i - meanX) * (values[i] - meanY);
                sxx += (i - meanX) * (i - meanX);
            }
            double slope = sxx > 0 ? sxy / sxx : 0.0;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double residual = values[i] - (meanY + slope * (i - meanX));
                if (residual < min) min = residual;
                if (residual > max) max = residual;
            }
            return (float)(max - min);
        }
    }
}
