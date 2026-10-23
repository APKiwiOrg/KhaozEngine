using System;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Owns one synchronous operation's workspace and publishes explicit retained inventory.</summary>
internal sealed class MapPreparationScope : IDisposable
{
    readonly MapSceneAdmission _admission;
    readonly MapPreparationScope? _previous;
    readonly MapPreparationReservation _workspace;
    bool _completed, _disposed;
    internal long Bytes => _workspace.Bytes;
    internal MapPreparationScope(MapSceneAdmission admission)
    {
        _admission = admission; _previous = admission.PreparationScope;
        _workspace = admission.Workspace(); admission.PreparationScope = this;
    }
    internal void Reserve(long bytes) => _workspace.Reserve(bytes);
    internal void Release(long bytes) => _workspace.Release(bytes);
    internal MapPreparationReservation Complete(long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
        if (bytes > Bytes) Reserve(bytes - Bytes);
        var result = _workspace.Transfer(bytes);
        _completed = true;
        return result;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_admission.PreparationScope != this) throw new InvalidOperationException("Preparation scopes must close in nesting order.");
        _admission.PreparationScope = _previous; _disposed = true; _workspace.Dispose();
    }
}
