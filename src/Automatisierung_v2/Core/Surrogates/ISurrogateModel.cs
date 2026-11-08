using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Ein lernendes Ersatzmodell, das aus wenigen echten Rechnungen eine Größe vorhersagt
    /// (TODO-54, F11). Der Bayes-Optimierer kennt nur diese Schnittstelle. Der Gauß-Prozess
    /// (<see cref="GaussianProcess"/>) ist die erste Umsetzung; ein anderes Modell, auch das eines
    /// Partners, lässt sich einstecken, ohne den Optimierer anzufassen.
    /// </summary>
    public interface ISurrogateModel
    {
        /// <summary>Ersetzt alle Daten und lernt neu.</summary>
        /// <param name="noiseVariances">
        /// Optional: bekannte Messunsicherheit je Punkt als Varianz in Ausgabeeinheiten (z. B. aus der
        /// Schwankung einer instationären Rechnung). Sie kommt zum gelernten, gemeinsamen Rauschen hinzu.
        /// <c>null</c> = unbekannt.
        /// </param>
        /// <exception cref="ArgumentException">Ungleich lange oder nicht endliche Daten.</exception>
        /// <exception cref="SurrogateFitException">Das Lernen ist numerisch gescheitert.</exception>
        void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs, IReadOnlyList<double>? noiseVariances = null);

        /// <summary>Erwartungswert und <b>latente</b> Varianz (ohne Messrauschen) am Ort <paramref name="input"/>.</summary>
        (double Mean, double Variance) Predict(double[] input);

        /// <summary>
        /// Trägt eine Scheinbeobachtung ein, ohne die Hyperparameter neu zu lernen. Gebraucht für
        /// Vorschlags-Blöcke (Kriging Believer): Nach einem Vorschlag sinkt die Unsicherheit dort.
        /// </summary>
        void AddPseudoObservation(double[] input, double output);

        /// <summary>Wichtigkeit je Dimension, 0..1 (1 = wichtigste). <c>null</c> = unbekannt.</summary>
        IReadOnlyList<double>? ParameterRelevance { get; }
    }

    /// <summary>
    /// Das Lernen des Ersatzmodells ist numerisch gescheitert (z. B. nicht zerlegbare Matrix).
    /// Der Optimierer fängt das ab und füllt die Iteration mit Latin-Hypercube-Punkten (F22).
    /// </summary>
    public sealed class SurrogateFitException : Exception
    {
        public SurrogateFitException(string message) : base(message) { }
        public SurrogateFitException(string message, Exception inner) : base(message, inner) { }
    }
}
