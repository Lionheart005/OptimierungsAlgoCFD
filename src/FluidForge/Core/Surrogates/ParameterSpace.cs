using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Parameter space of a run, normalized to the unit hypercube [0, 1]^d.
    ///
    /// <para>
    /// Key order in <see cref="SimulationContext.CurrentBaseParameters"/> defines dimension order.
    /// Parameters with <c>Min &lt; Max</c> are <b>free</b> and modeled; parameters with <c>Min == Max</c>
    /// are <b>fixed</b> and reinserted unchanged. Missing or invalid bounds fail fast in <see cref="Create"/>
    /// before any expensive evaluation starts.
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

        /// <summary>Names of free parameters in dimension order.</summary>
        public IReadOnlyList<string> FreeNames => _freeNames;

        /// <summary>Number of free parameters (model dimension).</summary>
        public int Dimension => _freeNames.Length;

        /// <summary>Fixed parameters (Min == Max) with their constant values.</summary>
        public IReadOnlyDictionary<string, float> FixedValues => _fixed;

        /// <summary>
        /// Builds and validates the parameter space.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A parameter is missing bounds, has Min &gt; Max, or has non-finite bounds.
        /// </exception>
        public static ParameterSpace Create(SimulationContext context)
        {
            var names = context.CurrentBaseParameters.Keys.ToArray();
            string projectName = string.IsNullOrWhiteSpace(context.Project.ProjectName) ? "<Project>" : context.Project.ProjectName;
            string where = $"src/projects/{projectName}/project.json → \"ParameterBounds\"";

            var missing = names.Where(n => !context.Project.ParameterBounds.ContainsKey(n)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Bayesian optimization requires bounds (Min/Max) for every parameter "
                    + $"to define the search space. Missing for: {string.Join(", ", missing)}. "
                    + $"Add them in {where}.");
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
                        $"{OutputConventions.ConfigTag} Parameter '{name}': invalid bounds (Min = {min}, Max = {max}). "
                        + $"Min must be ≤ Max and both must be finite. Fix in {where}.");
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
                        $"{OutputConventions.BayesTag} Initial value of '{name}' ({start}) is outside bounds [{min}, {max}] and will be clamped.");
                }
            }

            return new ParameterSpace(names, free.ToArray(), minimums.ToArray(), maximums.ToArray(), fixedValues, bounds);
        }

        /// <summary>
        /// Normalizes free parameters to [0, 1]. Missing or out-of-bounds values are clamped
        /// (missing → 0.5).
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
        /// Inverse of <see cref="ToUnit"/>: reinserts fixed parameters and clamps all values
        /// to their bounds, preserving project key order.
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

            // Preserve project parameter order.
            var ordered = new Dictionary<string, float>();
            foreach (string name in _allNames) ordered[name] = result[name];
            return ordered;
        }

        /// <summary>Clamps a parameter dictionary to its configured bounds.</summary>
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
