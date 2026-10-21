using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Only the renderer's final destruction callback returns claimed credit.</summary>
public sealed class MapUploadReservation : ISceneUploadReservation
{
    readonly object _gate = new();
    readonly MapRenderAdmission _owner;
    readonly long _gpu;
    Action? _release;
    internal void OnRelease(Action release) { lock (_gate) { if (_released) throw new ObjectDisposedException(nameof(MapUploadReservation)); _release += release; } }
    bool _claimed, _published, _retiring, _released;
    public SceneUploadDemand Demand { get; }
    internal MapUploadReservation(MapRenderAdmission owner, SceneUploadDemand demand, long gpu)
    { _owner = owner; Demand = demand; _gpu = gpu; }
    public void Claim(SceneUploadDemand demand)
    {
        lock (_gate)
        {
            if (_released || _claimed || demand != Demand) throw new InvalidOperationException("Reservation is unavailable or its renderer demand changed.");
            _claimed = true; _owner.Lifetime.Allocated();
        }
    }
    public void Published(SceneResourceHandle handle)
    {
        lock (_gate)
        {
            if (!_claimed || _released || _retiring || _published || handle.Value == 0) throw new InvalidOperationException("Reservation cannot be published.");
            _published = true;
        }
    }
    public void Retiring() { lock (_gate) { if (!_released) _retiring = true; } }
    public void Released()
    {
        lock (_gate)
        {
            if (_released) return;
            if (_claimed && !_retiring) throw new InvalidOperationException("A claimed reservation releases after renderer retirement.");
            _released = true; try { _owner.ReleaseUpload(Demand.PeakCpuBytes, _gpu, _claimed); }
            finally { _release?.Invoke(); _release = null; }
        }
    }
    public void Dispose() { lock (_gate) { if (!_claimed) Released(); } }
}
