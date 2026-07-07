using System;

namespace KhaozEngine.Objectives;

/// <summary>
/// The counter scopes an <see cref="ObjectiveTracker"/> holds for every metric key. A single
/// <see cref="ObjectiveTracker.Report(string, double)"/> / <see cref="ObjectiveTracker.Observe(string, double)"/>
/// updates <em>all</em> active scopes for the key; <see cref="ObjectiveTracker.ResetScope(MetricScope)"/> is the
/// entire "all-time vs single-run" mechanism (the framework never knows what a "run" is - the game calls
/// <c>ResetScope(Session)</c> at its own run / prestige boundary).
/// </summary>
/// <remarks>
/// A <see cref="ObjectiveCondition"/> targets exactly one scope (<see cref="Persistent"/> or <see cref="Session"/>).
/// The flag combinations exist for <see cref="ObjectiveTracker.ResetScope(MetricScope)"/> (which may clear several
/// scopes at once), not for authoring conditions.
/// </remarks>
[Flags]
public enum MetricScope
{
    /// <summary>No scope. Not valid on a condition.</summary>
    None = 0,

    /// <summary>The never-resets, lifetime scope (all-time totals / all-time maxima).</summary>
    Persistent = 1,

    /// <summary>The scope the game clears on demand at a run / prestige boundary via <see cref="ObjectiveTracker.ResetScope(MetricScope)"/>.</summary>
    Session = 2,

    /// <summary>Both scopes - only meaningful as a <see cref="ObjectiveTracker.ResetScope(MetricScope)"/> argument.</summary>
    All = Persistent | Session,
}
