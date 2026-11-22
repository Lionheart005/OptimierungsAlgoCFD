using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Computes the fitness of a model based on its metrics.
    /// Each project defines its own fitness formula.
    /// </summary>
    public interface IFitnessCalculator
    {
        void CalculateFitness(ModelRecord record, SimulationConfig config);

        /// <summary>
        /// Metrics in <see cref="ModelRecord.PassiveParameters"/> without which the
        /// fitness formula cannot be meaningfully evaluated.
        ///
        /// <para>
        /// Required <b>targets</b> can be checked in the constructor, whereas <b>metrics</b>
        /// only appear after the first geometry and solver run. <see cref="WorkflowController"/>
        /// checks this list after the first successful variant and aborts early if any are missing.
        /// </para>
        ///
        /// <para>
        /// Defaults to empty — implementations that do not declare metrics remain unaffected.
        /// </para>
        /// </summary>
        IReadOnlyCollection<string> RequiredMetrics => Array.Empty<string>();
    }
}
