using System;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Berechnete Parameter einer Grenzschicht nach Schlichting / Prandtlscher Grenzschichttheorie
    /// für eine turbulente Grenzschicht an einer ebenen Platte (TODO-40).
    /// </summary>
    /// <param name="ReynoldsNumber">Reynolds-Zahl Re_L bezogen auf die charakteristische Länge L.</param>
    /// <param name="SkinFrictionCoeff">Wandreibungsbeiwert Cf = 0.026 * Re_L^(-1/7).</param>
    /// <param name="WallShearStress">Wandschubspannung tau_w = 0.5 * rho * U^2 * Cf in Pa.</param>
    /// <param name="FrictionVelocity">Schubspannungsgeschwindigkeit u_tau = sqrt(tau_w / rho) in m/s.</param>
    /// <param name="FirstLayerHeightMm">Erste Zellhöhe y1 = y+ * mu / (rho * u_tau) in mm.</param>
    /// <param name="BoundaryLayerThicknessMm">Turbulente Grenzschichtdicke delta = 0.37 * L * Re_L^(-0.2) in mm.</param>
    /// <param name="LayerCount">Anzahl der Randschichten N.</param>
    /// <param name="TotalThicknessMm">Gesamtdicke aller N Schichten in mm.</param>
    /// <param name="GrowthRate">Wachstumsrate r zwischen zwei aufeinanderfolgenden Schichten.</param>
    /// <param name="TargetYPlus">Vorgegebenes Ziel-y+.</param>
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
    /// Reiner, zustandsloser Rechner für die Dimensionierung strukturierter Grenzschichtnetze (TODO-40).
    /// </summary>
    public static class BoundaryLayerCalculator
    {
        /// <summary>
        /// Berechnet die Grenzschichtparameter für eine turbulente Grenzschicht (Flachplatte)
        /// zur Erreichung des Ziel-y+-Wertes an der Wand.
        /// </summary>
        /// <param name="density">Dichte rho in kg/m³ (> 0).</param>
        /// <param name="velocity">Anströmgeschwindigkeit U in m/s (> 0).</param>
        /// <param name="dynamicViscosity">Dynamische Viskosität mu in Pa·s (> 0).</param>
        /// <param name="characteristicLengthMm">Charakteristische Bauteillänge L in mm (> 0).</param>
        /// <param name="targetYPlus">Ziel-y+ für die erste Zellhöhe (> 0, Vorgabe 1.0).</param>
        /// <param name="growthRate">Wachstumsfaktor von Schicht zu Schicht (> 1.0, Vorgabe 1.2).</param>
        /// <param name="minLayers">Mindestanzahl an Schichten (>= 1, Vorgabe 4).</param>
        /// <param name="fixedLayers">Feste Schichtanzahl; 0 = automatisch aus Grenzschichtdicke delta berechnen.</param>
        /// <param name="coverage">Faktor auf die abzudeckende Grenzschichtdicke (Vorgabe 1.0 = genau δ, Literatur oft 1.2–1.5).</param>
        /// <exception cref="ArgumentOutOfRangeException">Wird geworfen bei physikalisch unsinnigen Eingaben.</exception>
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
                throw new ArgumentOutOfRangeException(nameof(density), "Dichte muss positiv sein (> 0).");
            if (velocity <= 0f)
                throw new ArgumentOutOfRangeException(nameof(velocity), "Geschwindigkeit muss positiv sein (> 0).");
            if (dynamicViscosity <= 0f)
                throw new ArgumentOutOfRangeException(nameof(dynamicViscosity), "Dynamische Viskosität muss positiv sein (> 0).");
            if (characteristicLengthMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(characteristicLengthMm), "Charakteristische Länge muss positiv sein (> 0).");
            if (targetYPlus <= 0f)
                throw new ArgumentOutOfRangeException(nameof(targetYPlus), "Ziel-y+ muss positiv sein (> 0).");
            if (growthRate <= 1.0f)
                throw new ArgumentOutOfRangeException(nameof(growthRate), "Wachstumsfaktor muss größer als 1.0 sein.");
            if (minLayers < 1)
                throw new ArgumentOutOfRangeException(nameof(minLayers), "Mindestanzahl an Schichten muss mindestens 1 sein.");
            if (fixedLayers < 0)
                throw new ArgumentOutOfRangeException(nameof(fixedLayers), "fixedLayers darf nicht negativ sein.");
            if (coverage <= 0f)
                throw new ArgumentOutOfRangeException(nameof(coverage), "coverage muss positiv sein (> 0).");

            double rho = density;
            double u = velocity;
            double mu = dynamicViscosity;
            double l = characteristicLengthMm * 0.001; // mm in m
            double yPlus = targetYPlus;
            double r = growthRate;

            // Re_L = rho * U * L / mu
            double re = (rho * u * l) / mu;

            // Cf = 0.026 * Re_L^(-1/7) (turbulente Grenzschicht, Flachplatte)
            double cf = 0.026 * Math.Pow(re, -1.0 / 7.0);

            // tau_w = 0.5 * rho * U^2 * Cf,  u_tau = sqrt(tau_w / rho)
            double tauW = 0.5 * rho * u * u * cf;
            double uTau = Math.Sqrt(tauW / rho);

            // y1 = y+ * mu / (rho * u_tau) in Metern
            double y1M = (yPlus * mu) / (rho * uTau);
            double y1Mm = y1M * 1000.0;

            // delta = 0.37 * L * Re_L^(-0.2) in Metern
            double deltaM = 0.37 * l * Math.Pow(re, -0.2);
            double deltaMm = deltaM * 1000.0;

            int n;
            if (fixedLayers > 0)
            {
                n = Math.Max(minLayers, fixedLayers);
            }
            else
            {
                // Schichtzahl N, sodass y1 * (r^N - 1)/(r - 1) >= coverage * delta
                // r^N >= 1 + (coverage * delta / y1) * (r - 1)
                // N = ceil( ln(1 + (coverage * delta / y1) * (r - 1)) / ln(r) )
                double term = 1.0 + (coverage * deltaM / y1M) * (r - 1.0);
                double nCalc = Math.Log(term) / Math.Log(r);
                int nInt = (int)Math.Ceiling(nCalc);
                n = Math.Max(minLayers, nInt);
                if (n > 60) n = 60; // Sicherheitsgrenze
            }

            // Tatsächliche Gesamtdicke: y1 * (r^N - 1) / (r - 1)
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
