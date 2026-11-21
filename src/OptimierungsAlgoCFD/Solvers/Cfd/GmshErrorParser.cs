using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Zentrale Helfer zum Bereinigen von ANSI-Steuercodes und zum Extrahieren
    /// aussagekräftiger Fehlerzeilen aus der Gmsh-Ausgabe.
    /// </summary>
    internal static class GmshErrorParser
    {
        /// <summary>
        /// Entfernt ANSI-Farbcodes (<c>ESC[1m</c>, <c>ESC[31m</c> …): Gmsh färbt Fehlerzeilen ein,
        /// auch wenn die Ausgabe in eine Pipe geht.
        /// </summary>
        public static string StripAnsi(string text)
            => Regex.Replace(text, "\u001B\\[[0-9;]*m", string.Empty);

        /// <summary>
        /// Extrahiert bis zu zwei aussagekräftige Fehlerzeilen aus der vollständigen Gmsh-Ausgabe.
        /// </summary>
        public static string? ExtractGmshError(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            return ExtractGmshError(output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
        }

        /// <summary>
        /// Die aussagekräftigen Fehlerzeilen aus der Gmsh-Ausgabe (höchstens zwei, ohne die
        /// Schlusszusammenfassung "Mesh generation error summary", Trennlinien und Zähler).
        /// <c>null</c>, wenn keine vorkommt.
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
