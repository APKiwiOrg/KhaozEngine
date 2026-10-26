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
    internal static MapPreparationInventory Own(long bytes, params IDisposable?[] pieces)
    {
        var owned = new List<IDisposable>(pieces.Length);
        foreach (var piece in pieces) if (piece is not null) owned.Add(piece);
        return new(owned, bytes);
    }
    // A frozen arena has one fixed set of identities. Its registry dies with that arena's last lease,
    // keeping directory high-water storage out of the long-lived source inventory registry.
    internal static MapPreparationInventory AcquireIsolated(MapSceneAdmission admission,
        IEnumerable<(object Identity, long Bytes)> inventory, MapPreparationReservation workspace) =>
        new Registry(admission).Acquire(inventory, workspace, null, workspace, false);
    MapPreparationInventory(List<IDisposable> pieces, long bytes) => _shared = new(pieces) { Bytes = bytes };
    MapPreparationInventory(Shared shared) => _shared = shared;
    internal static MapPreparationInventory Acquire(MapSceneAdmission admission,
        IEnumerable<(object Identity, long Bytes)> inventory, MapPreparationReservation? workspace = null,
        MapPreparationReservation? ownership = null, MapPreparationReservation? windowOwner = null, bool admitWorkspace = false) =>
        Registries.GetValue(admission, static a => new(a)).Acquire(inventory, workspace, ownership, windowOwner, admitWorkspace);
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
        internal MapPreparationInventory Acquire(IEnumerable<(object Identity, long Bytes)> inventory, MapPreparationReservation? workspace,
            MapPreparationReservation? ownership, MapPreparationReservation? windowOwner, bool admitWorkspace)
        {
            lock (_gate)
            {
                var parts = inventory.DistinctBy(i => i.Identity, ReferenceEqualityComparer.Instance).ToArray();
                long newBytes = parts.Where(p => !_entries.ContainsKey(p.Identity)).Sum(p => p.Bytes);
                // Foreign dependencies adopt their portion of destination workspace, reserving only missing identities.
                if (admitWorkspace) workspace!.Reserve(newBytes);
                if (workspace is not null && newBytes > workspace.Bytes)
                    throw new InvalidOperationException("Decoded inventory exceeds its admitted owned reservation.");
                if (ownership is not null && newBytes != ownership.Bytes)
                    throw new InvalidOperationException("Shared inventory must adopt exactly its precharged owned allocation.");
                var leases = new List<IDisposable>();
                try
                {
                    object window = windowOwner?.WindowIdentity ?? admission.WindowIdentity;
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
                            entry = new(retained, window);
                            _entries.Add(part.Identity, entry);
                        }
                        var windows = new List<MapPreparationLedger.Window>();
                        MapPreparationLedger.Window? binding = null;
                        for (var current = window as MapPreparationLedger.Window; current is not null; current = current.Parent)
                        {
                            if (Covers(current, entry.Origin)) break;
                            windows.Add(current);
                            if (!entry.Views.ContainsKey(current) && (binding is null || current.Remaining < binding.Remaining))
                                binding = current;
                        }
                        if (binding is not null && entry.Reservation.Bytes > binding.Remaining)
                            throw new MapSceneCapacityException(binding.RefuseReference(entry.Reservation.Bytes,
                                windows[0], windows[^1].Parent, entry.Views.Keys));
                        var covered = new List<(object Key, View Value)>();
                        try
                        {
                            // Each borrowing window has one attribution, shared by every consumer using it.
                            foreach (var current in windows)
                            {
                                if (!entry.Views.TryGetValue(current, out var view))
                                {
                                    view = new(current.ReferenceOnly(entry.Reservation.Bytes));
                                    entry.Views.Add(current, view);
                                }
                                view.Owners++;
                                covered.Add((current, view));
                            }
                        }
                        catch { ReleaseViews(); throw; }
                        entry.Owners++;
                        leases.Add(new PieceLease(() =>
                        {
                            lock (_gate)
                            {
                                ReleaseViews();
                                if (--entry.Owners == 0) { entry.Reservation.Dispose(); _entries.Remove(part.Identity); }
                            }
                        }));
                        void ReleaseViews()
                        {
                            foreach (var ancestor in covered)
                                if (--ancestor.Value.Owners == 0)
                                { ancestor.Value.Reference?.Dispose(); entry.Views.Remove(ancestor.Key); }
                        }
                    }
                    return new(leases, parts.Sum(p => p.Bytes));
                }
                catch { foreach (var lease in leases) lease.Dispose(); throw; }
            }
        }
        static bool Covers(object ancestor, object window)
        {
            if (ReferenceEquals(ancestor, window)) return true;
            for (var current = window as MapPreparationLedger.Window; current is not null; current = current.Parent)
                if (ReferenceEquals(ancestor, current)) return true;
            return false;
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
