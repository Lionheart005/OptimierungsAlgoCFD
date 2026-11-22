using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Fingerprint of the loaded configuration: one SHA-256 hash per file over
    /// its <b>values</b>, as built by <see cref="EffectiveConfigWriter.Build(ProjectContext)"/> —
    /// excluding timestamps, origins, and runtime state.
    ///
    /// <para>
    /// <c>RandomSeed</c> is intentionally excluded: on resume the seed comes from the
    /// journal, and filling in an explicit seed afterward should not mark the config as "changed".
    /// </para>
    /// </summary>
    public static class ConfigFingerprint
    {
        private const string Prefix = "sha256:";

        /// <summary>One hash per section (<c>simulation.json</c>, <c>project.json</c>, <c>solvers/su2.json</c>, ...).</summary>
        public static SortedDictionary<string, string> Sections(ProjectContext context)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

            JsonObject built = EffectiveConfigWriter.Build(context);
            if (built["Sections"] is not JsonObject sections) return result;

            foreach (var section in sections)
            {
                JsonNode? values = (section.Value as JsonObject)?["Values"]?.DeepClone();

                if (section.Key == "simulation.json" && values is JsonObject simulation)
                    simulation.Remove(nameof(SimulationConfig.RandomSeed));

                result[section.Key] = Hash(values?.ToJsonString() ?? "null");
            }

            return result;
        }

        /// <summary>Combined hash over all sections.</summary>
        public static string Combine(IReadOnlyDictionary<string, string> sections)
            => Hash(string.Join("\n", sections.OrderBy(s => s.Key, StringComparer.Ordinal)
                                              .Select(s => $"{s.Key}={s.Value}")));

        /// <summary>
        /// Sections that differ: changed values, newly added, or removed. Sorted alphabetically.
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
