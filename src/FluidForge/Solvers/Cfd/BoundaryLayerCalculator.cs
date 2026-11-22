using System;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Computed boundary-layer parameters according to Schlichting / Prandtl boundary-layer theory
    /// for a turbulent boundary layer on a flat plate.
    /// </summary>
    /// <param name="ReynoldsNumber">Reynolds number Re_L based on characteristic length L.</param>
    /// <param name="SkinFrictionCoeff">Skin-friction coefficient Cf = 0.026 * Re_L^(-1/7).</param>
    /// <param name="WallShearStress">Wall shear stress tau_w = 0.5 * rho * U^2 * Cf in Pa.</param>
    /// <param name="FrictionVelocity">Friction velocity u_tau = sqrt(tau_w / rho) in m/s.</param>
    /// <param name="FirstLayerHeightMm">First cell height y1 = y+ * mu / (rho * u_tau) in mm.</param>
    /// <param name="BoundaryLayerThicknessMm">Turbulent boundary-layer thickness delta = 0.37 * L * Re_L^(-0.2) in mm.</param>
    /// <param name="LayerCount">Number of boundary layers N.</param>
    /// <param name="TotalThicknessMm">Total thickness of all N layers in mm.</param>
    /// <param name="GrowthRate">Growth rate r between two consecutive layers.</param>
    /// <param name="TargetYPlus">Target y+ value.</param>
    public sealed record BoundaryLayerSpec(
        float ReynoldsNumber,
        float SkinFrictionCoeff,
        float WallShearStress,
        float FrictionVelocity,
        float FirstLayerHeightMm,
        float BoundaryLayerThicknessMm,
        int LayerCount,
        float TotalThicknessMm,
        float GrowthRate,
        float TargetYPlus);

    /// <summary>
    /// Pure, stateless calculator for sizing structured boundary-layer meshes.
    /// </summary>
    public static class BoundaryLayerCalculator
    {
        /// <summary>
        /// Computes boundary-layer parameters for a turbulent flat-plate boundary layer
        /// to achieve the target y+ value at the wall.
        /// </summary>
        /// <param name="density">Fluid density rho in kg/m³ (> 0).</param>
        /// <param name="velocity">Freestream velocity U in m/s (> 0).</param>
        /// <param name="dynamicViscosity">Dynamic viscosity mu in Pa·s (> 0).</param>
        /// <param name="characteristicLengthMm">Characteristic part length L in mm (> 0).</param>
        /// <param name="targetYPlus">Target y+ for the first cell height (> 0, default 1.0).</param>
        /// <param name="growthRate">Layer-to-layer growth ratio (> 1.0, default 1.2).</param>
        /// <param name="minLayers">Minimum number of layers (>= 1, default 4).</param>
        /// <param name="fixedLayers">Fixed layer count; 0 = compute automatically from boundary-layer thickness delta.</param>
        /// <param name="coverage">Factor on boundary-layer thickness to cover (default 1.0 = exact δ, literature often 1.2–1.5).</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for physically invalid inputs.</exception>
        public static BoundaryLayerSpec Calculate(
            float density,
            float velocity,
            float dynamicViscosity,
            float characteristicLengthMm,
            float targetYPlus = 1.0f,
            float growthRate = 1.2f,
            int minLayers = 4,
            int fixedLayers = 0,
            float coverage = 1.0f)
        {
            if (density <= 0f)
                throw new ArgumentOutOfRangeException(nameof(density), "Density must be positive (> 0).");
            if (velocity <= 0f)
                throw new ArgumentOutOfRangeException(nameof(velocity), "Velocity must be positive (> 0).");
            if (dynamicViscosity <= 0f)
                throw new ArgumentOutOfRangeException(nameof(dynamicViscosity), "Dynamic viscosity must be positive (> 0).");
            if (characteristicLengthMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(characteristicLengthMm), "Characteristic length must be positive (> 0).");
            if (targetYPlus <= 0f)
                throw new ArgumentOutOfRangeException(nameof(targetYPlus), "Target y+ must be positive (> 0).");
            if (growthRate <= 1.0f)
                throw new ArgumentOutOfRangeException(nameof(growthRate), "Growth rate must be greater than 1.0.");
            if (minLayers < 1)
                throw new ArgumentOutOfRangeException(nameof(minLayers), "Minimum layer count must be at least 1.");
            if (fixedLayers < 0)
                throw new ArgumentOutOfRangeException(nameof(fixedLayers), "fixedLayers must not be negative.");
            if (coverage <= 0f)
                throw new ArgumentOutOfRangeException(nameof(coverage), "coverage must be positive (> 0).");

            double rho = density;
            double u = velocity;
            double mu = dynamicViscosity;
            double l = characteristicLengthMm * 0.001; // mm to m
            double yPlus = targetYPlus;
            double r = growthRate;

            // Re_L = rho * U * L / mu
            double re = (rho * u * l) / mu;

            // Cf = 0.026 * Re_L^(-1/7) (turbulent boundary layer, flat plate)
            double cf = 0.026 * Math.Pow(re, -1.0 / 7.0);

            // tau_w = 0.5 * rho * U^2 * Cf,  u_tau = sqrt(tau_w / rho)
            double tauW = 0.5 * rho * u * u * cf;
            double uTau = Math.Sqrt(tauW / rho);

            // y1 = y+ * mu / (rho * u_tau) in meters
            double y1M = (yPlus * mu) / (rho * uTau);
            double y1Mm = y1M * 1000.0;

            // delta = 0.37 * L * Re_L^(-0.2) in meters
            double deltaM = 0.37 * l * Math.Pow(re, -0.2);
            double deltaMm = deltaM * 1000.0;

            int n;
            if (fixedLayers > 0)
            {
                n = Math.Max(minLayers, fixedLayers);
            }
            else
            {
                // Layer count N such that y1 * (r^N - 1)/(r - 1) >= coverage * delta
                // r^N >= 1 + (coverage * delta / y1) * (r - 1)
                // N = ceil( ln(1 + (coverage * delta / y1) * (r - 1)) / ln(r) )
                double term = 1.0 + (coverage * deltaM / y1M) * (r - 1.0);
                double nCalc = Math.Log(term) / Math.Log(r);
                int nInt = (int)Math.Ceiling(nCalc);
                n = Math.Max(minLayers, nInt);
                if (n > 60) n = 60; // Safety cap
            }

            // Actual total thickness: y1 * (r^N - 1) / (r - 1)
            double totalThicknessMm = y1Mm * (Math.Pow(r, n) - 1.0) / (r - 1.0);

            return new BoundaryLayerSpec(
                (float)re,
                (float)cf,
                (float)tauW,
                (float)uTau,
                (float)y1Mm,
                (float)deltaMm,
                n,
                (float)totalThicknessMm,
                growthRate,
                targetYPlus);
        }
    }
}
