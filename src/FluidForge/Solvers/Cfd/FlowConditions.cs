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
            if (options.OperatingPoints != null && options.OperatingPoints.Count > 0)
            {
                float maxVelocity = 0f;
                for (int i = 0; i < options.OperatingPoints.Count; i++)
                {
                    OperatingPointOptions op = options.OperatingPoints[i];
                    if (op == null) continue;

                    float opVelocity;
                    if (op.Velocity.HasValue)
                    {
                        if (!float.IsFinite(op.Velocity.Value) || op.Velocity.Value <= 0f)
                        {
                            throw new InvalidOperationException(
                                $"{OutputConventions.ConfigTag} OperatingPoints[{i}] ('{op.Name}') has invalid Velocity '{op.Velocity.Value}'; must be finite and > 0 (or null to inherit FreestreamVelocity).");
                        }
                        opVelocity = op.Velocity.Value;
                    }
                    else
                    {
                        opVelocity = velocity;
                    }

                    maxVelocity = MathF.Max(maxVelocity, opVelocity);
                }

                if (maxVelocity > 0f)
                {
                    velocity = maxVelocity;
                    source = $"{source} (max over {options.OperatingPoints.Count} operating points)";
                }
            }

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
