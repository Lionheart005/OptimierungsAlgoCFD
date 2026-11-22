using System;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Physical flow conditions (velocity, density, viscosity) that must match between
    /// the mesher (boundary-layer calculation) and solver (SU2).
    /// </summary>
    /// <param name="Velocity">Freestream velocity in m/s.</param>
    /// <param name="Density">Fluid density in kg/m³.</param>
    /// <param name="DynamicViscosity">Dynamic viscosity in Pa·s (kg/(m·s)).</param>
    /// <param name="VelocitySource">Human-readable log label indicating where the velocity originated.</param>
    public sealed record FlowConditions(
        float Velocity,
        float Density,
        float DynamicViscosity,
        string VelocitySource = "")
    {
        /// <summary>
        /// Creates flow conditions from the simulation configuration and SU2 options.
        /// Resolves velocity via <see cref="Su2ConfigGenerator.ResolveVelocity"/>.
        /// </summary>
        public static FlowConditions From(SimulationConfig config, Su2SolverOptions options)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (options == null) throw new ArgumentNullException(nameof(options));

            float velocity = Su2ConfigGenerator.ResolveVelocity(config, options, out string source);
            return new FlowConditions(velocity, options.Density, options.DynamicViscosity, source);
        }

        /// <summary>
        /// Computes the Reynolds number Re_L = (density * velocity * L) / viscosity.
        /// Returns 0 if viscosity &lt;= 0 or length &lt;= 0.
        /// </summary>
        /// <param name="characteristicLengthMeters">Characteristic length L in meters.</param>
        public float ComputeReynoldsNumber(float characteristicLengthMeters)
        {
            if (DynamicViscosity <= 0f || characteristicLengthMeters <= 0f || Velocity <= 0f || Density <= 0f)
                return 0f;

            return Density * Velocity * characteristicLengthMeters / DynamicViscosity;
        }
    }
}
