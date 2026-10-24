using System;

namespace KhaozEngine.Physics;

/// <summary>Detached support-query work, route totals and per-session retained capacities.</summary>
public sealed class SupportQueryMetrics
{
    readonly SupportQueryCounts _counts;
    readonly SupportRouteCounts _routes;

    internal SupportQueryMetrics(in SupportQueryCounts counts, in SupportRouteCounts routes,
        in SupportProofReuseRetention retention, bool hasCompleteLifetimes)
    {
        _counts = counts;
        _routes = routes;
        Routes = new(routes);
        ProofReuse = new(retention.PeakKeys, retention.PeakBytes, hasCompleteLifetimes);
    }

    /// <summary>Recorded work for a defined, stable counter identity.</summary>
    public long this[SupportQueryCounter counter] => _counts[counter];
    /// <summary>Route totals, including the largest observed medium retry count.</summary>
    public SupportQueryRouteMetrics Routes { get; }
    /// <summary>Largest observed single-session capacities and lifetime completeness.</summary>
    public SupportProofReuseMetrics ProofReuse { get; }

    internal SupportQueryMetrics Add(SupportQueryMetrics other) => new(_counts + other._counts, _routes + other._routes,
        new(0, 0, Math.Max(ProofReuse.PeakKeys, other.ProofReuse.PeakKeys),
            Math.Max(ProofReuse.PeakBytes, other.ProofReuse.PeakBytes)),
        ProofReuse.HasCompleteLifetimes && other.ProofReuse.HasCompleteLifetimes);

    internal static SupportQueryMetrics Empty { get; } = new(default, default, default, true);
}

/// <summary>Immutable route counts. MaximumMediumRetries is a maximum, all other fields are totals.</summary>
public sealed class SupportQueryRouteMetrics
{
    internal SupportQueryRouteMetrics(in SupportRouteCounts routes)
    {
        WallRetries = routes.WallRetries;
        PushedPlacements = routes.PushedPlacements;
        MediumResolves = routes.MediumResolves;
        MaximumMediumRetries = routes.MaximumMediumRetries;
        GroundSegments = routes.GroundSegments;
        SlideSegments = routes.SlideSegments;
        AirSegments = routes.AirSegments;
        WaterSegments = routes.WaterSegments;
        RoundedReads = routes.RoundedReads;
        MediumHandoffs = routes.MediumHandoffs;
    }

    public long WallRetries { get; }
    public long PushedPlacements { get; }
    public long MediumResolves { get; }
    public long MaximumMediumRetries { get; }
    public long GroundSegments { get; }
    public long SlideSegments { get; }
    public long AirSegments { get; }
    public long WaterSegments { get; }
    public long RoundedReads { get; }
    public long MediumHandoffs { get; }
}

/// <summary>Maximum single-session owned memo capacities observed during the contributing intervals.
/// These are deterministic retained payload bytes, excluding CLR overhead and unrelated storage.</summary>
public sealed class SupportProofReuseMetrics
{
    internal SupportProofReuseMetrics(long peakKeys, long peakBytes, bool hasCompleteLifetimes)
    {
        PeakKeys = peakKeys;
        PeakBytes = peakBytes;
        HasCompleteLifetimes = hasCompleteLifetimes;
    }

    public long PeakKeys { get; }
    public long PeakBytes { get; }
    /// <summary>Every interval entered and ended with zero live memo keys and bytes.</summary>
    public bool HasCompleteLifetimes { get; }
}
