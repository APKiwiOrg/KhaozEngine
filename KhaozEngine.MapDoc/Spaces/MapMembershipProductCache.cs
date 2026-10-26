using System;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Attempt-scoped immutable footprints. Each window borrows a whole product once.</summary>
internal sealed class MapMembershipProductCache
{
    static readonly ConditionalWeakTable<MapSceneAdmission, MapMembershipProductCache> Caches = new();
    readonly MapSceneAdmission _admission;
    readonly MapPreparationReservation _control;
    Entry? _head;
    MapMembershipProductCache(MapSceneAdmission admission, MapPreparationReservation control)
    { _admission = admission; _control = control; }
    static MapMembershipProductCache For(MapSceneAdmission admission)
    {
        if (Caches.TryGetValue(admission, out var cache)) return cache;
        using var control = admission.Workspace();
        control.Reserve(256);
        cache = new(admission, control.Transfer(256));
        try { Caches.Add(admission, cache); return cache; }
        catch { cache._control.Dispose(); throw; }
    }
    internal static (MapMembershipPolygonRange Geometry, long SharedBytes) Acquire(MapMembershipBuild build, MapSpaceFootprint footprint)
    {
        var cache = For(build.Admission);
        Entry? entry = null;
        try
        {
            for (var candidate = cache._head; candidate is not null; candidate = candidate.Next)
                if (candidate.Identity.Matches(build.Scene!, footprint)) { entry = candidate; break; }
            bool hit = entry is not null;
            entry ??= cache.Create(build.Scene!, footprint);
            long bytes = build.Product.DependOnProduct(entry);
            AddCounts(build.Counts, entry.Counts);
            if (hit) build.Counts.FootprintCacheHits++;
            return (entry.Geometry, bytes);
        }
        catch { entry?.AbandonIfUnused(); cache.EndIfEmpty(); throw; }
    }
    Entry Create(MapScopedScene scene, MapSpaceFootprint footprint)
    {
        using var preparation = _admission.BeginPreparation();
        long baseBytes = checked(4096L + ((24L + 4L * footprint.SlotCells.Count + 7) & ~7L));
        preparation.Reserve(baseBytes);
        var counts = new MapRoofPreparationCounts();
        using var build = new MapMembershipBuild(_admission, preparation, counts, scene);
        var identity = MapMembershipFootprintIdentity.Capture(scene, footprint);
        MapMembershipGeometryArena arena;
        using (var building = new MapMembershipArenaBuilder.Scope(MapMembershipGeometryArena.Create()))
        {
            arena = building.Arena;
            foreach (int slot in footprint.SlotCells)
                _ = new MapMembershipPolygon(MapEffectiveGeometry.CellPolygon(scene.SurfaceView, footprint.Lattice, slot), cell: true);
            counts.UniqueFootprintPolygons = footprint.SlotCells.Count;
        }
        int identities = checked(arena.Inventory().Count() + 1);
        // Fixed per-identity capacity includes receipts, registry rows, delegates and old/new lookup backing.
        // It is admitted before creating the registry or materializing its identity directory.
        long metadata = checked(baseBytes + 512L * identities);
        preparation.Reserve(metadata - baseBytes);
        var entry = new Entry(this, identity, new(arena, 0, footprint.SlotCells.Count), counts,
            preparation.WorkspaceOwner.WindowIdentity);
        var parts = arena.Inventory().Append(((object)entry, metadata)).ToArray();
        entry.Root = MapPreparationInventory.AcquireIsolated(_admission, parts, preparation.WorkspaceOwner);
        entry.Next = _head; _head = entry;
        return entry;
    }
    Entry Import(Entry source, MapPreparationReservation destination)
    {
        foreach (var candidate in Entries()) if (candidate.Identity.Equals(source.Identity)) return candidate;
        using var workspace = _admission.Workspace();
        // Adoption into another ledger admits its full obligation before the earlier ledger retires.
        workspace.Reserve(source.Root.Bytes);
        var entry = new Entry(this, source.Identity, source.Geometry, source.Counts, destination.WindowIdentity);
        var parts = source.Geometry.Arena.Inventory().Append(((object)entry,
            checked(source.Root.Bytes - source.Geometry.Arena.InventoryBytes))).ToArray();
        entry.Root = MapPreparationInventory.AcquireIsolated(_admission, parts, workspace);
        entry.Next = _head; _head = entry;
        return entry;
    }
    System.Collections.Generic.IEnumerable<Entry> Entries()
    { for (var entry = _head; entry is not null; entry = entry.Next) yield return entry; }
    void Remove(Entry entry)
    {
        if (ReferenceEquals(_head, entry)) _head = entry.Next;
        else
            for (var prior = _head; prior is not null; prior = prior.Next)
                if (ReferenceEquals(prior.Next, entry)) { prior.Next = entry.Next; break; }
        entry.Root.Dispose();
        EndIfEmpty();
    }
    void EndIfEmpty()
    { if (_head is null) { Caches.Remove(_admission); _control.Dispose(); } }
    sealed class Entry(MapMembershipProductCache cache, MapMembershipFootprintIdentity identity,
        MapMembershipPolygonRange geometry, MapRoofPreparationCounts counts, object origin) : IMapPreparationSharedProduct
    {
        internal readonly MapMembershipFootprintIdentity Identity = identity;
        internal readonly MapMembershipPolygonRange Geometry = geometry;
        internal readonly MapRoofPreparationCounts Counts = counts;
        internal MapPreparationInventory Root = null!;
        internal Entry? Next;
        Group? _groups;
        bool _retired;
        public IMapPreparationSharedProduct For(MapSceneAdmission admission)
        {
            if (ReferenceEquals(cache._admission, admission)) return this;
            var receiving = MapMembershipProductCache.For(admission);
            try
            {
                using var destination = admission.Workspace();
                return receiving.Import(this, destination);
            }
            catch { receiving.EndIfEmpty(); throw; }
        }
        public MapPreparationInventory Acquire(MapPreparationReservation destination)
        {
            if (!ReferenceEquals(cache._admission, destination.Admission)) return For(destination.Admission).Acquire(destination);
            try { return AcquireHere(destination); }
            catch { AbandonIfUnused(); throw; }
        }
        MapPreparationInventory AcquireHere(MapPreparationReservation destination)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            Group? group = _groups;
            while (group is not null && !ReferenceEquals(group.Window, destination.WindowIdentity)) group = group.Next;
            if (group is null)
            {
                using var workspace = cache._admission.Workspace();
                workspace.Reserve(512);
                IDisposable? reference = null;
                MapPreparationReservation? control = null;
                MapPreparationInventory? pages = null;
                try
                {
                    reference = destination.ReferenceProductWindow(Root.Bytes, origin);
                    control = workspace.Transfer(512);
                    pages = Root.Retain();
                    var inventory = MapPreparationInventory.Own(checked(Root.Bytes + 512), pages, control, reference);
                    group = new(destination.WindowIdentity, inventory) { Next = _groups };
                    _groups = group;
                }
                catch { pages?.Dispose(); control?.Dispose(); reference?.Dispose(); throw; }
            }
            checked { group.Consumers++; }
            return MapPreparationInventory.Own(group.Inventory.Bytes, new Consumer(this, group, group.Inventory.Retain()));
        }
        void Release(Group group)
        {
            if (--group.Consumers != 0) return;
            if (ReferenceEquals(_groups, group)) _groups = group.Next;
            else
                for (var prior = _groups; prior is not null; prior = prior.Next)
                    if (ReferenceEquals(prior.Next, group)) { prior.Next = group.Next; break; }
            group.Inventory.Dispose();
            AbandonIfUnused();
        }
        internal void AbandonIfUnused()
        { if (_groups is null && !_retired) { _retired = true; cache.Remove(this); } }
        sealed class Group(object window, MapPreparationInventory inventory)
        {
            internal readonly object Window = window;
            internal readonly MapPreparationInventory Inventory = inventory;
            internal Group? Next;
            internal int Consumers;
        }
        sealed class Consumer(Entry entry, Group group, MapPreparationInventory inventory) : IDisposable
        {
            bool _disposed;
            public void Dispose()
            { if (_disposed) return; _disposed = true; inventory.Dispose(); entry.Release(group); }
        }
    }
    static void AddCounts(MapRoofPreparationCounts target, MapRoofPreparationCounts source)
    {
        target.Polygons += source.Polygons; target.PolygonCorners += source.PolygonCorners;
        target.Coefficients += source.Coefficients; target.NormalizedCoefficientBytes += source.NormalizedCoefficientBytes;
        target.CoefficientPoolPages += source.CoefficientPoolPages; target.CoefficientPoolCapacity += source.CoefficientPoolCapacity;
        target.UniqueFootprintPolygons += source.UniqueFootprintPolygons;
        target.SideTableRows += source.SideTableRows; target.SideTableBytes += source.SideTableBytes;
        target.GeometryArenaPages += source.GeometryArenaPages;
        target.GeometryArenaCapacityBytes += source.GeometryArenaCapacityBytes;
    }
}
