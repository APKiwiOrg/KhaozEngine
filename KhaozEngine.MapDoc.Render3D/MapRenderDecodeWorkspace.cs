using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Mirrors actual decoder ownership and temporary reservations into the enclosing live ledger.</summary>
internal sealed class MapRenderDecodeWorkspace : IGltfDecodeReservationObserver, IDisposable
{
    readonly GltfDecodeBudget _budget;
    readonly MapPreparationReservation? _workspace;
    readonly bool _observing;
    bool _disposed;
    internal MapPreparationReservation? Workspace => _workspace;
    internal GltfDecodeBudget Budget => _budget;
    internal MapRenderDecodeWorkspace(GltfDecodeBudget budget)
    {
        _budget = budget.ReservationView();
        _workspace = MapSceneAdmission.Current?.Workspace();
        try { _observing = _workspace is not null && _budget.Observe(this); }
        catch { _workspace?.Dispose(); throw; }
    }
    public void Reserve(long bytes) => _workspace?.Reserve(bytes);
    public void Release(long bytes) => _workspace?.Release(bytes);
    internal void Finish() { if (_observing) _budget.StopObserving(this); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Finish();
        _workspace?.Dispose();
    }
}
