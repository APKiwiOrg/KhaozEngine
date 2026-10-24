using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Unique shared allocation receipts per attempt, with selected obligations in every borrowing view.</summary>
internal sealed class MapPreparationInventory : IDisposable
{
    static readonly ConditionalWeakTable<MapSceneAdmission, Registry> Registries = new();
    sealed class Shared(List<IDisposable> pieces)
    { internal readonly List<IDisposable> Pieces = pieces; internal int Owners = 1; internal long Bytes; }
    readonly Shared _shared;
    bool _disposed;
    internal long Bytes => _shared.Bytes;
    MapPreparationInventory(List<IDisposable> pieces, long bytes) => _shared = new(pieces) { Bytes = bytes };
    MapPreparationInventory(Shared shared) => _shared = shared;
    internal static MapPreparationInventory Acquire(MapSceneAdmission admission,
        IEnumerable<(object Identity, long Bytes)> inventory, MapPreparationReservation? workspace = null,
        MapPreparationReservation? ownership = null) =>
        Registries.GetValue(admission, static a => new(a)).Acquire(inventory, workspace, ownership);
    internal MapPreparationInventory Retain()
    {
        lock (_shared.Pieces)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked { _shared.Owners++; }
            return new(_shared);
        }
    }
    internal void Release() => Dispose();
    public void Dispose()
    {
        lock (_shared.Pieces)
        {
            if (_disposed) return; _disposed = true;
            if (--_shared.Owners != 0) return;
            foreach (var piece in _shared.Pieces) piece.Dispose();
            _shared.Pieces.Clear();
        }
    }
    sealed class Registry(MapSceneAdmission admission)
    {
        readonly object _gate = new();
        readonly Dictionary<object, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        internal MapPreparationInventory Acquire(IEnumerable<(object Identity, long Bytes)> inventory, MapPreparationReservation? workspace, MapPreparationReservation? ownership)
        {
            lock (_gate)
            {
                var parts = inventory.DistinctBy(i => i.Identity, ReferenceEqualityComparer.Instance).ToArray();
                long newBytes = parts.Where(p => !_entries.ContainsKey(p.Identity)).Sum(p => p.Bytes);
                if (workspace is not null && newBytes > workspace.Bytes)
                    throw new InvalidOperationException("Decoded inventory exceeds its admitted owned reservation.");
                if (ownership is not null && newBytes != ownership.Bytes)
                    throw new InvalidOperationException("Shared inventory must adopt exactly its precharged owned allocation.");
                var leases = new List<IDisposable>();
                try
                {
                    foreach (var part in parts)
                    {
                        if (!_entries.TryGetValue(part.Identity, out var entry))
                        {
                            MapPreparationReservation retained;
                            if (ownership is not null) retained = ownership.SplitRetained(part.Bytes);
                            else if (workspace is not null) retained = workspace.Transfer(part.Bytes);
                            else
                            {
                                using var reserve = admission.Workspace(); reserve.Reserve(part.Bytes);
                                retained = reserve.Transfer(part.Bytes);
                            }
                            entry = new(retained, admission.WindowIdentity);
                            _entries.Add(part.Identity, entry);
                        }
                        var window = admission.WindowIdentity;
                        if (!entry.Views.TryGetValue(window, out var view))
                        {
                            view = new(window == entry.Origin ? null : admission.ReferenceWindow(entry.Reservation.Bytes, entry.Origin, entry.Views.Keys));
                            entry.Views.Add(window, view);
                        }
                        view.Owners++; entry.Owners++;
                        leases.Add(new PieceLease(() =>
                        {
                            lock (_gate)
                            {
                                if (--view.Owners == 0) { view.Reference?.Dispose(); entry.Views.Remove(window); }
                                if (--entry.Owners == 0) { entry.Reservation.Dispose(); _entries.Remove(part.Identity); }
                            }
                        }));
                    }
                    return new(leases, parts.Sum(p => p.Bytes));
                }
                catch { foreach (var lease in leases) lease.Dispose(); throw; }
            }
        }
    }
    sealed class Entry(MapPreparationReservation reservation, object origin)
    {
        internal MapPreparationReservation Reservation => reservation;
        internal object Origin => origin;
        internal int Owners;
        internal readonly Dictionary<object, View> Views = new(ReferenceEqualityComparer.Instance);
    }
    sealed class View(IDisposable? reference)
    { internal int Owners; internal IDisposable? Reference => reference; }
    sealed class PieceLease(Action release) : IDisposable
    {
        bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; release(); }
    }
}
