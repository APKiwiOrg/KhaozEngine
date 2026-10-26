using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Shared context allocations retained by a product, with attribution in its owning window.</summary>
internal sealed class MapPreparationDependencies : IDisposable
{
    sealed record Piece(long Bytes, MapPreparationInventory Lease);
    readonly Dictionary<object, Piece> _pieces = new(ReferenceEqualityComparer.Instance);
    internal long Bytes => _pieces.Values.Sum(p => p.Bytes);
    internal long AddProduct(MapPreparationReservation destination, IMapPreparationSharedProduct product)
    {
        product = product.For(destination.Admission);
        if (_pieces.TryGetValue(product, out var prior)) return prior.Bytes;
        var lease = product.Acquire(destination);
        try { _pieces.Add(product, new(lease.Bytes, lease)); return lease.Bytes; }
        catch { lease.Dispose(); throw; }
    }
    internal void Add(MapSceneAdmission admission, object identity, long bytes,
        MapPreparationReservation? ownership = null, MapPreparationReservation? windowOwner = null,
        MapPreparationReservation? workspace = null)
    {
        if (_pieces.ContainsKey(identity)) return;
        var lease = MapPreparationInventory.Acquire(admission, [(identity, bytes)], workspace: workspace,
            ownership: ownership, windowOwner: windowOwner, admitWorkspace: workspace is not null);
        _pieces.Add(identity, new(bytes, lease));
    }
    internal long UniqueBytes(HashSet<object> seen) => _pieces.Where(p => seen.Add(p.Key)).Sum(p => p.Value.Bytes);
    internal MapPreparationDependencies Rebind(MapPreparationReservation destination, MapPreparationReservation? workspace = null)
    {
        var result = new MapPreparationDependencies();
        try
        {
            foreach (var piece in _pieces)
                if (piece.Key is IMapPreparationSharedProduct product) result.AddProduct(destination, product);
                else result.Add(destination.Admission, piece.Key, piece.Value.Bytes, windowOwner: destination, workspace: workspace);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal void MoveTo(MapPreparationDependencies destination)
    {
        foreach (var piece in _pieces)
            if (!destination._pieces.TryAdd(piece.Key, piece.Value)) piece.Value.Lease.Dispose();
        _pieces.Clear();
    }
    public void Dispose()
    {
        foreach (var piece in _pieces.Values) piece.Lease.Dispose();
        _pieces.Clear();
    }
}
