namespace KhaozEngine.MapDoc.Storage;

/// <summary>Before-work admission for traversal, rectangle bookkeeping, metadata and page decodes.</summary>
internal sealed class MapPageBudget
{
    sealed class Admission(int limit)
    {
        internal readonly int Limit = limit;
        internal readonly long RangeLimit = 1L + 4L * MapSurfacePages.MaxEntries * limit;
        internal int Attempts, Visits;
        internal long Ranges, Metadata;
    }
    readonly Admission _admission;
    internal MapPageBudget(int limit, MapSurfaceAcquisitionWork? work = null) : this(new Admission(limit), work) { }
    MapPageBudget(Admission admission, MapSurfaceAcquisitionWork? work) { _admission = admission; Work = work; }
    // Admission is shared. Newly decoded page observations remain local to each session.
    internal MapPageBudget Share(MapSurfaceAcquisitionWork? work = null) => new(_admission, work ?? Work);
    internal int Reads;
    internal MapSurfaceAcquisitionWork? Work { get; }

    internal void BeforeMetadata()
    {
        if (_admission.Metadata >= _admission.RangeLimit) throw new MapSurfaceCapacityException();
        _admission.Metadata++;
        if (Work is { } observation) observation.MetadataChecks++;
    }

    internal void BeforeVisit()
    {
        if (_admission.Visits >= _admission.Limit) throw new MapSurfaceCapacityException();
        _admission.Visits++;
        if (Work is { } observation) observation.PageVisits++;
    }

    internal void BeforeRange()
    {
        if (_admission.Ranges >= _admission.RangeLimit) throw new MapSurfaceCapacityException();
        _admission.Ranges++;
        if (Work is { } observation) observation.RangeOperations++;
    }

    internal void BeforeRead()
    {
        // Storage reads also use this budget outside queries. Cache hits never affect decoded statistics.
        if (_admission.Attempts >= _admission.Limit) throw new MapSurfaceCapacityException();
        _admission.Attempts++;
        if (Work is { } observation) observation.DecodeAttempts++;
    }
}
