using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// A trainable surrogate model that predicts a scalar quantity from a few real evaluations.
    /// The Bayesian optimizer depends only on this interface. <see cref="GaussianProcess"/> is the
    /// default implementation; custom models can be plugged in without touching the optimizer.
    /// </summary>
    public interface ISurrogateModel
    {
        /// <summary>Replaces all training data and refits the model.</summary>
        /// <param name="noiseVariances">
        /// Optional known per-point measurement uncertainty as variance in output units (e.g., from
        /// unsteady solver fluctuations). Added to the learned shared noise. <c>null</c> = unknown.
        /// </param>
        /// <exception cref="ArgumentException">Mismatched lengths or non-finite values.</exception>
        /// <exception cref="SurrogateFitException">Model fitting failed numerically.</exception>
        void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs, IReadOnlyList<double>? noiseVariances = null);

        /// <summary>Predicted mean and <b>latent</b> variance (excluding observation noise, for EI, Kriging Believer, and explorer) at <paramref name="input"/>.</summary>
        (double Mean, double Variance) Predict(double[] input);

        /// <summary>
        /// Learned observation noise variance σ_n² in original output units (0 if unknown).
        /// </summary>
        double ObservationNoiseVariance => 0.0;

        /// <summary>
        /// Adds a pseudo-observation without re-optimizing hyperparameters. Used for batch proposals
        /// (Kriging Believer) so predicted uncertainty decreases at already-chosen locations.
        /// </summary>
        void AddPseudoObservation(double[] input, double output);

        /// <summary>Relative importance per dimension in [0, 1] (1 = most important). <c>null</c> if unavailable.</summary>
        IReadOnlyList<double>? ParameterRelevance { get; }
    }

    /// <summary>
    /// Surrogate fitting failed numerically (e.g., non-positive-definite covariance matrix).
    /// The optimizer catches this and falls back to Latin Hypercube points for that iteration.
    /// </summary>
    public sealed class SurrogateFitException : Exception
    {
        public SurrogateFitException(string message) : base(message) { }
        public SurrogateFitException(string message, Exception inner) : base(message, inner) { }
    }
}
