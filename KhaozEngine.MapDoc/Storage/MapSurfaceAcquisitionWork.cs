namespace KhaozEngine.MapDoc.Storage;

/// <summary>Observations only. Private budget counters admit work independently of these values.</summary>
internal sealed class MapSurfaceAcquisitionWork
{
    internal int PageVisits;
    internal int DecodeAttempts;
    internal long MetadataChecks;
    internal long RangeOperations;
    internal int SurfaceLookups;
    internal long LookupNodeInspections;
    internal int DirectoryCallbacks;
    internal int IndexCallbacks;
    internal int PayloadReads;
    internal int Disposals;
}
