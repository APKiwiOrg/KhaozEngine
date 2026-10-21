using System;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static class FoliageComparisonFixtures
{
    internal static FoliageFixture Flat(int seed = 0, float spacing = 0.5f, float origin = 0, bool varied = false)
    {
        FoliageFixture f = FoliageFixtures.Create();
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, "floor", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            -8, -8, 140, 8, (_, _) => 100);
        foreach (MapSurfacePatch p in set.Patches.Values)
            for (int i = 0; i < p.Cells.Length; i++) p.Cells[i] = p.Cells[i] with { Underlay = 10 };
        f.Document.Surfaces.Refs.Clear();
        f.Document.Surfaces.Refs.AddRange(set.Refs.Select(surface => surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, set.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                .Select(p => new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
        }));
        f.Document.Surfaces.Patches.Clear();
        foreach (var p in set.Patches) f.Document.Surfaces.Patches.Add(p.Key, p.Value);
        MapFoliageLayer layer = FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), origin, -2, 2, 66, 3,
            Enumerable.Range(0, 66 * 3).Select(i => varied && i % 66 == 32 ? (byte)254 : (byte)255).ToArray(), []);
        return Set(f, Change(layer, l => { l["seed"] = seed; l["spacing"] = spacing; }));
    }

    internal static FoliageFixture TwoSamplesPerChunk()
    {
        FoliageFixture f = Flat(spacing: 4);
        // Candidate jitter is less than half a spacing. A narrow Z band keeps one lattice row, with exactly two
        // accepted columns in each 8 m world chunk. The sparse raster still owns 32 sample columns per chunk.
        MapFoliageLayer layer = FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), 2, -2, 0.25f, 65, 17,
            Enumerable.Repeat((byte)255, 65 * 17).ToArray(), []);
        return Set(f, Change(layer, l => { l["spacing"] = 4; l["seed"] = 1; }));
    }

    internal static FoliageFixture Set(FoliageFixture f, MapFoliageLayer layer)
    {
        f.Document.Foliage = new(1, [layer]);
        return f;
    }

    internal static MapFoliageLayer Change(MapFoliageLayer layer, Action<JsonObject> change) => FoliageStorageFixtures.Change(layer, change);

    internal static MapFoliageLayer Layer(FoliageFixture f) => f.Document.Foliage!.Layers[0];

    internal static FoliageFixture Copy(FoliageFixture f, Action<JsonObject> change) =>
        Set(FoliageFixtures.Reload(f, tiled: false), Change(Layer(f), change));

    // Task 4's growth variants, including distant wide proxies and independent painted support floors.
    internal static FoliageFixture Grown(int chunks, int placements, int paintTargets, int wide)
    {
        FoliageFixture f = FoliageFixtures.Create();
        MapFoliageLayer layer = Change(FoliageFixtures.BasicLayer([]), l =>
        {
            l["width"] = 64 * 32; l["depth"] = 32;
            l["clearanceRules"] = new JsonArray(Rule("solid-tree", "tree", 1), Rule("solid-wide", "reed-a", 200));
            l["chunks"] = new JsonArray(Enumerable.Range(0, chunks).Select(x => (JsonNode)new JsonObject
            {
                ["x"] = x,
                ["z"] = 0,
                ["density"] = Convert.ToBase64String(Enumerable.Repeat((byte)(x + 1), 1024).ToArray()),
            }).ToArray());
        });
        Set(f, layer);
        for (int i = 0; i < placements + wide; i++)
        {
            bool big = i >= placements;
            int n = big ? i - placements : i;
            f.Document.Placements.Add(new MapPlacement
            {
                Id = (big ? "wide-" : "tree-") + n,
                AssetId = big ? "reed-a" : "tree",
                Kind = "scenery",
                NumericId = MapNumericIds.Allocate(f.Document),
                X = big ? 20000 + 512 * n : n < 2 ? 4 + 6 * n : 3000 + 8 * n,
                Z = big ? 20000 : n < 2 ? 2 : 3000,
                Y = !big && n < 2 ? null : 1,
                SupportBinding = !big && n < 2 ? FoliageFixtures.Surface("floor") : null,
            });
        }
        var added = new MapSurfaceSet();
        for (int k = 0; k < paintTargets; k++)
        {
            long x = 4096 + 64L * k;
            WaterFixtures.Surface(added, "far-" + k, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, x, 4096, x + 2, 4098, (_, _) => 0);
            WaterFixtures.Surface(added, "paint-" + k, MapSurfaceRole.PaintOverride, MapLatticeFrame.ImportedMetreCentimetre, x, 4096, x + 2, 4098, (_, _) => 0);
            added.Refs[^1] = added.Refs[^1] with { PaintTargetSurfaceId = "far-" + k };
        }
        foreach (var p in added.Patches) f.Document.Surfaces.Patches.Add(p.Key, p.Value);
        foreach (MapSurfaceRef surface in added.Refs)
            f.Document.Surfaces.Refs.Add(surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, added.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            });
        return f;
    }

    static JsonObject Rule(string id, string asset, int extent) => new()
    {
        ["id"] = id,
        ["assetId"] = asset,
        ["kind"] = "Solid",
        ["footprint"] = new JsonObject { ["minX"] = -extent, ["minZ"] = -extent, ["maxX"] = extent, ["maxZ"] = extent },
    };
}
