using System.Globalization;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Advisory checks for flow regime and turbulence modeling assumptions in SU2.
    /// </summary>
    public static class Su2RegimeAdvisor
    {
        /// <summary>Reynolds number below which boundary layers are commonly laminar/transitional.</summary>
        public const float TransitionalReynoldsThreshold = 5e5f;

        /// <summary>
        /// Returns an explicit warning when <paramref name="reynoldsNumber"/> falls in the transitional regime
        /// (<c>0 &lt; Re_L &lt; 5e5</c>) under fully turbulent RANS modeling, or <c>null</c> otherwise.
        /// </summary>
        public static string? CheckLowReynoldsWarning(float reynoldsNumber, string? turbulenceModel)
        {
            if (!float.IsFinite(reynoldsNumber) || reynoldsNumber <= 0f || reynoldsNumber >= TransitionalReynoldsThreshold)
                return null;

            string model = string.IsNullOrWhiteSpace(turbulenceModel)
                ? "SA"
                : turbulenceModel.Trim().ToUpperInvariant();

            return $"Transitional regime detected (Re_L = {reynoldsNumber.ToString("0.00e0", CultureInfo.InvariantCulture)} < 5.0e5): "
                 + $"fully turbulent {model} ignores laminar separation bubbles — absolute CD may deviate from free-transition wind-tunnel data; "
                 + "consider SST/transition modeling or compare against fully-turbulent references.";
        }
    }
}
