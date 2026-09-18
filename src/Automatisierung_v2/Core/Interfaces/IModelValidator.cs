using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Prüft ob ein Champion physikalisch stabil ist.
    /// Jedes Projekt kann eigene Validierungskriterien definieren.
    /// </summary>
    public interface IModelValidator
    {
        ModelRecord ValidateChampion(
            List<ModelRecord> candidates,
            SimulationConfig config,
            Func<Dictionary<string, float>, ModelRecord> resimulate);
    }
}
