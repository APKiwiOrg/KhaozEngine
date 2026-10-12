using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    static bool TryPaintVariant(string variant, out PrefabFixture? fixture)
    {
        fixture = null;
        if (variant is not ("uneven-unpainted" or "uneven-painted" or "paint-notch" or "paint-hole" or
            "prioritized-paint" or "equal-priority-paint" or "identical-paint")) return false;
        var f = Create("roomless");
        var ground = Patch("ground", 8, 8, 0);
        for (int z = 0; z <= ground.Depth; z++)
            for (int x = 0; x <= ground.Width; x++) ground.Heights[z * (ground.Width + 1) + x] = x * 25;
        Array.Fill(ground.Cells, new(3, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto));
        f.Document.Surfaces.Refs.Add(Surface("ground", MapSurfaceRole.SupportFloor));
        f.Document.Surfaces.Patches.Add(ground.Key, ground);
        if (variant is "prioritized-paint" or "equal-priority-paint" or "identical-paint")
        {
            AddWorldPaint("priority-low", 0, 1, 7);
            AddWorldPaint("priority-high", 1, variant == "prioritized-paint" ? 2 : 1,
                variant == "identical-paint" ? (ushort)7 : (ushort)8);
        }
        else
        {
            var asset = f.Assets.GetPrefab("hut");
            int size = variant == "paint-hole" ? 3 : 2;
            var patch = Patch("paint", size, size, 9000);
            Array.Fill(patch.Cells, new(7, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto));
            if (variant is "paint-notch" or "paint-hole") patch.SetPresent(1, 1, false);
            var child = SurfaceChild("paint", MapSurfaceRole.PaintOverride, patch) with
            {
                Surface = Surface("paint", MapSurfaceRole.PaintOverride) with { PaintTargetSurfaceId = "ground" },
                References = Slots(Surf("/patches/0/key/surfaceId", "paint"),
                    Surf("/surface/paintTargetSurfaceId", "ground", MapPrefabReferenceScope.World)),
            };
            if (variant != "uneven-unpainted")
                asset = asset with { Children = asset.Children.Append(child).ToArray() };
            f = Reasset(f, asset, new Dictionary<string, string> { ["paint"] = "a/paint" });
            var instance = f.Document.Prefabs!.Instances.Single();
            f.Document.Prefabs = new(1, 1, new[] { instance with { Transform = new(new(2.25f, 9, -2.125f), 0.371f, 1) } }, []);
        }
        fixture = f;
        return true;

        void AddWorldPaint(string id, int x, int priority, ushort material)
        {
            var patch = Patch(id, 3, 2, 9000);
            patch.CellMinX = x;
            Array.Fill(patch.Cells, new(material, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto));
            f.Document.Surfaces.Refs.Add(Surface(id, MapSurfaceRole.PaintOverride) with
            { PaintTargetSurfaceId = "ground", PaintPriority = priority });
            f.Document.Surfaces.Patches.Add(patch.Key, patch);
        }
    }

    internal static MapCompiledPatch CanonicalPatch(MapEffectiveResources resources, string surfaceId) =>
        MapEffectiveGeometry.Patch(resources.CompleteView(), new(surfaceId, 0, 0));

    internal static MapExactValue PaintArea(MapPaintRegionSet regions) => NarrowPaintArea(regions.Regions.Aggregate(
        default(MapWideRational), (sum, r) => sum.Add(PaintPolygonArea(r.Polygon))));

    // Independent shoelace sum over the four frozen footprint corners, never compiler output.
    internal static MapExactValue PaintDomainArea(MapEffectiveResources resources)
    {
        var transform = new MapResourceTransform(resources.Transforms["Surface:a/paint"]);
        var corners = new[] { (0, 0), (2, 0), (2, -2), (0, -2) }
            .Select(p => transform.Point(new(new(p.Item1, 1), default, new(p.Item2, 1)))).ToArray();
        return NarrowPaintArea(PaintPolygonArea(corners));
    }

    internal static MapWideRational PaintPolygonArea(IReadOnlyList<MapExactPoint> polygon)
    {
        MapWideRational sum = default;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = MapWidePoint.From(polygon[i]); var b = MapWidePoint.From(polygon[(i + 1) % polygon.Count]);
            sum = sum.Add(a.X.Multiply(b.Z).Subtract(b.X.Multiply(a.Z)));
        }
        return sum.Abs().Divide(MapWideRational.From(2));
    }

    static MapExactValue NarrowPaintArea(MapWideRational area) =>
        area.TryNarrow(out var exact) ? exact : throw new InvalidOperationException("Fixture area is not representable.");

    internal static bool HighPriorityOwnsOverlap(MapEffectiveResources resources)
    {
        var regions = resources.PaintRegions["ground"].Regions;
        // Hand-authored rectangles overlap on (1, 3) x (-2, 0). Probe strict interiors of each cell.
        for (int x = 1; x < 3; x++)
            for (int z = 0; z < 2; z++)
            {
                var owners = regions.Where(r => PaintContains(r.Polygon, new(new(4 * x + 1, 4), default, new(-4 * z - 2, 4)))).ToArray();
                if (owners.Length != 1 || owners[0].PaintId != "priority-high" || owners[0].Material.Underlay != 8) return false;
            }
        return PaintArea(new(regions.Where(r => r.PaintId == "priority-high").ToArray(), "")).Equals(new MapExactValue(6, 1)) &&
            PaintArea(new(regions.Where(r => r.PaintId == "priority-low").ToArray(), "")).Equals(new MapExactValue(2, 1));
    }

    internal static bool PaintContains(IReadOnlyList<MapExactPoint> polygon, MapExactPoint point)
    {
        int winding = 0;
        var p = MapWidePoint.From(point);
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = MapWidePoint.From(polygon[i]); var b = MapWidePoint.From(polygon[(i + 1) % polygon.Count]);
            int side = b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X))).Sign;
            if (side == 0) continue;
            if (winding != 0 && winding != side) return false;
            winding = side;
        }
        return winding != 0;
    }
}
