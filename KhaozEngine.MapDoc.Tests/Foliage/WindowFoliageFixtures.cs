using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static class WindowFoliageFixtures
{
    internal static NativeSceneFixture Scene(FoliageFixture fixture) => new(fixture.Document, fixture.Assets);

    internal static MapSceneScope Scope(MapFoliageLayer layer, MapFoliageChunkKey key)
    {
        var r = MapFoliageDistribution.Rectangle(layer, key);
        return Scope(r.MinX, r.MinZ, r.MaxX, r.MaxZ);
    }

    internal static MapSceneScope Scope(float minX, float minZ, float maxX, float maxZ) => new(
        new(new WorldFrame(0, 0), new(minX, minZ), new(maxX, maxZ), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits()),
        new(minX, -500, minZ, maxX, 500, maxZ), MapSceneChannels.All);

    internal static FoliageFixture Rounded()
    {
        var f = FoliageComparisonFixtures.Flat();
        var layer = FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), 0.1f, -1, 1, 65, 3,
            Enumerable.Repeat((byte)255, 65 * 3).ToArray(), []);
        return FoliageComparisonFixtures.Set(f, layer);
    }

    internal static FoliageFixture ThreeLattices(float edge)
    {
        var f = FoliageFixtures.EdgeChunkSpace(edge);
        var extra = new MapSurfaceSet();
        foreach (var (id, unit, height) in new[] { ("medium", new MapRational(2, 1), 2200), ("fine", new MapRational(1, 2), 2300) })
        {
            WaterFixtures.Surface(extra, id, MapSurfaceRole.SupportFloor,
                new(unit, new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0), -4, -4, 4, 4, (_, _) => height);
        }
        foreach (var p in extra.Patches)
        {
            for (int i = 0; i < p.Value.Cells.Length; i++) p.Value.Cells[i] = p.Value.Cells[i] with { Underlay = 10 };
            f.Document.Surfaces.Patches.Add(p.Key, p.Value);
        }
        f.Document.Surfaces.Refs.AddRange(extra.Refs);
        f.Document.NativeResourceTransforms = new[]
        {
            new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, "fine", new(0, 0, 0, 0.371, 1.2), new string('a', 64)),
        };
        FoliageFixtures.Rehash(f.Document);
        return f;
    }
}
