namespace KhaozEngine.MapDoc.Storage;

/// <summary>Observations only. Acquisition authority stays in the request and producer session.</summary>
internal sealed class MapAcquisitionWork
{
    internal long LargestBoundSlotRange;
    internal long BoundSlotVisits;
}
