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
    internal readonly Dictionary<MapPatchKey, MapSlotCellMask> Patches = new();
    internal readonly Dictionary<(string Footprint, bool Upper), MapCellDemand[]> Bounds = new();
    internal int Cells { get; private set; }
    internal long Faces { get; private set; }
    internal long Associations { get; private set; }

    internal MapMembershipDemandPlan(MapScopedSurfaces view, MapTopologyRecord[] records)
    {
        var slots = new Dictionary<MapPatchKey, HashSet<int>>();
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
                        Add(new(patch.Key, (z + patch.CellMinZ) * 64 + x + patch.CellMinX));
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
            var mask = MapSlotCellMask.Of(entry.Value);
            Patches.Add(entry.Key, mask); Cells = checked(Cells + mask.Count);
            var surface = MapMembershipPreparation.Surface(view, entry.Key.SurfaceId);
            view.TryAcquiredPatch(entry.Key, out var patch, out _);
            foreach (int slot in entry.Value)
            {
                long physical = MapSurfaceCompiler.CountFaces(surface, patch!, slot);
                int x = slot % 64 - patch!.CellMinX, z = slot / 64 - patch.CellMinZ;
                Faces = checked(Faces + physical * (surface.IndoorSpan is null ? 1 : 3) +
                    (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]) ? 4 : 0));
            }
        }

        void Chain(MapRecordRef reference)
        {
            var chain = MapSpaceGeometry.Record<MapBoundaryChain>(view, reference);
            foreach (var cell in bounds.SourceCells(chain)) Add(cell);
            // The chain resolver also borrows its declared source patch when endpoints yield no cells.
            if (chain.SourcePatch is { } key && !slots.ContainsKey(key)) slots.Add(key, new());
        }
        void Add(MapCellDemand cell)
        {
            if (!slots.TryGetValue(cell.Patch, out var cells)) slots.Add(cell.Patch, cells = new());
            cells.Add(cell.SlotCell);
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
                foreach (int slot in footprint.SlotCells)
                {
                    var polygon = MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, slot);
                    var extent = MapEffectiveGeometry.Bounds(polygon);
                    var range = MapEffectiveGeometry.CellRange(view, id!, new(extent.MinX, extent.MinZ), new(extent.MaxX, extent.MaxZ));
                    // Closed incident candidates are retained until the point kernel applies half-open ownership.
                    for (long z = checked(range.MinZ - 1); z <= range.MaxZExclusive; z++)
                        for (long x = checked(range.MinX - 1); x <= range.MaxXExclusive; x++)
                        {
                            var key = MapPatchKey.ForCell(id!, x, z);
                            if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null) continue;
                            int cell = MapLowerCellClassifier.SlotCell(x, z);
                            int lx = cell % 64 - patch.CellMinX, lz = cell / 64 - patch.CellMinZ;
                            if (lx < 0 || lz < 0 || lx >= patch.Width || lz >= patch.Depth) continue;
                            if (!Touches(polygon, MapEffectiveGeometry.CellPolygon(view, key, cell))) continue;
                            var demand = new MapCellDemand(key, cell);
                            selected.Add(demand); Add(demand);
                        }
                }
            Bounds.Add((footprint.Id, upper), selected.OrderBy(d => d.Patch).ThenBy(d => d.SlotCell).ToArray());
            Associations = checked(Associations + selected.Count);
        }
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
