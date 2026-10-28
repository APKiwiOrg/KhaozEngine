using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
namespace KhaozEngine.MapDoc.Storage;

/// <summary>Before-work admission for traversal, rectangle bookkeeping, metadata and page decodes.</summary>
internal sealed class MapPageBudget
{
    sealed class Admission
    {
        internal readonly int Limit;
        internal readonly object? Source;
        internal readonly long RangeLimit;
        internal int Attempts, Visits;
        internal long Ranges, Metadata;
        internal Admission(int limit, object? source)
        {
            Limit = limit;
            Source = source;
            RangeLimit = Ceiling(limit);
            if (source is not null) DeriveCeilings(this, source, limit, RangeLimit);
        }
    }

    /// <summary>The metadata and range ceiling a page-read allowance implies, one check plus a full page of entries in
    /// each of four index levels per page read.</summary>
    internal static long Ceiling(int pageReads) => 1L + 4L * MapSurfacePages.MaxEntries * pageReads;

    /// <summary>Names both scaled ceilings' origins for <paramref name="derived"/>: the ceiling itself, and the caller's
    /// MaxPageReads at one page read per 4 * MaxEntries checks after the one free check, so each origin refuses exactly
    /// when the gate does. A no-op unless a demand observer is subscribed.</summary>
    internal static void DeriveCeilings(object derived, object? source, int pageReads, long ceiling)
    {
        if (DemandDiagnostics.IsObserved) StoreCeilings(derived, source, pageReads, ceiling);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void StoreCeilings(object derived, object? source, int pageReads, long ceiling)
    {
        decimal scale = 1m / (4m * MapSurfacePages.MaxEntries);
        var pages = Scene.MapDemandOrigins.Of(source, nameof(MapQueryLimits.MaxPageReads), pageReads, -scale, scale);
        foreach (string name in new[] { "MaxPageMetadataChecks", "MaxPageRangeOperations" })
            Scene.MapDemandOrigins.Derive(derived, name, [new CapacityDemandOrigin(name, 0, ceiling)], pages);
    }
    readonly Admission _admission;
    internal MapPageBudget(int limit, MapSurfaceAcquisitionWork? work = null, object? source = null) : this(new Admission(limit, source), work) { }
    MapPageBudget(Admission admission, MapSurfaceAcquisitionWork? work) { _admission = admission; Work = work; }
    // Admission is shared. Newly decoded page observations remain local to each session.
    internal MapPageBudget Share(MapSurfaceAcquisitionWork? work = null) => new(_admission, work ?? Work);
    internal int Reads;
    internal MapSurfaceAcquisitionWork? Work { get; }
    internal event System.Action? PageVisiting;
    internal event System.Action? MetadataInspecting;
    internal event System.Action? RangeAdding;

    internal void BeforeMetadata()
    {
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageMetadataChecks.1", "MaxPageMetadataChecks", _admission.Metadata, 1, _admission.RangeLimit, _admission.Metadata >= _admission.RangeLimit, CapacityDemandLifetime.Cumulative, allowanceSource: _admission)) throw new MapSurfaceCapacityException("MaxPageMetadataChecks", (decimal)_admission.Metadata + 1, _admission.RangeLimit);
        MetadataInspecting?.Invoke();
        _admission.Metadata++;
        if (Work is { } observation) observation.MetadataChecks++;
    }

    internal void BeforeVisit()
    {
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageReads.1", "MaxPageReads", _admission.Visits, 1, _admission.Limit, _admission.Visits >= _admission.Limit, CapacityDemandLifetime.Cumulative, allowanceSource: _admission.Source)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxPageReads), (long)_admission.Visits + 1, _admission.Limit);
        PageVisiting?.Invoke();
        _admission.Visits++;
        if (Work is { } observation) observation.PageVisits++;
    }

    internal void BeforeRange()
    {
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageRangeOperations.1", "MaxPageRangeOperations", _admission.Ranges, 1, _admission.RangeLimit, _admission.Ranges >= _admission.RangeLimit, CapacityDemandLifetime.Cumulative, allowanceSource: _admission)) throw new MapSurfaceCapacityException("MaxPageRangeOperations", (decimal)_admission.Ranges + 1, _admission.RangeLimit);
        RangeAdding?.Invoke();
        _admission.Ranges++;
        if (Work is { } observation) observation.RangeOperations++;
    }

    internal void BeforeRead()
    {
        // Storage reads also use this budget outside queries. Cache hits never affect decoded statistics.
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageReads.2", "MaxPageReads", _admission.Attempts, 1, _admission.Limit, _admission.Attempts >= _admission.Limit, CapacityDemandLifetime.Cumulative, allowanceSource: _admission.Source)) throw new MapSurfaceCapacityException(nameof(MapQueryLimits.MaxPageReads), (long)_admission.Attempts + 1, _admission.Limit);
        _admission.Attempts++;
        if (Work is { } observation) observation.DecodeAttempts++;
    }
}
