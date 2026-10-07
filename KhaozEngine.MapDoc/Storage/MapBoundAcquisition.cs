using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Preflights every fresh bound key before any bound payload is read.</summary>
internal static class MapBoundAcquisition
{
    internal static IReadOnlyList<MapPatchKey> Collect(MapSurfaceScope scope, IEnumerable<MapSpaceFootprint> footprints,
        IReadOnlyCollection<MapPatchKey> reserved, Func<string, MapSurfaceRef> surface, MapAcquisitionWork? work)
    {
        var known = new HashSet<MapPatchKey>(reserved);
        var pending = new List<MapPatchKey>();
        MapExactRect window = scope.WorldRectangle();
        foreach (MapSpaceFootprint footprint in footprints.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            MapSurfaceRef lattice = surface(footprint.Lattice.SurfaceId);
            foreach (int cell in footprint.SlotCells.Distinct().OrderBy(c => c))
            {
                MapExactXz[] rect = MapLatticeRanges.CellRect(lattice.Frame, footprint.Lattice, cell);
                if (window.MinX.CompareTo(rect[1].X) >= 0 || window.MaxX.CompareTo(rect[0].X) <= 0 ||
                    window.MinZ.CompareTo(rect[1].Z) >= 0 || window.MaxZ.CompareTo(rect[0].Z) <= 0) continue;
                foreach (MapBoundRef bound in new[] { footprint.Lower, footprint.Upper })
                {
                    if (bound.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.Ceiling or MapBoundKind.LegacyExteriorV1)) continue;
                    if (string.IsNullOrWhiteSpace(bound.SurfaceId)) throw new MapDocumentException("bound surface is undeclared");
                    MapSurfaceRef target = surface(bound.SurfaceId);
                    MapCellRect cells = MapLatticeRanges.CellRange(target.Frame, rect[0], rect[1]);
                    MapSlotRect slots = MapSurfaceRanges.Slots(cells);
                    long gross;
                    try { gross = checked(checked(slots.MaxXExclusive - slots.MinX) * checked(slots.MaxZExclusive - slots.MinZ)); }
                    catch (OverflowException) { throw new MapSurfaceCapacityException(); }
                    if (work is not null) work.LargestBoundSlotRange = Math.Max(work.LargestBoundSlotRange, gross);
                    long overlap = known.Count(k => k.SurfaceId == bound.SurfaceId && slots.Contains(k.SlotX, k.SlotZ));
                    long fresh = gross - overlap;
                    if (fresh > scope.Limits.MaxCandidatePatches - known.Count) throw new MapSurfaceCapacityException();
                    if (fresh == 0) continue;
                    for (long z = slots.MinZ; z < slots.MaxZExclusive; z++)
                        for (long x = slots.MinX; x < slots.MaxXExclusive; x++)
                        {
                            if (work is not null) work.BoundSlotVisits++;
                            var key = new MapPatchKey(bound.SurfaceId, x, z);
                            if (known.Add(key)) pending.Add(key);
                        }
                }
            }
        }
        return pending.AsReadOnly();
    }
}
