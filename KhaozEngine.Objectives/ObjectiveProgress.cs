using System.Collections.Generic;

namespace KhaozEngine.Objectives;

/// <summary>
/// A live snapshot of one <see cref="ObjectiveCondition"/>'s progress: its current reading against its target, so
/// a game can render a "500 / 500" bar without any bookkeeping of its own.
/// </summary>
public readonly struct ConditionProgress
{
    /// <summary>The condition kind.</summary>
    public ObjectiveConditionKind Kind { get; }

    /// <summary>The metric key.</summary>
    public string Key { get; }

    /// <summary>The scope the reading is taken from.</summary>
    public MetricScope Scope { get; }

    /// <summary>The current reading (Sum for AtLeast/AtMost, Max for Reached) at the time of the query.</summary>
    public double Current { get; }

    /// <summary>The condition's target.</summary>
    public double Target { get; }

    /// <summary>Whether this single condition is satisfied right now.</summary>
    public bool IsSatisfied { get; }

    /// <summary>Creates a condition-progress row.</summary>
    public ConditionProgress(ObjectiveConditionKind kind, string key, MetricScope scope, double current, double target, bool isSatisfied)
    {
        Kind = kind;
        Key = key;
        Scope = scope;
        Current = current;
        Target = target;
        IsSatisfied = isSatisfied;
    }
}

/// <summary>
/// A live snapshot of one objective's completion state plus its per-condition progress, for a progress-log UI.
/// </summary>
/// <remarks>
/// For a completed objective <see cref="IsComplete"/> is the authoritative truth; the per-condition
/// <see cref="ConditionProgress.Current"/> readings are still live, so a Session-scoped condition can read back to
/// zero after a <see cref="ObjectiveTracker.ResetScope(MetricScope)"/> even though the objective stays complete.
/// </remarks>
public sealed class ObjectiveProgress
{
    /// <summary>The objective's id.</summary>
    public string ObjectiveId { get; }

    /// <summary>Whether the objective has completed (idempotent; never reverts).</summary>
    public bool IsComplete { get; }

    /// <summary>Per-condition progress, in the definition's condition order.</summary>
    public IReadOnlyList<ConditionProgress> Conditions { get; }

    /// <summary>Creates an objective-progress view.</summary>
    public ObjectiveProgress(string objectiveId, bool isComplete, IReadOnlyList<ConditionProgress> conditions)
    {
        ObjectiveId = objectiveId;
        IsComplete = isComplete;
        Conditions = conditions;
    }
}
