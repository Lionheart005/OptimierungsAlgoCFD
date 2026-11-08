using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Gauß-Prozess (Kriging) mit Matérn-5/2-Kern, einer Längenskala je Parameter (ARD) und gelerntem
    /// Rauschen (TODO-54, F15). Rein in C#, ohne fremde Bibliothek (F10).
    ///
    /// <para>
    /// Kern: <c>k = σ²·(1 + √5·r + 5/3·r²)·exp(−√5·r)</c> mit <c>r² = Σ ((x_i − x'_i) / ℓ_i)²</c>.
    /// Die Ausgaben werden intern standardisiert (Mittelwert 0, Streuung 1). Die Hyperparameter
    /// (ln ℓ_i, ln σ², ln σ_n²) werden über die Log-Marginal-Likelihood mit analytischem Gradienten
    /// bestimmt (Adam, mehrere Starts, Schranken ℓ ∈ [0,01; 20], σ² ∈ [0,05; 20], σ_n² ∈ [1e-6; 0,5]).
    /// </para>
    /// </summary>
    public sealed class GaussianProcess : ISurrogateModel
    {
        private const double MinLength = 0.01, MaxLength = 20.0;
        private const double MinSignal = 0.05, MaxSignal = 20.0;
        private const double MinNoise = 1e-6, MaxNoise = 0.5;
        private const double Sqrt5 = 2.2360679774997896964;

        private readonly Random _random;

        // Daten (Eingaben unverändert, Ausgaben standardisiert)
        private readonly List<double[]> _inputs = new();
        private readonly List<double> _outputs = new();
        private readonly List<double> _extraNoise = new();   // bekannte Unsicherheit je Punkt, standardisiert
        private double _outputMean;
        private double _outputScale = 1.0;
        private int _dimension;

        // Hyperparameter im Log-Raum: [ln ℓ_0 … ln ℓ_{d-1}, ln σ², ln σ_n²]
        private double[]? _theta;

        // Zerlegung
        private List<double[]> _chol = new();
        private double[][] _cholArray = Array.Empty<double[]>();
        private double[] _alpha = Array.Empty<double>();

        /// <param name="random">Zufallsquelle für die zufälligen Startpunkte der Hyperparameter-Suche.</param>
        public GaussianProcess(Random? random = null)
        {
            _random = random ?? new Random(12345);
        }

        /// <summary>Anzahl der Lernpunkte (einschließlich Scheinbeobachtungen).</summary>
        public int Count => _inputs.Count;

        /// <summary>Gelernte Längenskalen ℓ_i (normierte Einheiten), <c>null</c> vor dem ersten Lernen.</summary>
        public IReadOnlyList<double>? LengthScales =>
            _theta == null ? null : _theta.Take(_dimension).Select(Math.Exp).ToArray();

        /// <summary>Gelernte Signalvarianz σ² (standardisierte Einheiten).</summary>
        public double SignalVariance => _theta == null ? double.NaN : Math.Exp(_theta[_dimension]);

        /// <summary>Gelerntes Rauschen σ_n² (standardisierte Einheiten).</summary>
        public double NoiseVariance => _theta == null ? double.NaN : Math.Exp(_theta[_dimension + 1]);

        /// <inheritdoc/>
        public IReadOnlyList<double>? ParameterRelevance
        {
            get
            {
                if (_theta == null) return null;
                // (1/ℓ_i) / max_j (1/ℓ_j)  =  min ℓ / ℓ_i
                double minLength = double.PositiveInfinity;
                for (int i = 0; i < _dimension; i++) minLength = Math.Min(minLength, Math.Exp(_theta[i]));
                var relevance = new double[_dimension];
                for (int i = 0; i < _dimension; i++) relevance[i] = minLength / Math.Exp(_theta[i]);
                return relevance;
            }
        }

        /// <inheritdoc/>
        public void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs, IReadOnlyList<double>? noiseVariances = null)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (inputs.Count != outputs.Count)
                throw new ArgumentException($"Eingaben ({inputs.Count}) und Ausgaben ({outputs.Count}) sind verschieden viele.");
            if (inputs.Count == 0)
                throw new ArgumentException("Zum Lernen wird mindestens ein Datenpunkt gebraucht.");

            int d = inputs[0].Length;
            if (d == 0) throw new ArgumentException("Die Eingaben haben keine Dimension.");

            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i].Length != d)
                    throw new ArgumentException($"Eingabe {i} hat {inputs[i].Length} Dimensionen, erwartet {d}.");
                if (inputs[i].Any(v => !double.IsFinite(v)))
                    throw new ArgumentException($"Eingabe {i} enthält einen nicht endlichen Wert.");
                if (!double.IsFinite(outputs[i]))
                    throw new ArgumentException($"Ausgabe {i} ist nicht endlich ({outputs[i]}).");
            }

            if (noiseVariances != null)
            {
                if (noiseVariances.Count != inputs.Count)
                    throw new ArgumentException($"Unsicherheiten ({noiseVariances.Count}) und Eingaben ({inputs.Count}) sind verschieden viele.");
                if (noiseVariances.Any(v => !double.IsFinite(v) || v < 0))
                    throw new ArgumentException("Eine Unsicherheit ist negativ oder nicht endlich.");
            }

            if (_theta != null && _dimension != d) _theta = null;
            _dimension = d;

            _inputs.Clear();
            _outputs.Clear();
            foreach (double[] x in inputs) _inputs.Add((double[])x.Clone());

            // Standardisieren
            double mean = outputs.Average();
            double variance = outputs.Sum(v => (v - mean) * (v - mean)) / outputs.Count;
            double scale = Math.Sqrt(variance);
            if (!(scale > 1e-12)) scale = 1.0;
            _outputMean = mean;
            _outputScale = scale;
            foreach (double y in outputs) _outputs.Add((y - mean) / scale);
            _extraNoise.Clear();
            for (int i = 0; i < outputs.Count; i++)
                _extraNoise.Add(noiseVariances != null ? noiseVariances[i] / (scale * scale) : 0.0);

            _theta = OptimizeHyperparameters();
            Refactor();
        }

        /// <inheritdoc/>
        public (double Mean, double Variance) Predict(double[] input)
        {
            if (_theta == null || _inputs.Count == 0)
                throw new InvalidOperationException("Das Modell wurde noch nicht gelernt (Fit).");

            double sf2 = Math.Exp(_theta[_dimension]);
            var kstar = new double[_inputs.Count];
            for (int i = 0; i < kstar.Length; i++)
                kstar[i] = Kernel(input, _inputs[i], _theta, sf2);

            double meanStd = 0.0;
            for (int i = 0; i < kstar.Length; i++) meanStd += kstar[i] * _alpha[i];

            double[] v = DenseCholesky.SolveLower(_cholArray, kstar);
            double reduction = 0.0;
            for (int i = 0; i < v.Length; i++) reduction += v[i] * v[i];

            double varStd = Math.Max(sf2 - reduction, 1e-12);
            return (meanStd * _outputScale + _outputMean, varStd * _outputScale * _outputScale);
        }

        /// <inheritdoc/>
        public void AddPseudoObservation(double[] input, double output)
        {
            if (_theta == null || _inputs.Count == 0)
                throw new InvalidOperationException("Das Modell wurde noch nicht gelernt (Fit).");
            if (!double.IsFinite(output) || input.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("Scheinbeobachtung mit nicht endlichem Wert.");

            double sf2 = Math.Exp(_theta[_dimension]);
            double sn2 = Math.Exp(_theta[_dimension + 1]);

            var k = new double[_inputs.Count];
            for (int i = 0; i < k.Length; i++) k[i] = Kernel(input, _inputs[i], _theta, sf2);

            double[] l = DenseCholesky.SolveLower(_cholArray, k);
            double dot = 0.0;
            for (int i = 0; i < l.Length; i++) dot += l[i] * l[i];

            double schur = Math.Max(sf2 + sn2 - dot, 1e-12);

            var row = new double[l.Length + 1];
            Array.Copy(l, row, l.Length);
            row[l.Length] = Math.Sqrt(schur);

            _inputs.Add((double[])input.Clone());
            _outputs.Add((output - _outputMean) / _outputScale);
            _extraNoise.Add(0.0);
            _chol.Add(row);
            _cholArray = _chol.ToArray();
            _alpha = DenseCholesky.Solve(_cholArray, _outputs.ToArray());
        }

        // ------------------------------------------------------------------------------------
        // Kern
        // ------------------------------------------------------------------------------------

        private static double Kernel(double[] a, double[] b, double[] theta, double sf2)
        {
            double r2 = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double diff = (a[i] - b[i]) / Math.Exp(theta[i]);
                r2 += diff * diff;
            }
            return Matern52(r2, sf2);
        }

        private static double Matern52(double r2, double sf2)
        {
            double s = Sqrt5 * Math.Sqrt(r2);
            return sf2 * (1.0 + s + 5.0 / 3.0 * r2) * Math.Exp(-s);
        }

        private void Refactor()
        {
            double sf2 = Math.Exp(_theta![_dimension]);
            double sn2 = Math.Exp(_theta[_dimension + 1]);
            int n = _inputs.Count;

            var k = new double[n][];
            double[] lengths = new double[_dimension];
            for (int i = 0; i < _dimension; i++) lengths[i] = Math.Exp(_theta[i]);

            for (int i = 0; i < n; i++)
            {
                k[i] = new double[i + 1];
                for (int j = 0; j < i; j++) k[i][j] = KernelFast(_inputs[i], _inputs[j], lengths, sf2);
                k[i][i] = sf2 + sn2 + _extraNoise[i];
            }

            double[][] l = DenseCholesky.Factor(k, out _);
            _chol = new List<double[]>(l);
            _cholArray = l;
            _alpha = DenseCholesky.Solve(l, _outputs.ToArray());
        }

        private static double KernelFast(double[] a, double[] b, double[] lengths, double sf2)
        {
            double r2 = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double diff = (a[i] - b[i]) / lengths[i];
                r2 += diff * diff;
            }
            return Matern52(r2, sf2);
        }

        // ------------------------------------------------------------------------------------
        // Log-Marginal-Likelihood und Gradient
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Log-Marginal-Likelihood der (bereits standardisierten) Ausgaben <paramref name="y"/> für die
        /// Hyperparameter <paramref name="theta"/> (Log-Raum). Öffentlich, damit der analytische Gradient
        /// gegen finite Differenzen geprüft werden kann.
        /// </summary>
        /// <param name="gradient">Wird gefüllt, wenn nicht <c>null</c> (Länge = theta.Length).</param>
        /// <param name="extraNoise">Bekannte Varianz je Punkt (standardisiert), kommt auf die Diagonale.</param>
        /// <returns><see cref="double.NegativeInfinity"/>, wenn die Matrix nicht zerlegbar ist.</returns>
        public static double LogMarginalLikelihood(
            IReadOnlyList<double[]> x, IReadOnlyList<double> y, double[] theta, double[]? gradient,
            IReadOnlyList<double>? extraNoise = null)
        {
            int n = x.Count;
            int d = theta.Length - 2;
            double sf2 = Math.Exp(theta[d]);
            double sn2 = Math.Exp(theta[d + 1]);
            var lengths = new double[d];
            for (int i = 0; i < d; i++) lengths[i] = Math.Exp(theta[i]);

            var k = new double[n][];
            // Hilfsgröße des Längenskalen-Gradienten: (5/3)·σ²·(1 + s)·exp(−s)
            double[][]? g = gradient != null ? new double[n][] : null;

            for (int i = 0; i < n; i++)
            {
                k[i] = new double[i + 1];
                if (g != null) g[i] = new double[i + 1];
                for (int j = 0; j < i; j++)
                {
                    double r2 = 0.0;
                    for (int m = 0; m < d; m++)
                    {
                        double diff = (x[i][m] - x[j][m]) / lengths[m];
                        r2 += diff * diff;
                    }
                    double s = Sqrt5 * Math.Sqrt(r2);
                    double e = Math.Exp(-s);
                    k[i][j] = sf2 * (1.0 + s + 5.0 / 3.0 * r2) * e;
                    if (g != null) g[i][j] = 5.0 / 3.0 * sf2 * (1.0 + s) * e;
                }
                k[i][i] = sf2 + sn2 + (extraNoise != null ? extraNoise[i] : 0.0);
            }

            double[][] l;
            try
            {
                l = DenseCholesky.Factor(k, out _);
            }
            catch (SurrogateFitException)
            {
                return double.NegativeInfinity;
            }

            double[] yv = y.ToArray();
            double[] alpha = DenseCholesky.Solve(l, yv);

            double fit = 0.0;
            for (int i = 0; i < n; i++) fit += yv[i] * alpha[i];
            double logLik = -0.5 * fit - 0.5 * DenseCholesky.LogDeterminant(l) - 0.5 * n * Math.Log(2.0 * Math.PI);

            if (gradient != null)
            {
                double[][] kinv = DenseCholesky.Inverse(l);
                Array.Clear(gradient, 0, gradient.Length);

                double gSignal = 0.0;
                double gNoise = 0.0;

                for (int i = 0; i < n; i++)
                {
                    double wii = alpha[i] * alpha[i] - kinv[i][i];
                    gSignal += 0.5 * wii * sf2;
                    gNoise += 0.5 * wii * sn2;

                    for (int j = 0; j < i; j++)
                    {
                        double w = alpha[i] * alpha[j] - kinv[i][j];
                        // Beitrag von (i,j) und (j,i), mal ½ aus der Formel → Faktor 1
                        double kSignal = k[i][j];
                        gSignal += w * kSignal;

                        double wg = w * g![i][j];
                        for (int m = 0; m < d; m++)
                        {
                            double t = (x[i][m] - x[j][m]) / lengths[m];
                            gradient[m] += wg * t * t;
                        }
                    }
                }

                gradient[d] = gSignal;
                gradient[d + 1] = gNoise;
            }

            return logLik;
        }

        // ------------------------------------------------------------------------------------
        // Hyperparameter-Suche
        // ------------------------------------------------------------------------------------

        private double[] OptimizeHyperparameters()
        {
            int n = _inputs.Count;
            int d = _dimension;

            double lengthStart = Math.Clamp(0.3 * Math.Sqrt(d), 0.5, 5.0);
            double[] defaultStart = DefaultTheta(d, lengthStart);

            // Mit weniger als 3 Punkten lässt sich nichts Sinnvolles lernen.
            if (n < 3) return _theta != null ? (double[])_theta.Clone() : defaultStart;

            var starts = new List<double[]>();
            if (_theta != null) starts.Add((double[])_theta.Clone());
            starts.Add(defaultStart);

            // Viele Punkte: nur Warmstart und Vorgabe, wenige Schritte (Rechenzeit ~ n³ je Schritt).
            int steps = n <= 100 ? 200 : n <= 200 ? 100 : 40;
            if (n <= 150)
            {
                for (int s = 0; s < 2; s++)
                {
                    var t = new double[d + 2];
                    for (int i = 0; i < d; i++) t[i] = Math.Log(0.1) + _random.NextDouble() * (Math.Log(5.0) - Math.Log(0.1));
                    t[d] = Math.Log(0.3) + _random.NextDouble() * (Math.Log(5.0) - Math.Log(0.3));
                    t[d + 1] = Math.Log(1e-4) + _random.NextDouble() * (Math.Log(0.1) - Math.Log(1e-4));
                    starts.Add(Clamp(t, d));
                }
            }
            else if (_theta != null)
            {
                starts.RemoveAt(starts.Count - 1);   // nur Warmstart
            }

            var results = new (double[] Theta, double LogLik)[starts.Count];
            if (n >= 40 && starts.Count > 1)
            {
                Parallel.For(0, starts.Count, i => results[i] = RunAdam(starts[i], steps));
            }
            else
            {
                for (int i = 0; i < starts.Count; i++) results[i] = RunAdam(starts[i], steps);
            }

            int best = -1;
            for (int i = 0; i < results.Length; i++)
            {
                if (double.IsNaN(results[i].LogLik) || double.IsNegativeInfinity(results[i].LogLik)) continue;
                if (best < 0 || results[i].LogLik > results[best].LogLik) best = i;
            }

            if (best < 0) return _theta != null ? (double[])_theta.Clone() : defaultStart;
            return results[best].Theta;
        }

        private (double[] Theta, double LogLik) RunAdam(double[] start, int steps)
        {
            int d = _dimension;
            int p = d + 2;
            double[] theta = Clamp((double[])start.Clone(), d);
            double[] bestTheta = (double[])theta.Clone();
            double bestLogLik = double.NegativeInfinity;

            var m = new double[p];
            var v = new double[p];
            var grad = new double[p];
            const double lr = 0.05, beta1 = 0.9, beta2 = 0.999, eps = 1e-8;

            for (int step = 1; step <= steps; step++)
            {
                double logLik = LogMarginalLikelihood(_inputs, _outputs, theta, grad, _extraNoise);

                if (double.IsNegativeInfinity(logLik) || double.IsNaN(logLik) || grad.Any(g => !double.IsFinite(g)))
                {
                    if (double.IsNegativeInfinity(bestLogLik)) break;
                    // Zurück zur Mitte zwischen aktuellem und bestem Punkt, Momente zurücksetzen.
                    for (int i = 0; i < p; i++) theta[i] = 0.5 * (theta[i] + bestTheta[i]);
                    Array.Clear(m); Array.Clear(v);
                    continue;
                }

                if (logLik > bestLogLik)
                {
                    bestLogLik = logLik;
                    Array.Copy(theta, bestTheta, p);
                }

                for (int i = 0; i < p; i++)
                {
                    m[i] = beta1 * m[i] + (1 - beta1) * grad[i];
                    v[i] = beta2 * v[i] + (1 - beta2) * grad[i] * grad[i];
                    double mHat = m[i] / (1 - Math.Pow(beta1, step));
                    double vHat = v[i] / (1 - Math.Pow(beta2, step));
                    theta[i] += lr * mHat / (Math.Sqrt(vHat) + eps);
                }
                Clamp(theta, d);
            }

            // Der letzte Schritt wurde noch nicht bewertet.
            double finalLogLik = LogMarginalLikelihood(_inputs, _outputs, theta, null, _extraNoise);
            if (finalLogLik > bestLogLik)
            {
                bestLogLik = finalLogLik;
                Array.Copy(theta, bestTheta, p);
            }

            return (bestTheta, bestLogLik);
        }

        private static double[] DefaultTheta(int d, double length)
        {
            var theta = new double[d + 2];
            for (int i = 0; i < d; i++) theta[i] = Math.Log(length);
            theta[d] = Math.Log(1.0);
            theta[d + 1] = Math.Log(0.01);
            return theta;
        }

        private static double[] Clamp(double[] theta, int d)
        {
            for (int i = 0; i < d; i++) theta[i] = Math.Clamp(theta[i], Math.Log(MinLength), Math.Log(MaxLength));
            theta[d] = Math.Clamp(theta[d], Math.Log(MinSignal), Math.Log(MaxSignal));
            theta[d + 1] = Math.Clamp(theta[d + 1], Math.Log(MinNoise), Math.Log(MaxNoise));
            return theta;
        }
    }
}
