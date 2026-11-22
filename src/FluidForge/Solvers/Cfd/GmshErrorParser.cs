using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Centralized helpers for stripping ANSI control codes and extracting
    /// meaningful error lines from Gmsh output.
    /// </summary>
    internal static class GmshErrorParser
    {
        /// <summary>
        /// Removes ANSI color codes (<c>ESC[1m</c>, <c>ESC[31m</c>, ...): Gmsh colorizes error lines
        /// even when output is redirected to a pipe.
        /// </summary>
        public static string StripAnsi(string text)
            => Regex.Replace(text, "\u001B\\[[0-9;]*m", string.Empty);

        /// <summary>
        /// Extracts up to two meaningful error lines from the full Gmsh output.
        /// </summary>
        public static string? ExtractGmshError(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            return ExtractGmshError(output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
        }

        /// <summary>
        /// Meaningful error lines from Gmsh output (at most two, excluding the
        /// final "Mesh generation error summary", separator lines, and counters).
        /// Returns <c>null</c> if none are present.
        /// </summary>
        public static string? ExtractGmshError(IEnumerable<string> lines)
        {
            var picked = new List<string>();
            foreach (string withCodes in lines)
            {
                string raw = StripAnsi(withCodes);
                if (!raw.StartsWith("Error", StringComparison.Ordinal)) continue;
                int colon = raw.IndexOf(':');
                string text = (colon >= 0 ? raw.Substring(colon + 1) : raw).Trim();
                if (text.Length == 0 || text.StartsWith("-", StringComparison.Ordinal)) continue;
                if (text.Contains("error summary", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Contains("Check the full log", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(text, @"^\d+\s+(warning|error)s?$")) continue;

                picked.Add(text);
                if (picked.Count == 2) break;
            }

            if (picked.Count == 0) return null;
            string joined = string.Join(" | ", picked);
            return joined.Length > 300 ? joined.Substring(0, 300) + "…" : joined;
        }
    }
}
