using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
namespace KhaozEngine.MapDoc.Storage;

/// <summary>Before-work admission for traversal, rectangle bookkeeping, metadata and page decodes.</summary>
internal sealed class MapPageBudget
{
    sealed class Admission(int limit, object? source)
    {
        internal readonly int Limit = limit;
        internal readonly object? Source = source;
        internal readonly long RangeLimit = Ceiling(limit);
        internal int Attempts, Visits;
        internal long Ranges, Metadata;
    }

    /// <summary>The metadata and range ceiling a page-read allowance implies, one check plus a full page of entries in
    /// each of four index levels per page read.</summary>
    internal static long Ceiling(int pageReads) => 1L + 4L * MapSurfacePages.MaxEntries * pageReads;

    /// <summary>The demand a scaled ceiling's check places on each origin of the caller's MaxPageReads, in whole page
    /// reads. The ceiling admits a count of <paramref name="checks"/> + 1 exactly when ceil(checks / (4 * MaxEntries)) page
    /// reads fit, so each origin compares that demand in its own field's units and refuses exactly when the gate does.
    /// Call only while a demand observer is subscribed.</summary>
    internal static (CapacityDemandOrigin Origin, decimal Increment, bool Over, bool Saturated)[] PageDemand(object source, int pageReads, long checks)
    {
        decimal pages = System.Math.Ceiling(checks / (4m * MapSurfacePages.MaxEntries));
        var origins = Scene.MapDemandOrigins.Of(source, nameof(MapQueryLimits.MaxPageReads), pageReads);
        var demand = new (CapacityDemandOrigin, decimal, bool, bool)[origins.Length];
        for (int i = 0; i < origins.Length; i++)
        {
            var origin = origins[i];
            try
            {
                decimal increment = origin.Scale * pages;
                demand[i] = (origin, increment, origin.Consumed + increment > origin.Allowance, false);
            }
            catch (System.OverflowException) { demand[i] = (origin, decimal.MaxValue, true, true); }
        }
        return demand;
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
        if (_admission.Source is { } source && DemandDiagnostics.IsObserved)
            foreach (var (origin, increment, over, saturated) in PageDemand(source, _admission.Limit, _admission.Metadata))
                DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageMetadataChecks.2", origin.LimitName, origin.Consumed, increment, origin.Allowance, over, CapacityDemandLifetime.Cumulative, origin.Identity, saturated);
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageMetadataChecks.1", "MaxPageMetadataChecks", _admission.Metadata, 1, _admission.RangeLimit, _admission.Metadata >= _admission.RangeLimit, CapacityDemandLifetime.Cumulative)) throw new MapSurfaceCapacityException("MaxPageMetadataChecks", (decimal)_admission.Metadata + 1, _admission.RangeLimit);
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
        if (_admission.Source is { } source && DemandDiagnostics.IsObserved)
            foreach (var (origin, increment, over, saturated) in PageDemand(source, _admission.Limit, _admission.Ranges))
                DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageRangeOperations.2", origin.LimitName, origin.Consumed, increment, origin.Allowance, over, CapacityDemandLifetime.Cumulative, origin.Identity, saturated);
        if (DemandDiagnostics.Check(_admission, "MapPageBudget.MaxPageRangeOperations.1", "MaxPageRangeOperations", _admission.Ranges, 1, _admission.RangeLimit, _admission.Ranges >= _admission.RangeLimit, CapacityDemandLifetime.Cumulative)) throw new MapSurfaceCapacityException("MaxPageRangeOperations", (decimal)_admission.Ranges + 1, _admission.RangeLimit);
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
