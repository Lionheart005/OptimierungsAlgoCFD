using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Extended fitness evaluator that exposes raw physical objectives and post-solve constraints
    /// independently of scalarized fitness.
    /// </summary>
    public interface IObjectiveEvaluator : IFitnessCalculator
    {
        EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config);

        IReadOnlyCollection<string> RawComparisonMetrics => Array.Empty<string>();
    }

    /// <summary>
    /// Base class bridging <see cref="IObjectiveEvaluator"/> and legacy <see cref="IFitnessCalculator"/>.
    /// Keeps raw objectives unpenalized and tracks feasibility via <see cref="ModelRecord.Constraints"/>.
    /// </summary>
    public abstract class ObjectiveEvaluatorBase : IObjectiveEvaluator
    {
        public const float DefaultFailedFitness = 0.0001f;

        public virtual IReadOnlyCollection<string> RequiredMetrics => Array.Empty<string>();

        public virtual IReadOnlyCollection<string> RawComparisonMetrics => Array.Empty<string>();

        public abstract EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config);

        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            // Keep pre-solve geometry constraints intact while making repeated calls idempotent.
            record.Constraints.RemoveAll(c => !c.IsPreSolve);
            record.Objectives.Clear();
            if (record.FailedStage == EvaluationFailureStage.PostConstraint)
            {
                record.FailedStage = EvaluationFailureStage.None;
            }

            if (record.SimulationFailed || record.IsPreConstraintInfeasible)
            {
                record.Feasible = false;
                record.Fitness = DefaultFailedFitness;
                return;
            }

            EvaluationOutcome outcome = EvaluateObjectives(record, config);

            record.Objectives.AddRange(outcome.Objectives);
            foreach (ConstraintEvaluation constraint in outcome.Constraints)
            {
                record.Constraints.Add(constraint.IsPreSolve ? constraint with { IsPreSolve = false } : constraint);
            }

            record.Feasible = record.Constraints.All(c => c.IsSatisfied);
            if (!record.Feasible && record.FailedStage == EvaluationFailureStage.None)
            {
                record.FailedStage = EvaluationFailureStage.PostConstraint;
            }

            record.Fitness = ComputeScalarFitness(outcome);
        }

        private static float ComputeScalarFitness(EvaluationOutcome outcome)
        {
            if (outcome.ScalarFitnessOverride.HasValue && float.IsFinite(outcome.ScalarFitnessOverride.Value))
            {
                return outcome.ScalarFitnessOverride.Value;
            }

            if (outcome.Objectives.Count == 0)
            {
                return DefaultFailedFitness;
            }

            double weightedSum = 0.0;
            double totalWeight = 0.0;

            foreach (ObjectiveValue obj in outcome.Objectives)
            {
                if (!float.IsFinite(obj.Value) || !float.IsFinite(obj.Weight) || obj.Weight <= 0f)
                {
                    return DefaultFailedFitness;
                }

                double signedValue = obj.Direction == OptimizationDirection.Maximize ? obj.Value : -obj.Value;
                weightedSum += obj.Weight * signedValue;
                totalWeight += obj.Weight;
            }

            if (totalWeight <= 0.0 || !double.IsFinite(weightedSum))
            {
                return DefaultFailedFitness;
            }

            float scalar = (float)(weightedSum / totalWeight);
            return float.IsFinite(scalar) ? scalar : DefaultFailedFitness;
        }
    }
}
