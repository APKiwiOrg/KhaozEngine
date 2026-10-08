using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

internal sealed record MapFootprintRetarget(MapPatchKey Anchor, MapSpaceFootprint Original,
    MapSpaceFootprint Remaining, MapSpaceFootprint? Added);

/// <summary>Plans bound changes on each footprint's own lattice without mutating any record.</summary>
internal static class MapConversionFootprints
{
    internal static IReadOnlyList<MapFootprintRetarget> Plan(MapSurfaceSet surfaces, MapSurfaceRef source,
        MapFinePatchRequest request)
    {
        MapExactRect[] regions = request.Regions.Select(r => MapSurfaceRanges.World(r, source.Frame)).ToArray();
        var plans = new List<MapFootprintRetarget>();
        var ids = surfaces.AllRecords().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var footprints = surfaces.Patches.Values.SelectMany(p => p.Records.OfType<MapSpaceFootprint>()
            .Select(f => (Anchor: p.Key, Footprint: f))).OrderBy(row => row.Footprint.Id, StringComparer.Ordinal);
        foreach (var row in footprints)
        {
            MapSpaceFootprint footprint = row.Footprint;
            if (footprint.Lower.SurfaceId != source.Id && footprint.Upper.SurfaceId != source.Id) continue;
            MapSurfaceRef lattice = surfaces.Refs.Single(s => s.Id == footprint.Lattice.SurfaceId);
            var inside = new List<int>();
            var outside = new List<int>();
            foreach (int cell in footprint.SlotCells.Order())
            {
                long x = checked(footprint.Lattice.SlotX * 64 + cell % 64);
                long z = checked(footprint.Lattice.SlotZ * 64 + cell / 64);
                MapExactRect rectangle = MapSurfaceRanges.World(new(x, z, checked(x + 1), checked(z + 1)), lattice.Frame);
                MapExactValue area = rectangle.MaxX.Subtract(rectangle.MinX)
                    .Multiply(rectangle.MaxZ.Subtract(rectangle.MinZ));
                MapExactValue covered = default;
                foreach (MapExactRect region in regions)
                {
                    MapExactValue minX = Max(rectangle.MinX, region.MinX), minZ = Max(rectangle.MinZ, region.MinZ);
                    MapExactValue maxX = Min(rectangle.MaxX, region.MaxX), maxZ = Min(rectangle.MaxZ, region.MaxZ);
                    if (minX.CompareTo(maxX) < 0 && minZ.CompareTo(maxZ) < 0)
                        covered = covered.Add(maxX.Subtract(minX).Multiply(maxZ.Subtract(minZ)));
                }
                if (covered == area) inside.Add(cell);
                else if (covered.Sign == 0) outside.Add(cell);
                else throw new MapDocumentException($"footprint straddle: footprint '{footprint.Id}' cell {cell} crosses the conversion region boundary. Align the region to whole footprint cells or split the footprint first.");
            }
            if (inside.Count == 0) continue;
            MapSpaceFootprint moved = footprint with
            {
                SlotCells = inside.ToArray(),
                Lower = Retarget(footprint.Lower, lower: true),
                Upper = Retarget(footprint.Upper, lower: false),
            };
            if (outside.Count == 0) plans.Add(new(row.Anchor, footprint, moved, null));
            else
            {
                string id = footprint.Id + "-" + request.FineSurfaceId;
                if (!ids.Add(id)) throw new MapDocumentException($"duplicate footprint id '{id}'");
                plans.Add(new(row.Anchor, footprint, footprint with { SlotCells = outside.ToArray() }, moved with { Id = id }));
            }
        }
        return plans.AsReadOnly();

        MapBoundRef Retarget(MapBoundRef bound, bool lower) => bound.SurfaceId != source.Id ? bound : bound with
        {
            SurfaceId = request.FineSurfaceId,
            Kind = lower && bound.Kind == MapBoundKind.LegacyExteriorV1 ? MapBoundKind.SupportFloor : bound.Kind,
        };
    }

    static MapExactValue Min(MapExactValue a, MapExactValue b) => a.CompareTo(b) <= 0 ? a : b;
    static MapExactValue Max(MapExactValue a, MapExactValue b) => a.CompareTo(b) >= 0 ? a : b;
}
