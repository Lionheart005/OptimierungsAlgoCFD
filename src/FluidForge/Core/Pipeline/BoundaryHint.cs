using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Detects whether the champion lies at a parameter bound (<c>ParameterBounds</c> in <c>project.json</c>).
    ///
    /// <para>
    /// If the optimum sits on the boundary of the allowed range, the result is constrained by that bound
    /// and a better value may lie beyond it. The framework <b>never</b> widens bounds automatically
    /// (only the user knows the physics, structural limits, and manufacturability); it prints a visible
    /// notice at the end of the run instead.
    /// Background: in the 10x10 benchmark of 08.11.2026, all Bayesian winners hit maximum camber and
    /// minimum thickness.
    /// </para>
    /// </summary>
    public static class BoundaryHint
    {
        /// <summary>A parameter counts as "at the bound" if its distance to the limit is at most this fraction of the span.</summary>
        public const double RelativeMargin = 0.01;

        /// <summary>A parameter of the winner that lies on a bound.</summary>
        public sealed record Finding(string Parameter, float Value, float Bound, bool IsUpper)
        {
            public string Describe()
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                return $"{Parameter} = {Value.ToString("0.##", inv)} ({(IsUpper ? "upper" : "lower")} bound {Bound.ToString("0.##", inv)})";
            }
        }

        /// <summary>
        /// All active parameters of the winner that lie on a bound. Fixed parameters (Min = Max) and
        /// unbounded parameters are excluded.
        /// </summary>
        public static IReadOnlyList<Finding> Find(
            IReadOnlyDictionary<string, float> parameters,
            IReadOnlyDictionary<string, (float Min, float Max)>? bounds)
        {
            var findings = new List<Finding>();
            if (bounds == null) return findings;

            foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!bounds.TryGetValue(name, out (float Min, float Max) limit)) continue;

                double span = (double)limit.Max - limit.Min;
                if (!(span > 0)) continue;

                double margin = RelativeMargin * span;
                if (value - limit.Min <= margin) findings.Add(new Finding(name, value, limit.Min, IsUpper: false));
                else if (limit.Max - value <= margin) findings.Add(new Finding(name, value, limit.Max, IsUpper: true));
            }

            return findings;
        }

        /// <summary>Output lines shown at the end of the run; empty if no parameter lies on a bound.</summary>
        public static IReadOnlyList<string> Lines(IReadOnlyList<Finding> findings)
        {
            if (findings.Count == 0) return Array.Empty<string>();

            var lines = new List<string> { "⚠ HINT: The optimum lies at the boundary of the search space" };
            lines.AddRange(findings.Select(f => "   " + f.Describe()));
            lines.Add("   The result is constrained by this bound; a better value may lie beyond it.");
            lines.Add("   Bounds are never widened automatically. Adjust \"ParameterBounds\" in project.json and re-run if desired.");
            return lines;
        }
    }
}
