using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Verifies whether a champion model is physically stable.
    /// Each project can define its own validation criteria.
    /// </summary>
    public interface IModelValidator
    {
        ModelRecord ValidateChampion(
            List<ModelRecord> candidates,
            SimulationConfig config,
            Func<Dictionary<string, float>, ModelRecord> resimulate);
    }
}
