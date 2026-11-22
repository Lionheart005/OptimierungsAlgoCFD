using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Validates the keys of a configuration file against the properties of the target type.
    /// Unknown keys are rejected immediately instead of being silently ignored during deserialization.
    /// </summary>
    public static class ConfigSchema
    {
        /// <summary>
        /// Validates all keys in <paramref name="overlay"/> against <paramref name="targetType"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The file contains at least one key unknown to the target type. The message lists
        /// the filename, invalid key(s), and closest valid property names.
        /// </exception>
        public static void Validate(Type targetType, JsonObject overlay, string filePath)
        {
            var problems = new List<string>();
            Collect(targetType, overlay, prefix: string.Empty, problems);

            if (problems.Count == 0) return;

            string headline = problems.Count == 1
                ? "contains a key unknown to"
                : $"contains {problems.Count} keys unknown to";

            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} '{filePath}' {headline} type {targetType.Name}:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "          " + problem))
                + Environment.NewLine
                + "          An unknown key has no effect: it would be silently dropped and the "
                + "run would continue using the default value.");
        }

        private static void Collect(Type type, JsonObject overlay, string prefix, List<string> problems)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (KeyValuePair<string, JsonNode?> entry in overlay)
            {
                // Case-insensitive match — matching the JSON deserializer.
                PropertyInfo? property = properties.FirstOrDefault(
                    candidate => string.Equals(candidate.Name, entry.Key, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    problems.Add($"'{prefix}{entry.Key}' — {Suggest(entry.Key, properties)}");
                    continue;
                }

                // Only recurse into real objects. A Dictionary has free-form keys: "Length"
                // under BaseParameters is a parameter name, not a property name, just as
                // "Drag" under ResultMetrics is a metric name.
                if (entry.Value is JsonObject nested && HasOwnProperties(property.PropertyType))
                    Collect(property.PropertyType, nested, prefix + property.Name + ".", problems);
            }
        }

        /// <summary>
        /// Returns whether this type has its own named properties rather than being a value or
        /// free-key container (string, primitive, enum, or IEnumerable such as Dictionary/List/Array).
        /// </summary>
        private static bool HasOwnProperties(Type type)
        {
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return false;

            return true;
        }

        private static string Suggest(string key, PropertyInfo[] properties)
        {
            int tolerance = Math.Max(2, key.Length / 3);

            List<string> close = properties
                .Select(property => new { property.Name, Distance = Distance(key, property.Name) })
                .Where(candidate => candidate.Distance <= tolerance)
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(candidate => candidate.Name)
                .ToList();

            if (close.Count > 0)
                return "did you mean " + string.Join(" or ", close) + "?";

            string[] valid = properties
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (valid.Length == 0) return "this type has no configurable settings.";

            // Always show the full list so no valid key is hidden by truncation.
            return "valid keys are: " + string.Join(", ", valid);
        }

        /// <summary>
        /// Case-insensitive Levenshtein distance for typo suggestions.
        /// </summary>
        private static int Distance(string left, string right)
        {
            left = left.ToLowerInvariant();
            right = right.ToLowerInvariant();

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];

            for (int column = 0; column <= right.Length; column++) previous[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;

                for (int column = 1; column <= right.Length; column++)
                {
                    int substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                    int deletion = previous[column] + 1;
                    int insertion = current[column - 1] + 1;

                    current[column] = Math.Min(substitution, Math.Min(deletion, insertion));
                }

                Array.Copy(current, previous, current.Length);
            }

            return previous[right.Length];
        }
    }
}
