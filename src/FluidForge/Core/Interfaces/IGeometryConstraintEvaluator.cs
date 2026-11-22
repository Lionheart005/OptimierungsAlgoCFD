using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Evaluates geometric constraints directly after geometry creation and before meshing/CFD.
    /// Violations short-circuit expensive meshing and solver runs.
    /// </summary>
    public interface IGeometryConstraintEvaluator
    {
        IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project);
    }
}
