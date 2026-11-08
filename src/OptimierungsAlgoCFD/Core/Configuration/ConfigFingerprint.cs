using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Fingerabdruck der geladenen Konfiguration (TODO-50/52, F27): je Datei ein SHA-256 über
    /// ihre <b>Werte</b>, so wie <see cref="EffectiveConfigWriter.Build(ProjectContext)"/> sie
    /// aufstellt — ohne Zeitpunkt, Herkunft und Laufzeitwerte.
    ///
    /// <para>
    /// <c>RandomSeed</c> zählt bewusst nicht mit: bei der Wiederaufnahme kommt der Seed aus dem
    /// Journal, und ein nachträglich eingetragener Seed soll den Lauf nicht für „geändert"
    /// erklären (TODO-52, Schritt 4).
    /// </para>
    /// </summary>
    public static class ConfigFingerprint
    {
        private const string Prefix = "sha256:";

        /// <summary>Je Abschnitt (<c>simulation.json</c>, <c>project.json</c>, <c>solvers/su2.json</c> …) ein Hash.</summary>
        public static SortedDictionary<string, string> Sections(ProjectContext context)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

            JsonObject built = EffectiveConfigWriter.Build(context);
            if (built["Abschnitte"] is not JsonObject sections) return result;

            foreach (var section in sections)
            {
                JsonNode? values = (section.Value as JsonObject)?["Werte"]?.DeepClone();

                if (section.Key == "simulation.json" && values is JsonObject simulation)
                    simulation.Remove(nameof(SimulationConfig.RandomSeed));

                result[section.Key] = Hash(values?.ToJsonString() ?? "null");
            }

            return result;
        }

        /// <summary>Ein Hash über alle Abschnitte zusammen.</summary>
        public static string Combine(IReadOnlyDictionary<string, string> sections)
            => Hash(string.Join("\n", sections.OrderBy(s => s.Key, StringComparer.Ordinal)
                                              .Select(s => $"{s.Key}={s.Value}")));

        /// <summary>
        /// Die Abschnitte, die sich unterscheiden: andere Werte, neu hinzugekommen oder
        /// weggefallen. Alphabetisch.
        /// </summary>
        public static IReadOnlyList<string> Differences(
            IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> now)
        {
            return before.Keys.Union(now.Keys)
                .Where(key => !before.TryGetValue(key, out string? a)
                              || !now.TryGetValue(key, out string? b)
                              || a != b)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }

        private static string Hash(string text)
            => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
