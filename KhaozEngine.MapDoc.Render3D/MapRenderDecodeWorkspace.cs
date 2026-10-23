using System;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>The decoder's already bounded peak is live workspace until selected input ownership takes over.</summary>
internal sealed class MapRenderDecodeWorkspace : IDisposable
{
    [ThreadStatic] static GltfDecodeBudget? t_budget;
    readonly GltfDecodeBudget? _previous;
    readonly MapPreparationReservation? _workspace;
    bool _disposed;
    internal MapRenderDecodeWorkspace(GltfDecodeBudget budget)
    {
        _previous = t_budget;
        if (_previous != budget)
        {
            _workspace = MapSceneAdmission.Current?.Workspace();
            try { _workspace?.Reserve(budget.RemainingBytes); }
            catch { _workspace?.Dispose(); throw; }
        }
        t_budget = budget;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; t_budget = _previous; _workspace?.Dispose();
    }
}
