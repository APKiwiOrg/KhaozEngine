namespace KhaozEngine.MapDoc.Storage;

/// <summary>Before-work admission for traversal, rectangle bookkeeping, metadata and page decodes.</summary>
internal sealed class MapPageBudget(int limit, MapSurfaceAcquisitionWork? work = null)
{
    internal int Reads;
    internal MapSurfaceAcquisitionWork? Work { get; } = work;
    readonly int _limit = limit;
    int _attempts;
    int _visits;
    long _ranges;
    long _metadata;
    // Each admitted page has at most 256 entries. Splitting one rectangle emits at most four pieces.
    readonly long _rangeLimit = 1L + 4L * MapSurfacePages.MaxEntries * limit;

    internal void BeforeMetadata()
    {
        if (_metadata >= _rangeLimit) throw new MapSurfaceCapacityException();
        _metadata++;
        if (Work is { } observation) observation.MetadataChecks++;
    }

    internal void BeforeVisit()
    {
        if (_visits >= _limit) throw new MapSurfaceCapacityException();
        _visits++;
        if (Work is { } observation) observation.PageVisits++;
    }

    internal void BeforeRange()
    {
        if (_ranges >= _rangeLimit) throw new MapSurfaceCapacityException();
        _ranges++;
        if (Work is { } observation) observation.RangeOperations++;
    }

    internal void BeforeRead()
    {
        // Storage reads also use this budget outside queries. Cache hits never affect decoded statistics.
        if (_attempts >= _limit) throw new MapSurfaceCapacityException();
        _attempts++;
        if (Work is { } observation) observation.DecodeAttempts++;
    }
}
