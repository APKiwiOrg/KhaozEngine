using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Frozen union of queryable cells and independent footprint bound associations.</summary>
internal sealed class MapMembershipDemandPlan
{
    sealed class Pending
    {
        readonly ulong[] _words = new ulong[256];
        internal bool Add(int slot, byte category)
        {
            int offset = category == 1 ? 64 : category == 2 ? 128 : 192;
            ulong bit = 1UL << (slot % 64);
            int word = slot / 64;
            bool fresh = (_words[offset + word] & bit) == 0;
            _words[offset + word] |= bit; _words[word] |= bit;
            return fresh;
        }
        internal IEnumerable<int> Cells()
        {
            for (int row = 0; row < 64; row++)
                for (int x = 0; x < 64; x++) if ((_words[row] & (1UL << x)) != 0) yield return row * 64 + x;
        }
    }
    internal readonly Dictionary<MapPatchKey, MapSlotCellMask> Patches = new();
    internal readonly Dictionary<(string Footprint, bool Upper), MapCellDemand[]> Bounds = new();
    internal int Cells { get; private set; }
    internal long Faces { get; private set; }
    internal long Associations { get; private set; }

    internal MapMembershipDemandPlan(MapScopedSurfaces view, MapTopologyRecord[] records, MapPreparationReservation? owner = null)
    {
        owner?.Reserve(checked(1024L + 128L * records.Length));
        var counts = MapMembershipBuild.Current?.Counts;
        long indexBytes = checked(256L * view.Witness.Present.Count);
        owner?.Reserve(indexBytes);
        var patchIndex = new MapMembershipPatchIndex(view);
        var slots = new Dictionary<MapPatchKey, Pending>();
        long builderBytes = 0;
        foreach (var footprint in records.OfType<MapSpaceFootprint>())
        {
            Select(footprint, footprint.Lower, false);
            Select(footprint, footprint.Upper, true);
        }
        foreach (var present in view.Witness.Present)
        {
            var surface = MapMembershipPreparation.Surface(view, present.Key.SurfaceId);
            if (surface.IndoorSpan is null || !view.TryAcquiredPatch(present.Key, out var patch, out _) || patch is null) continue;
            for (int z = 0; z < patch.Depth; z++)
                for (int x = 0; x < patch.Width; x++)
                    if ((patch.Cells[z * patch.Width + x].Flags & MapCellFlags.Indoor) != 0)
                        Add(new(patch.Key, (z + patch.CellMinZ) * 64 + x + patch.CellMinX), 2);
        }
        var bounds = new MapSpacePointBounds(view, new(MaxContextPatches: int.MaxValue), null);
        foreach (var portal in records.OfType<MapCavePortal>())
        {
            if (records.OfType<MapPrismDomain>().Any(p => p.Space.Id == portal.FromSpace.Id || p.Space.Id == portal.ToSpace.Id)) continue;
            Chain(portal.BandBottom);
            if (portal.BandTop is { } top) Chain(top);
        }
        foreach (var entry in slots)
        {
            owner?.Reserve(640);
            var mask = MapSlotCellMask.Of(entry.Value.Cells());
            Patches.Add(entry.Key, mask); Cells = checked(Cells + mask.Count);
            var surface = MapMembershipPreparation.Surface(view, entry.Key.SurfaceId);
            view.TryAcquiredPatch(entry.Key, out var patch, out _);
            foreach (int slot in entry.Value.Cells())
            {
                long physical = MapSurfaceCompiler.CountFaces(surface, patch!, slot);
                int x = slot % 64 - patch!.CellMinX, z = slot / 64 - patch.CellMinZ;
                if (counts is not null)
                {
                    counts.PhysicalFaces += physical;
                    counts.SpanFaces += surface.IndoorSpan is null ? 0 : 2 * physical;
                    if (physical == 0) counts.FaceFreeTargetSlots++;
                    if (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]) &&
                        view.EffectiveContext?.Transform(Prefabs.MapPrefabChildKind.Surface, surface.Id) is not null) counts.ChartFaces += 4;
                }
                Faces = checked(Faces + physical * (surface.IndoorSpan is null ? 1 : 3) +
                    (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]) ? 4 : 0));
            }
        }

        owner?.Release(checked(builderBytes + indexBytes));
        if (counts is not null)
        { counts.UniqueTargetSlots = Cells; counts.BoundAssociations = Associations; counts.PlannerBytes = owner?.Bytes ?? 0; }

        void Chain(MapRecordRef reference)
        {
            var chain = MapSpaceGeometry.Record<MapBoundaryChain>(view, reference);
            foreach (var cell in bounds.SourceCells(chain)) Add(cell, 4);
            // The chain resolver also borrows its declared source patch when endpoints yield no cells.
            if (chain.SourcePatch is { } key && !slots.ContainsKey(key)) PendingPatch(key);
        }
        Pending PendingPatch(MapPatchKey key)
        {
            if (slots.TryGetValue(key, out var pending)) return pending;
            // Four bounded bit planes plus dictionary growth and old/new backing overlap.
            owner?.Reserve(2304); builderBytes += 2304;
            slots.Add(key, pending = new());
            return pending;
        }
        void Add(MapCellDemand cell, byte category)
        {
            if (!PendingPatch(cell.Patch).Add(cell.SlotCell, category) || counts is null) return;
            if (category == 1) counts.FootprintTargetSlots++;
            if (category == 2) counts.IndoorTargetSlots++;
            if (category == 4) counts.PortalTargetSlots++;
        }
        void Select(MapSpaceFootprint footprint, MapBoundRef bound, bool upper)
        {
            if (bound.Kind == MapBoundKind.OpenTop) return;
            string? id = bound.SurfaceId;
            if (bound.Opening is { } reference && view.TryRecord(reference, out var record, out _, borrow: true) && record is MapHorizontalOpening opening)
                id = opening.Patch.SurfaceId;
            var selected = new HashSet<MapCellDemand>();
            var surface = id is null ? null : MapMembershipPreflight.FindSurface(view, id);
            if (surface?.Role is MapSurfaceRole.SupportFloor or MapSurfaceRole.Ceiling)
            {
                // The released inverse uses binary64 discovery and padding. Below the frozen world quantum,
                // rounding can move a boundary by arbitrarily many target cells. Refuse that uncertified range.
                CertifyRange(surface.Frame, view.EffectiveContext?.Transform(Prefabs.MapPrefabChildKind.Surface, id!));
                foreach (int slot in footprint.SlotCells)
                {
                    using var discovery = MapMembershipBuild.Kernel();
                    var polygon = MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, slot);
                    var extent = MapEffectiveGeometry.Bounds(polygon);
                    var range = MapEffectiveGeometry.CellRange(view, id!, new(extent.MinX, extent.MinZ), new(extent.MaxX, extent.MaxZ));
                    // Closed incident candidates are retained until the point kernel applies half-open ownership.
                    var closed = new MapCellRect(checked(range.MinX - 1), checked(range.MinZ - 1),
                        checked(range.MaxXExclusive + 1), checked(range.MaxZExclusive + 1));
                    foreach (var patch in patchIndex.Overlapping(id!, closed))
                    {
                        var key = patch.Key;
                        long minX = checked(key.SlotX * 64 + patch.CellMinX), minZ = checked(key.SlotZ * 64 + patch.CellMinZ);
                        for (long z = Math.Max(closed.MinZ, minZ); z < Math.Min(closed.MaxZExclusive, checked(minZ + patch.Depth)); z++)
                            for (long x = Math.Max(closed.MinX, minX); x < Math.Min(closed.MaxXExclusive, checked(minX + patch.Width)); x++)
                            {
                            int cell = MapLowerCellClassifier.SlotCell(x, z);
                            if (!Touches(polygon, MapEffectiveGeometry.CellPolygon(view, key, cell))) continue;
                            var demand = new MapCellDemand(key, cell);
                            if (!selected.Contains(demand)) { owner?.Reserve(128); selected.Add(demand); }
                            Add(demand, 1);
                            }
                    }
                }
            }
            owner?.Reserve(checked(128L + 32L * selected.Count));
            Bounds.Add((footprint.Id, upper), selected.OrderBy(d => d.Patch).ThenBy(d => d.SlotCell).ToArray());
            owner?.Release(checked(128L * selected.Count));
            Associations = checked(Associations + selected.Count);
        }
    }
    internal static void CertifyRange(MapLatticeFrame frame, Prefabs.MapResourceTransform? transform)
    {
        if (transform is null) return;
        var pose = transform.Transform;
        if (frame.CellUnitMetres.Exact().ToDouble() * pose.Scale < 4.0 / Prefabs.MapResourceTransform.Quantum ||
            Math.Abs(pose.X) > Prefabs.MapResourceTransform.MaxCoordinateMetres || Math.Abs(pose.Z) > Prefabs.MapResourceTransform.MaxCoordinateMetres)
            throw new MapExactOverflowException();
    }
    static bool Touches(IReadOnlyList<MapExactXz> a, IReadOnlyList<MapExactXz> b) => !Separated(a, b) && !Separated(b, a);
    static bool Separated(IReadOnlyList<MapExactXz> a, IReadOnlyList<MapExactXz> b)
    {
        for (int edge = 0; edge < a.Count; edge++)
        {
            int inside = MapMembershipExact.Orientation(a[edge], a[(edge + 1) % a.Count], a[(edge + 2) % a.Count]);
            bool outside = true;
            for (int i = 0; i < b.Count; i++)
                outside &= MapMembershipExact.Orientation(a[edge], a[(edge + 1) % a.Count], b[i]) * inside < 0;
            if (outside) return true;
        }
        return false;
    }
}
