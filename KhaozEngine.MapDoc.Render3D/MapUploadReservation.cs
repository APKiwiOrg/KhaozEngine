using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Renderer-confirmed transient release settles peak credit. Final destruction returns the remainder.</summary>
public sealed class MapUploadReservation : ISceneUploadReservation
{
    readonly object _gate = new();
    readonly MapRenderAdmission _owner;
    long _gpu, _cpu;
    Action? _release;
    internal void OnRelease(Action release) { lock (_gate) { if (_released) throw new ObjectDisposedException(nameof(MapUploadReservation)); _release += release; } }
    bool _claimed, _published, _retiring, _released, _transferCompleted, _inputsReleased;
    public SceneUploadDemand Demand { get; }
    internal MapUploadReservation(MapRenderAdmission owner, SceneUploadDemand demand, long gpu)
    { _owner = owner; Demand = demand; _gpu = gpu; _cpu = demand.PeakCpuBytes; }
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
    public void TransferCompleted()
    {
        lock (_gate)
        {
            if (_released || _transferCompleted) return;
            if (!_claimed || _retiring) throw new InvalidOperationException("Only a live claimed upload can complete transfer.");
            long staging = _gpu - Demand.OwnedGpuBytes;
            _gpu = Demand.OwnedGpuBytes; _transferCompleted = true;
            _owner.ReleaseUpload(0, staging, false);
        }
    }
    public void InputsReleased(long retainedCpuBytes)
    {
        lock (_gate)
        {
            if (_released) return;
            if (!_claimed || !_published || _retiring || retainedCpuBytes < 0 || retainedCpuBytes > _cpu ||
                (_inputsReleased && retainedCpuBytes != _cpu))
                throw new InvalidOperationException("Only published input release can settle its reserved CPU inventory.");
            if (_inputsReleased) return;
            long transient = _cpu - retainedCpuBytes;
            _cpu = retainedCpuBytes; _inputsReleased = true;
            _owner.ReleaseUpload(transient, 0, false);
        }
    }
    public void Retiring() { lock (_gate) { if (!_released) _retiring = true; } }
    public void Released()
    {
        lock (_gate)
        {
            if (_released) return;
            if (_claimed && !_retiring) throw new InvalidOperationException("A claimed reservation releases after renderer retirement.");
            _released = true; try { _owner.ReleaseUpload(_cpu, _gpu, _claimed); }
            finally { _release?.Invoke(); _release = null; }
        }
    }
    public void Dispose() { lock (_gate) { if (!_claimed) Released(); } }
}
