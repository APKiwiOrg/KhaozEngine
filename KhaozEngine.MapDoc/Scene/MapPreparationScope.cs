using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Owns one synchronous operation's workspace and publishes explicit retained inventory.</summary>
internal sealed class MapPreparationScope : IDisposable
{
    readonly MapSceneAdmission _admission;
    readonly MapPreparationScope? _previous;
    readonly MapPreparationReservation _workspace;
    readonly List<MapPreparationReservation> _products = new();
    List<IDisposable>? _lifetimes;
    bool _completed, _disposed;
    internal long Bytes => _workspace.Bytes;
    internal MapPreparationScope(MapSceneAdmission admission)
    {
        _admission = admission; _previous = admission.PreparationScope;
        _workspace = admission.Workspace(); admission.PreparationScope = this;
    }
    internal void Reserve(long bytes) => _workspace.Reserve(bytes);
    internal void Release(long bytes) => _workspace.Release(bytes);
    internal void Track(MapPreparationReservation product) => _products.Add(product);
    internal void TrackParentCleanup(IDisposable product)
    { if (_previous is { } parent) (parent._lifetimes ??= new()).Add(product); }
    internal void Publish(MapPreparationReservation product)
    {
        if (!_products.Remove(product)) throw new InvalidOperationException("The product is not owned by this preparation collector.");
    }
    internal MapPreparationReservation Complete(long bytes, bool publishToParent = true)
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
        foreach (var product in _products) _workspace.Absorb(product);
        if (bytes > Bytes) Reserve(bytes - Bytes);
        var result = _workspace.Transfer(bytes);
        if (publishToParent) _previous?.Track(result);
        _completed = true;
        return result;
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_admission.PreparationScope != this) throw new InvalidOperationException("Preparation scopes must close in nesting order.");
        _admission.PreparationScope = _previous; _disposed = true;
        foreach (var product in _products) product.Dispose();
        foreach (var lifetime in _lifetimes ?? []) lifetime.Dispose();
        _workspace.Dispose();
    }
}
