using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapRenderHandle(long Value);

/// <summary>Measures before work. BeginUpload must claim the supplied reservation before any upload factory runs.</summary>
public interface IMapRenderDevice
{
    SceneUploadDemand MeasureUpload(MapPreparedResource resource);
    SceneUploadCapacity MeasureUploadCapacity(MapPreparedResource resource) => MeasureUpload(resource).Capacity;
    IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation);
    void Release(MapRenderHandle handle);
}

public interface IMapRenderUpload : IDisposable
{
    bool Ready { get; }
    long Advance(long transferBytes);
    MapRenderHandle Publish();
}
