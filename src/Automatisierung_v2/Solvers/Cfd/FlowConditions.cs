using System;
using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// Physikalische Strömungsbedingungen (Geschwindigkeit, Dichte, Viskosität), die für
    /// Vernetzer (Grenzschichtberechnung, TODO-40) und Solver (SU2) identisch sein müssen (TODO-38).
    /// </summary>
    /// <param name="Velocity">Anströmgeschwindigkeit in m/s.</param>
    /// <param name="Density">Fluiddichte in kg/m³.</param>
    /// <param name="DynamicViscosity">Dynamische Viskosität in Pa·s (kg/(m·s)).</param>
    /// <param name="VelocitySource">Klartext für das Log: woher die Geschwindigkeit stammt.</param>
    public sealed record FlowConditions(
        float Velocity,
        float Density,
        float DynamicViscosity,
        string VelocitySource = "")
    {
        /// <summary>
        /// Erzeugt die Strömungsbedingungen aus der Simulationskonfiguration und den SU2-Optionen.
        /// Löst die Geschwindigkeit über <see cref="Su2ConfigGenerator.ResolveVelocity"/> auf.
        /// </summary>
        public static FlowConditions From(SimulationConfig config, Su2SolverOptions options)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (options == null) throw new ArgumentNullException(nameof(options));

            float velocity = Su2ConfigGenerator.ResolveVelocity(config, options, out string source);
            return new FlowConditions(velocity, options.Density, options.DynamicViscosity, source);
        }

        /// <summary>
        /// Berechnet die Reynoldszahl Re_L = (Dichte × Geschwindigkeit × L) / Viskosität.
        /// Gibt 0 zurück, falls Viskosität &lt;= 0 oder die Länge &lt;= 0 ist.
        /// </summary>
        /// <param name="characteristicLengthMeters">Charakteristische Länge L in Metern.</param>
        public float ComputeReynoldsNumber(float characteristicLengthMeters)
        {
            if (DynamicViscosity <= 0f || characteristicLengthMeters <= 0f || Velocity <= 0f || Density <= 0f)
                return 0f;

            return Density * Velocity * characteristicLengthMeters / DynamicViscosity;
        }
    }
}
