using System.Collections.Generic;

namespace KhaozEngine.Objectives;

/// <summary>
/// One serialized counter cell: the Sum + Max held for a single (<see cref="Key"/>, <see cref="Scope"/>) pair.
/// A plain data type with public get/set members so any serializer (the game's own, e.g. System.Text.Json)
/// round-trips it - the framework itself takes no serialization dependency.
/// </summary>
public struct MetricCellSnapshot
{
    /// <summary>The metric key.</summary>
    public string Key { get; set; }

    /// <summary>The scope (<see cref="MetricScope.Persistent"/> or <see cref="MetricScope.Session"/>).</summary>
    public MetricScope Scope { get; set; }

    /// <summary>The accumulated Report total.</summary>
    public double Sum { get; set; }

    /// <summary>The peak Observe value.</summary>
    public double Max { get; set; }
}

/// <summary>
/// The full serializable state of an <see cref="ObjectiveTracker"/>: every non-empty counter cell plus the set of
/// completed objective ids. Produced by <see cref="ObjectiveTracker.Capture"/> and consumed by
/// <see cref="ObjectiveTracker.Restore(ObjectivesSnapshot)"/>. The game owns transport (folds this into its own
/// save); the framework guarantees a deterministic capture order so identical state yields identical output.
/// </summary>
/// <remarks>
/// Objective <see cref="ObjectiveDefinition.Metadata"/> is deliberately <em>not</em> captured - it comes from the
/// re-registered definitions, not the save. Register definitions, then <see cref="ObjectiveTracker.Restore(ObjectivesSnapshot)"/>.
/// </remarks>
public sealed class ObjectivesSnapshot
{
    /// <summary>The non-empty counter cells, sorted by key then scope on capture.</summary>
    public List<MetricCellSnapshot> Metrics { get; set; } = new();

    /// <summary>The ids of completed objectives, sorted on capture.</summary>
    public List<string> Completed { get; set; } = new();
}
