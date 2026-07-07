using System;

namespace KhaozEngine.Objectives;

/// <summary>How an <see cref="ObjectiveCondition"/> compares a metric reading against its target.</summary>
public enum ObjectiveConditionKind
{
    /// <summary>Accumulator threshold: the key's summed <see cref="ObjectiveTracker.Report(string, double)"/> total is <c>&gt;= target</c> (e.g. "mine 500 ore").</summary>
    AtLeast,

    /// <summary>Peak threshold: the key's maximum <see cref="ObjectiveTracker.Observe(string, double)"/> value is <c>&gt;= target</c> (e.g. "reach depth 200").</summary>
    Reached,

    /// <summary>Constraint / negative goal: the key's summed total is <c>&lt;= target</c> (e.g. "buy no upgrades this run").</summary>
    AtMost,
}

/// <summary>
/// One declarative, pure-data predicate over a single metric key in a single <see cref="MetricScope"/>. An
/// <see cref="ObjectiveDefinition"/> is an AND-composition of these. The framework names no domain concept: the
/// <see cref="Key"/> is an opaque string the game chose, and the <see cref="Target"/> is a plain number.
/// </summary>
public readonly struct ObjectiveCondition
{
    /// <summary>How <see cref="Target"/> is compared against the key's reading.</summary>
    public ObjectiveConditionKind Kind { get; }

    /// <summary>The opaque metric key this condition watches (the same string the game passes to Report / Observe).</summary>
    public string Key { get; }

    /// <summary>The numeric target the reading is compared against.</summary>
    public double Target { get; }

    /// <summary>The scope (<see cref="MetricScope.Persistent"/> or <see cref="MetricScope.Session"/>) the reading is taken from.</summary>
    public MetricScope Scope { get; }

    /// <summary>Creates a condition. Prefer the <see cref="AtLeast"/> / <see cref="Reached"/> / <see cref="AtMost"/> factories.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null/empty, or <paramref name="scope"/> is not exactly one of <see cref="MetricScope.Persistent"/> / <see cref="MetricScope.Session"/>.</exception>
    public ObjectiveCondition(ObjectiveConditionKind kind, string key, double target, MetricScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (scope != MetricScope.Persistent && scope != MetricScope.Session)
            throw new ArgumentException("A condition scope must be exactly one of Persistent or Session.", nameof(scope));
        Kind = kind;
        Key = key;
        Target = target;
        Scope = scope;
    }

    /// <summary>An accumulator-threshold condition: the summed total of <paramref name="key"/> in <paramref name="scope"/> is <c>&gt;= <paramref name="target"/></c>.</summary>
    public static ObjectiveCondition AtLeast(string key, double target, MetricScope scope)
        => new(ObjectiveConditionKind.AtLeast, key, target, scope);

    /// <summary>A peak-threshold condition: the maximum observed value of <paramref name="key"/> in <paramref name="scope"/> is <c>&gt;= <paramref name="target"/></c>.</summary>
    public static ObjectiveCondition Reached(string key, double target, MetricScope scope)
        => new(ObjectiveConditionKind.Reached, key, target, scope);

    /// <summary>A constraint condition: the summed total of <paramref name="key"/> in <paramref name="scope"/> is <c>&lt;= <paramref name="target"/></c>.</summary>
    public static ObjectiveCondition AtMost(string key, double target, MetricScope scope)
        => new(ObjectiveConditionKind.AtMost, key, target, scope);

    /// <summary>True for <see cref="ObjectiveConditionKind.Reached"/>, which reads the peak (Max); the others read the accumulator (Sum).</summary>
    internal bool UsesMax => Kind == ObjectiveConditionKind.Reached;

    /// <summary>Evaluates this condition against an already-selected reading (Sum for AtLeast/AtMost, Max for Reached).</summary>
    internal bool IsSatisfiedBy(double reading) => Kind switch
    {
        ObjectiveConditionKind.AtLeast => reading >= Target,
        ObjectiveConditionKind.Reached => reading >= Target,
        ObjectiveConditionKind.AtMost => reading <= Target,
        _ => false,
    };
}
