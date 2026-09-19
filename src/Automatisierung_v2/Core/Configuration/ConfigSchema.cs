using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Gleicht die Schlüssel einer Konfigurationsdatei gegen die Properties des Zieltyps ab.
    ///
    /// <para>
    /// Vor TODO-22 verschluckte der Merge einen Tippfehler stillschweigend: <c>"MachNumer"</c>
    /// landete im JsonNode, fiel beim Deserialisieren weg, und der Lauf rechnete stundenlang
    /// mit dem Standardwert weiter. Bei mehreren Projekten × mehreren Dateien wird das zur
    /// Falle — deshalb ist ein unbekannter Schlüssel ab jetzt ein Abbruch (Entscheidung 8).
    /// </para>
    /// </summary>
    public static class ConfigSchema
    {
        /// <summary>Ab wie vielen gültigen Namen die Aufzählung abgekürzt wird.</summary>
        private const int MaxNamesToList = 15;

        /// <summary>
        /// Prüft alle Schlüssel von <paramref name="overlay"/> gegen <paramref name="targetType"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Die Datei nennt mindestens einen Schlüssel, den der Zieltyp nicht kennt. Die Meldung
        /// führt Dateiname, Schlüssel und die nächstähnlichen gültigen Namen.
        /// </exception>
        public static void Validate(Type targetType, JsonObject overlay, string filePath)
        {
            var problems = new List<string>();
            Collect(targetType, overlay, prefix: string.Empty, problems);

            if (problems.Count == 0) return;

            string headline = problems.Count == 1
                ? "nennt einen Schlüssel, den"
                : $"nennt {problems.Count} Schlüssel, die";

            throw new InvalidOperationException(
                $"[CONFIG] '{filePath}' {headline} der Typ {targetType.Name} nicht kennt:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "          " + problem))
                + Environment.NewLine
                + "          Ein unbekannter Schlüssel wirkt nicht: er fiele beim Lesen weg und der "
                + "Lauf rechnete mit dem Standardwert weiter.");
        }

        private static void Collect(Type type, JsonObject overlay, string prefix, List<string> problems)
        {
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (KeyValuePair<string, JsonNode?> entry in overlay)
            {
                // Groß-/Kleinschreibung ignorieren — genau wie der Deserialisierer.
                PropertyInfo? property = properties.FirstOrDefault(
                    candidate => string.Equals(candidate.Name, entry.Key, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    problems.Add($"'{prefix}{entry.Key}' — {Suggest(entry.Key, properties)}");
                    continue;
                }

                // Nur in echte Objekte absteigen. Ein Dictionary hat freie Schlüssel: "Length"
                // unter BaseParameters ist ein Parametername und kein Property-Name, genauso
                // wie "Drag" unter ResultMetrics ein Metrikname ist.
                if (entry.Value is JsonObject nested && HasOwnProperties(property.PropertyType))
                    Collect(property.PropertyType, nested, prefix + property.Name + ".", problems);
            }
        }

        /// <summary>
        /// Hat dieser Typ eigene, benannte Properties — oder ist er ein Behälter mit freien
        /// Schlüsseln? Zeichenketten, Zahlen, Aufzählungen und alles Aufzählbare (Dictionary,
        /// List, HashSet, Array) sind Behälter und werden nicht betreten.
        /// </summary>
        private static bool HasOwnProperties(Type type)
        {
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return false;

            return true;
        }

        private static string Suggest(string key, PropertyInfo[] properties)
        {
            // Je länger der Schlüssel, desto mehr Vertipper lässt man durchgehen: bei
            // "BouncerTolerance" ist ein Abstand von 3 noch offensichtlich gemeint,
            // bei "Min" wäre er es nicht mehr.
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
                return "meintest du " + string.Join(" oder ", close) + "?";

            string[] valid = properties
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (valid.Length == 0) return "dieser Typ hat überhaupt keine Einstellungen.";

            return valid.Length <= MaxNamesToList
                ? "gültig wären: " + string.Join(", ", valid)
                : "gültig wären u.a.: " + string.Join(", ", valid.Take(MaxNamesToList)) + ", …";
        }

        /// <summary>
        /// Levenshtein-Abstand, Groß-/Kleinschreibung egal. Nur für die Vorschlagsliste —
        /// die Prüfung selbst hängt nicht daran.
        /// </summary>
        private static int Distance(string left, string right)
        {
            left = left.ToLowerInvariant();
            right = right.ToLowerInvariant();

            // Zwei Zeilen genügen: für die Zeile i braucht man nur i-1.
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
