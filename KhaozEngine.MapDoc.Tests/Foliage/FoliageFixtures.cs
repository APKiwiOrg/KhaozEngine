using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal sealed record FoliageFixture(MapDocument Document, MapAssetClosure Assets);

/// <summary>Public invented foliage fixtures. Linked into other test projects, so nothing here reads a committed file. The
/// native floor covers world X [-8, 72] and Z [-8, 8] at height 1 with underlay 10, on the imported metre and centimetre
/// lattice, in a resolver-2 document.</summary>
internal static partial class FoliageFixtures
{
    internal const string LayerId = "cover-a";
    internal const string FloorId = "floor";

    internal static MapResolveOptions Options() => new("foliage-fixture", 1, "foliage-fixture/1", 2);

    /// <summary>"basic" holds the basic layer alone. "decorated" adds support floor "upper", a clearance rule, an exception
    /// mask, an owner support and a compatibility row for "upper" sharing ordinal 0.</summary>
    internal static FoliageFixture Create(string variant = "basic")
    {
        MapFoliageLayer layer = variant switch
        {
            "basic" => BasicLayer(),
            "decorated" => BasicLayer(decorated: true),
            _ => throw new ArgumentException("Unknown foliage variant " + variant, nameof(variant)),
        };
        var (roots, source) = AssetInputs();
        MapDocument document = FloorDocument(variant == "decorated");
        document.NativeAssets = roots.ToList();
        document.Foliage = new MapFoliageSet(1, [layer]);
        return new(document, MapAssetClosure.Load(roots, source));
    }

    /// <summary>Saves and reloads either public storage form, including every native density byte and setting.</summary>
    internal static FoliageFixture Reload(FoliageFixture fixture, bool tiled)
    {
        if (!tiled) return fixture with { Document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)) };
        string root = Path.Combine(Path.GetTempPath(), "foliage-reload-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, root);
            return fixture with { Document = MapDocumentFile.LoadTiled(root) };
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Layer cover-a: origin (-3, -5), cell size 2, 35 by 3 samples with row bytes z * 35 + x in two chunks, seed 17,
    /// spacing 0.5, scale 0.8 to 1.4, root offset -0.01 and ordered models reed-a (3) and reed-b (1). Materials 10 and 11
    /// are ordinary and allowed, 12 is water.</summary>
    internal static MapFoliageLayer BasicLayer(IReadOnlyList<MapFoliageChunk>? chunks = null, bool decorated = false)
    {
        MapFoliageCompatibilityOrdinal[] ordinals = decorated ? [new(FloorId, 0), new("upper", 0)] : [new(FloorId, 0)];
        MapFoliageClearanceRule[] rules = decorated
            ? [new("door-rule", "tree", "door", MapFoliageClearanceKind.Door, new(-0.5f, -0.25f, 0.5f, 0.25f))]
            : [];
        var house = new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "house-1");
        MapFoliageMask[] masks = decorated
            ? [new("roof-mask", house, Surface(FloorId), MapFoliageClearanceKind.RoofAbove, new(-2, -1.5f, 2, 1.5f))]
            : [];
        MapFoliageOwnerSupport[] owners = decorated
            ? [new(house, new MapSupportBinding(MapSupportBindingKind.Space, null, "hall", 1.25f, 0.5f, 0.75f))]
            : [];
        return new MapFoliageLayer(1, LayerId, Surface(FloorId), -3, -5, 2, 35, 3, MapRowDirection.PositiveZ, 17, 0.5f, 0.8f, 1.4f,
            -0.01f, [new("reed-a", 3), new("reed-b", 1)], [10, 11],
            [new(10, "moss-material", MapFoliageMaterialKind.Ordinary), new(11, "soil-material", MapFoliageMaterialKind.Ordinary),
             new(12, "pond-material", MapFoliageMaterialKind.Water)],
            new("legacy-tile", 1, 0), true, true, 1.5f, 0.5f, ordinals, rules, masks, owners, chunks ?? BasicChunks());
    }

    /// <summary>Chunks (0, 0) and (1, 0) of the basic raster. Every in-domain byte is z * 35 + x and padding is zero.</summary>
    internal static MapFoliageChunk[] BasicChunks() => [BasicChunk(0), BasicChunk(1)];

    internal static MapFoliageChunk BasicChunk(int chunkX)
    {
        var bytes = new byte[MapFoliageChunk.ByteLength];
        for (int z = 0; z < 3; z++)
            for (int local = 0; local < MapFoliageChunk.Size; local++)
            {
                int x = chunkX * MapFoliageChunk.Size + local;
                if (x < 35) bytes[z * MapFoliageChunk.Size + local] = (byte)(z * 35 + x);
            }
        return new MapFoliageChunk(new(chunkX, 0), bytes);
    }

    internal static MapSupportBinding Surface(string id) => new(MapSupportBindingKind.Surface, id, null, null, null, null);

    // The decorated variant adds support floor "upper" at height 4 over world X [0, 8] and Z [-4, 4].
    static MapDocument FloorDocument(bool upper)
    {
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, FloorId, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, -8, -8, 72, 8, (_, _) => 100);
        if (upper)
            WaterFixtures.Surface(set, "upper", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, 0, -4, 8, 4, (_, _) => 400);
        foreach (MapSurfacePatch patch in set.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.Id = "foliage-fixture";
        return document;
    }

    // The shared native fixture closure, with reed-a and reed-b as model assets, plus the three invented materials.
    static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) AssetInputs()
    {
        var (roots, source) = NativeAssetFixtures.Change(root =>
        {
            JsonArray assets = root["assets"]!.AsArray();
            foreach (string id in new[] { "reed-a", "reed-b" })
            {
                JsonObject asset = NativeAssetFixtures.Asset();
                asset["id"] = id;
                assets.Add(asset);
            }
        });
        var materials = new JsonArray();
        foreach (string id in new[] { "moss-material", "soil-material", "pond-material" })
            materials.Add(NativeAssetFixtures.Resource(source.Add(id, Encoding.UTF8.GetBytes("bytes:" + id)), "Material"));
        MapAssetRef root = source.Add("root-foliage", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = materials,
        }.ToJsonString());
        return (roots.Append(root).ToArray(), source);
    }
}
