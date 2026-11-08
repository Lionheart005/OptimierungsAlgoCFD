using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Der Parameterraum eines Laufs, normiert auf den Einheitswürfel [0, 1]^d (TODO-53, F14).
    ///
    /// <para>
    /// Die Reihenfolge der Schlüssel in <see cref="SimulationContext.CurrentBaseParameters"/> ist die
    /// Reihenfolge der Dimensionen. Parameter mit <c>Min &lt; Max</c> sind <b>frei</b> und werden
    /// modelliert, Parameter mit <c>Min == Max</c> sind <b>fest</b> und werden nur wieder eingesetzt.
    /// Fehlt eine Grenze, ist der Raum nicht definiert: <see cref="Create"/> bricht dann mit einer
    /// klaren Meldung ab, bevor die erste teure Rechnung startet (Fail-fast wie TODO-16 und TODO-26).
    /// </para>
    /// </summary>
    public sealed class ParameterSpace
    {
        private readonly string[] _freeNames;
        private readonly double[] _min;
        private readonly double[] _max;
        private readonly Dictionary<string, float> _fixed;
        private readonly string[] _allNames;
        private readonly Dictionary<string, (float Min, float Max)> _bounds;

        private ParameterSpace(
            string[] allNames,
            string[] freeNames,
            double[] min,
            double[] max,
            Dictionary<string, float> fixedValues,
            Dictionary<string, (float Min, float Max)> bounds)
        {
            _allNames = allNames;
            _freeNames = freeNames;
            _min = min;
            _max = max;
            _fixed = fixedValues;
            _bounds = bounds;
        }

        /// <summary>Namen der frei veränderlichen Parameter in Dimensionsreihenfolge.</summary>
        public IReadOnlyList<string> FreeNames => _freeNames;

        /// <summary>Anzahl der frei veränderlichen Parameter (Dimension des Modells).</summary>
        public int Dimension => _freeNames.Length;

        /// <summary>Feste Parameter (Min == Max) mit ihrem Wert.</summary>
        public IReadOnlyDictionary<string, float> FixedValues => _fixed;

        /// <summary>
        /// Baut und prüft den Parameterraum.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Für einen Parameter fehlt die Grenze, oder es gilt Min &gt; Max oder sie ist nicht endlich.
        /// Die Meldung nennt den Parameter und die Datei, in der er einzutragen ist.
        /// </exception>
        public static ParameterSpace Create(SimulationContext context)
        {
            var names = context.CurrentBaseParameters.Keys.ToArray();
            string projectName = string.IsNullOrWhiteSpace(context.Project.ProjectName) ? "<Projekt>" : context.Project.ProjectName;
            string where = $"src/projects/{projectName}/project.json → \"ParameterBounds\"";

            var missing = names.Where(n => !context.Project.ParameterBounds.ContainsKey(n)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"[KONFIGURATION] Der Bayes-Optimierer braucht für jeden Parameter eine Grenze (Min/Max), "
                    + $"damit er den Suchraum kennt. Es fehlt für: {string.Join(", ", missing)}. "
                    + $"Nachtragen in {where}.");
            }

            var bounds = new Dictionary<string, (float Min, float Max)>();
            var free = new List<string>();
            var minimums = new List<double>();
            var maximums = new List<double>();
            var fixedValues = new Dictionary<string, float>();

            foreach (string name in names)
            {
                var (min, max) = context.Project.ParameterBounds[name];
                if (!float.IsFinite(min) || !float.IsFinite(max) || min > max)
                {
                    throw new InvalidOperationException(
                        $"[KONFIGURATION] Parameter '{name}': ungültige Grenzen (Min = {min}, Max = {max}). "
                        + $"Min muss ≤ Max sein, beide endlich. Korrigieren in {where}.");
                }

                bounds[name] = (min, max);
                if (min < max)
                {
                    free.Add(name);
                    minimums.Add(min);
                    maximums.Add(max);
                }
                else
                {
                    fixedValues[name] = min;
                }
            }

            foreach (string name in names)
            {
                float start = context.CurrentBaseParameters[name];
                var (min, max) = bounds[name];
                if (start < min || start > max)
                {
                    SimulationLog.Warning(
                        $"[BAYES] Startwert von '{name}' ({start}) liegt außerhalb der Grenzen [{min}, {max}] und wird begrenzt.");
                }
            }

            return new ParameterSpace(names, free.ToArray(), minimums.ToArray(), maximums.ToArray(), fixedValues, bounds);
        }

        /// <summary>
        /// Normiert die freien Parameter auf [0, 1]. Fehlende Namen und Werte außerhalb der Grenzen
        /// werden auf den erlaubten Bereich begrenzt (fehlend → Mitte).
        /// </summary>
        public double[] ToUnit(IReadOnlyDictionary<string, float> parameters)
        {
            var unit = new double[_freeNames.Length];
            for (int i = 0; i < unit.Length; i++)
            {
                double range = _max[i] - _min[i];
                if (parameters.TryGetValue(_freeNames[i], out float value) && float.IsFinite(value))
                    unit[i] = Math.Clamp((value - _min[i]) / range, 0.0, 1.0);
                else
                    unit[i] = 0.5;
            }
            return unit;
        }

        /// <summary>
        /// Gegenstück zu <see cref="ToUnit"/>: füllt die festen Parameter auf und begrenzt alle
        /// Werte auf den erlaubten Bereich. Die Schlüsselreihenfolge ist die des Projekts.
        /// </summary>
        public Dictionary<string, float> FromUnit(double[] unit)
        {
            var result = new Dictionary<string, float>();
            foreach (var (name, value) in _fixed) result[name] = value;

            for (int i = 0; i < _freeNames.Length; i++)
            {
                double clamped = Math.Clamp(unit[i], 0.0, 1.0);
                var (min, max) = _bounds[_freeNames[i]];
                result[_freeNames[i]] = Math.Clamp((float)(_min[i] + clamped * (_max[i] - _min[i])), min, max);
            }

            // Reihenfolge wie im Projekt herstellen.
            var ordered = new Dictionary<string, float>();
            foreach (string name in _allNames) ordered[name] = result[name];
            return ordered;
        }

        /// <summary>Begrenzt einen Parametersatz auf die Grenzen (z. B. den Startpunkt).</summary>
        public Dictionary<string, float> Clamp(IReadOnlyDictionary<string, float> parameters)
        {
            var result = new Dictionary<string, float>();
            foreach (var (name, value) in parameters)
            {
                result[name] = _bounds.TryGetValue(name, out var b) ? Math.Clamp(value, b.Min, b.Max) : value;
            }
            return result;
        }
    }
}
