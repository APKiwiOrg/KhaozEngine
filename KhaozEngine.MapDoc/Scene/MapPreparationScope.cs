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
    internal MapPreparationReservation WorkspaceOwner => _workspace;
    internal MapPreparationScope(MapSceneAdmission admission)
    {
        _admission = admission; _previous = admission.PreparationScope;
        _workspace = admission.Workspace(); admission.PreparationScope = this;
    }
    internal void Reserve(long bytes) => _workspace.Reserve(bytes);
    internal void Release(long bytes) => _workspace.Release(bytes);
    internal void AdoptWorkspace(MapPreparationReservation source, long bytes) => source.MoveWorkspaceTo(_workspace, bytes);
    internal void Track(MapPreparationReservation product) => _products.Add(product);
    internal void TrackParentCleanup(IDisposable product)
    { if (_previous is { } parent) (parent._lifetimes ??= new()).Add(product); }
    internal void Publish(MapPreparationReservation product)
    {
        if (!_products.Remove(product)) throw new InvalidOperationException("The product is not owned by this preparation collector.");
    }
    internal void Export(MapPreparationReservation product, MapPreparationScope destination)
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed || destination._disposed || destination._completed, this);
        if (!_products.Contains(product)) throw new InvalidOperationException("The product is not owned by this preparation collector.");
        product.ReattributeTo(destination._workspace);
        destination.Track(product);
        _products.Remove(product);
    }
    internal void EnsureCapacity(long bytes, MapPreparationReservation? dependency = null, bool retainDependencies = true)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long available = _workspace.InventoryBytes(seen);
        foreach (var product in _products) available = checked(available + product.InventoryBytes(seen, retainDependencies));
        if (dependency is not null && !_products.Contains(dependency)) available = checked(available + dependency.InventoryBytes(seen));
        if (bytes > available) Reserve(bytes - available);
    }
    internal long DeduplicateSharedProducts(long reachableBytes, long sharedReachableBytes, MapPreparationReservation? dependency = null)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long shared = _workspace.SharedProductBytes(seen);
        foreach (var product in _products) shared = checked(shared + product.SharedProductBytes(seen));
        if (dependency is not null && !_products.Contains(dependency))
            shared = checked(shared + dependency.SharedProductBytes(seen));
        if (shared > sharedReachableBytes || sharedReachableBytes > reachableBytes || sharedReachableBytes > 0 && shared == 0)
            throw new InvalidOperationException("Shared product inventory does not match its retained dependencies.");
        return checked(reachableBytes - sharedReachableBytes + shared);
    }
    internal MapPreparationReservation Complete(long bytes, bool publishToParent = true, bool retainDependencies = true)
    {
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
        // Shared dependencies follow the returned graph. Value-only consumers explicitly end their input leases.
        EnsureCapacity(bytes, retainDependencies: retainDependencies);
        foreach (var product in _products) _workspace.Absorb(product, retainDependencies);
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
        if (_lifetimes is { } lifetimes) foreach (var lifetime in lifetimes) lifetime.Dispose();
        _workspace.Dispose();
    }
}
