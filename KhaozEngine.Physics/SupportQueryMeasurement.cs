using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.Physics;

/// <summary>Immutable cumulative evidence. Combine only disjoint intervals, never a parent with its child
/// or repeated cumulative snapshots of the same interval. Results can be read on any thread.</summary>
public sealed class SupportQueryMeasurement
{
    internal SupportQueryMeasurement(string label, SupportQueryMetrics metrics)
        : this(new SortedDictionary<string, SupportQueryMetrics>(StringComparer.Ordinal) { [label] = metrics }) { }

    internal SupportQueryMeasurement(SortedDictionary<string, SupportQueryMetrics> rows)
    {
        var frozen = new SortedDictionary<string, SupportQueryMetrics>(rows, StringComparer.Ordinal);
        SupportQueryMetrics total = SupportQueryMetrics.Empty;
        foreach (SupportQueryMetrics row in frozen.Values) total = total.Add(row);
        Total = total;
        ByLabel = new ReadOnlyDictionary<string, SupportQueryMetrics>(frozen);
    }

    public SupportQueryMetrics Total { get; }
    /// <summary>Exclusive rows, enumerated in ordinal, case-sensitive label order.</summary>
    public IReadOnlyDictionary<string, SupportQueryMetrics> ByLabel { get; }

    /// <summary>Adds supplied evidence without deduplication. Ordinary counts add with checked arithmetic,
    /// retry and retention peaks take maxima, and lifetime completeness combines with logical AND.</summary>
    public static SupportQueryMeasurement Combine(IEnumerable<SupportQueryMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var rows = new SortedDictionary<string, SupportQueryMetrics>(StringComparer.Ordinal);
        foreach (SupportQueryMeasurement measurement in measurements)
        {
            ArgumentNullException.ThrowIfNull(measurement);
            foreach (var (label, metrics) in measurement.ByLabel)
                rows[label] = rows.TryGetValue(label, out SupportQueryMetrics? prior) ? prior.Add(metrics) : metrics;
        }
        return new(rows);
    }
}
