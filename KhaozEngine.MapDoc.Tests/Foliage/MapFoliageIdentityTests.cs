using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageIdentityTests
{
    [Theory]
    [InlineData("id")]
    [InlineData("support")]
    [InlineData("support-kind")]
    [InlineData("space-spaceId")]
    [InlineData("space-referenceY")]
    [InlineData("space-searchBelow")]
    [InlineData("space-searchAbove")]
    [InlineData("originX")]
    [InlineData("originZ")]
    [InlineData("cellSize")]
    [InlineData("width")]
    [InlineData("depth")]
    [InlineData("seed")]
    [InlineData("spacing")]
    [InlineData("scaleMin")]
    [InlineData("scaleMax")]
    [InlineData("rootOffset")]
    [InlineData("model-id")]
    [InlineData("model-weight")]
    [InlineData("model-order")]
    [InlineData("allowed-material")]
    [InlineData("material-id")]
    [InlineData("material-resource")]
    [InlineData("material-kind")]
    [InlineData("normal-boundary")]
    [InlineData("excludeIndoors")]
    [InlineData("excludeSolidObjects")]
    [InlineData("doorClearance")]
    [InlineData("edgeFade")]
    [InlineData("ordinal-surface")]
    [InlineData("ordinal-value")]
    [InlineData("rule-id")]
    [InlineData("rule-asset")]
    [InlineData("rule-tag")]
    [InlineData("rule-kind")]
    [InlineData("rule-minX")]
    [InlineData("rule-minZ")]
    [InlineData("rule-maxX")]
    [InlineData("rule-maxZ")]
    [InlineData("mask-id")]
    [InlineData("mask-owner")]
    [InlineData("mask-owner-kind")]
    [InlineData("mask-support-kind")]
    [InlineData("mask-support")]
    [InlineData("mask-kind")]
    [InlineData("mask-footprint")]
    [InlineData("mask-minX")]
    [InlineData("mask-minZ")]
    [InlineData("mask-maxZ")]
    [InlineData("owner-reference")]
    [InlineData("owner-support")]
    [InlineData("owner-kind")]
    [InlineData("owner-support-kind")]
    [InlineData("owner-spaceId")]
    [InlineData("owner-searchBelow")]
    [InlineData("owner-searchAbove")]
    [InlineData("byte")]
    [InlineData("explicit-zero")]
    public void FoliageChangesBothIdentityPathsButNotAnnotationDigest(string field)
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create("decorated");
        MapDocument doc = fixture.Document;
        if (field.StartsWith("space-", StringComparison.Ordinal))
            doc.Foliage = new(1, [FoliageStorageFixtures.Change(doc.Foliage!.Layers[0], l => l["support"] = Space())]);
        string beforeRoot = MapSurfaceSemantics.RootDigest(doc);
        string beforeWorld = MapDocumentHash.OfWorld(doc);
        string beforeManifest = MapDocumentHash.OfManifest(doc);
        string beforeAuthored = MapAuthoredIdentityV2.Compute(doc, fixture.Assets, FoliageFixtures.Options());
        var beforeBounds = doc.Bounds;
        string[] beforeAnnotations = Annotations(doc, fixture.Assets);
        doc.Foliage = new(1, [FoliageStorageFixtures.Change(doc.Foliage!.Layers[0], l => Change(l, field))]);
        Assert.NotEqual(beforeRoot, MapSurfaceSemantics.RootDigest(doc));
        Assert.NotEqual(beforeWorld, MapDocumentHash.OfWorld(doc));
        Assert.NotEqual(beforeManifest, MapDocumentHash.OfManifest(doc));
        string monolithicAuthored = MapAuthoredIdentityV2.Compute(doc, fixture.Assets, FoliageFixtures.Options());
        Assert.NotEqual(beforeAuthored, monolithicAuthored);
        MapDocumentFile.SaveTiled(doc, dir.Root);
        string storedAuthored = MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(dir.Root), fixture.Assets, FoliageFixtures.Options());
        Assert.Equal(monolithicAuthored, storedAuthored);
        Assert.Equal(monolithicAuthored, MapAuthoredIdentityV2.Compute(MapDocumentFile.LoadTiled(dir.Root), fixture.Assets, FoliageFixtures.Options()));
        Assert.Equal(beforeAnnotations, Annotations(doc, fixture.Assets));
        Assert.Equal(beforeBounds, doc.Bounds);
    }

    [Fact]
    public void Projection_HashesIndependentCanonicalBytesAndIgnoresPacking()
    {
        var fixture = FoliageFixtures.Create("decorated");
        JsonObject expected = JsonNode.Parse(FoliageStorageFixtures.Json(fixture.Document.Foliage!))!.AsObject();
        foreach (JsonNode? layer in expected["layers"]!.AsArray())
            foreach (JsonNode? chunk in layer!["chunks"]!.AsArray())
            {
                byte[] bytes = Convert.FromBase64String(chunk!["density"]!.GetValue<string>());
                chunk.AsObject().Remove("density"); chunk["sha256"] = FoliageStorageFixtures.Hash(bytes);
            }
        string independent = FoliageStorageFixtures.Hash(Encoding.UTF8.GetBytes("kemap/foliage-authored/1\0" + expected.ToJsonString()));
        Assert.Equal(independent, MapFoliageProjection.Digest(fixture.Document.Foliage));
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var partial = MapDocumentFile.LoadTiled(dir.Root, new MapTileRect(new(100, 100), new(100, 100)));
        Assert.Equal(independent, MapFoliageProjection.Digest(partial.Foliage));
        Assert.Equal(MapSurfaceSemantics.RootDigest(fixture.Document), MapSurfaceSemantics.RootDigest(partial));
        var layerBefore = fixture.Document.Foliage!.Layers[0].ChunkDigests.ToArray();
        string digest = MapFoliageProjection.Digest(fixture.Document.Foliage);
        fixture.Document.TileSize = 64;
        MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        Assert.Equal(layerBefore, fixture.Document.Foliage.Layers[0].ChunkDigests);
        Assert.Equal(digest, MapFoliageProjection.Digest(fixture.Document.Foliage));
        Assert.Empty(MapDocumentFile.VerifyTiled(dir.Root));
    }

    [Fact]
    public void FormatNine_MovesNativeIdentitiesExactlyOnce()
    {
        var fixture = FoliageFixtures.Create(); fixture.Document.Foliage = null;
        JsonObject atEight = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject(); atEight["formatVersion"] = 8;
        MapDocument upgraded = MapDocumentFile.LoadText(atEight.ToJsonString());
        Assert.Equal(9, upgraded.FormatVersion); Assert.Null(upgraded.Foliage);
        string current = MapSurfaceSemantics.RootDigest(upgraded);
        upgraded.FormatVersion = 8;
        Assert.NotEqual(current, MapSurfaceSemantics.RootDigest(upgraded));
        upgraded.FormatVersion = 9;
        Assert.Equal(current, MapSurfaceSemantics.RootDigest(MapDocumentFile.LoadText(MapDocumentFile.SaveText(upgraded))));
    }

    [Fact]
    public void StoredIdentity_VerifiesUnloadedFoliagePacksAndBindings()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create(); MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
        var pinned = MapStoredSurfaceSource.Open(dir.Root);
        string path = FoliageStorageFixtures.Packs(dir.Root)[0];
        byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(pinned, fixture.Assets, FoliageFixtures.Options()));
    }

    [Fact]
    public void InMemoryIdentity_RefusesIncompleteAdvertisedBytesWithoutTileIndex()
    {
        var fixture = FoliageFixtures.Create();
        MapFoliageLayer layer = fixture.Document.Foliage!.Layers[0];
        fixture.Document.Foliage = new(1, [MapFoliageLayer.FromStorage(layer, layer.ChunkDigests, [])]);
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(fixture.Document, fixture.Assets, FoliageFixtures.Options()));
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(fixture.Document));
    }

    [Fact]
    public void EachDensityByte_IndependentlyMovesBothAuthoredIdentityPaths()
    {
        using var dir = new FoliageStorageFixtures.DirectoryScope();
        var fixture = FoliageFixtures.Create();
        MapFoliageLayer baseline = fixture.Document.Foliage!.Layers[0];
        string before = MapAuthoredIdentityV2.Compute(fixture.Document, fixture.Assets, FoliageFixtures.Options());
        // Each iteration is a different one-byte authored proposal, including both chunks and all three rows.
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 35; x++)
            {
                var chunks = baseline.Chunks.Select(c =>
                {
                    byte[] bytes = c.CopyDensity();
                    if (c.Key.X == x / 32) bytes[z * 32 + x % 32] ^= 128;
                    return new MapFoliageChunk(c.Key, bytes);
                }).ToArray();
                fixture.Document.Foliage = new(1, [MapFoliageLayer.FromStorage(baseline, baseline.ChunkDigests, chunks)]);
                string monolithic = MapAuthoredIdentityV2.Compute(fixture.Document, fixture.Assets, FoliageFixtures.Options());
                Assert.NotEqual(before, monolithic);
                MapDocumentFile.SaveTiled(fixture.Document, dir.Root);
                Assert.Equal(monolithic, MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(dir.Root), fixture.Assets, FoliageFixtures.Options()));
            }
    }

    static JsonObject Space() => new()
    {
        ["kind"] = "Space",
        ["spaceId"] = "hall",
        ["referenceY"] = 1,
        ["searchBelow"] = 1,
        ["searchAbove"] = 1,
    };

    static string[] Annotations(MapDocument document, MapAssetClosure assets)
    {
        MapBuildingIntent? original = document.BuildingIntent;
        try
        {
            return new[] { 1, 2 }.Select(v =>
            {
                var marker = new MapLocalMarkerDoc(v, "marker", "Marker", new Vector3(0, 1, 0), 0, "test", true, [])
                { HeightMode = v == 2 ? MapMarkerHeightMode.Explicit : null };
                document.BuildingIntent = new(1, [], [], [], [marker]);
                return MapPrefabResolver.Expand(document, assets).AnnotationDigest;
            }).ToArray();
        }
        finally { document.BuildingIntent = original; }
    }
    static void Change(JsonObject layer, string field)
    {
        switch (field)
        {
            case "id": layer[field] = "cover-b"; break;
            case "support": layer[field]!["surfaceId"] = "upper"; break;
            case "support-kind": layer["support"] = Space(); break;
            case "space-spaceId": layer["support"]!["spaceId"] = "other-hall"; break;
            case "space-referenceY": case "space-searchBelow": case "space-searchAbove": layer["support"]![field[6..]] = 2; break;
            case "originX":
            case "originZ":
            case "cellSize":
            case "spacing":
            case "scaleMin":
            case "rootOffset":
            case "doorClearance":
            case "edgeFade": layer[field] = layer[field]!.GetValue<float>() + 0.1f; break;
            case "width": case "depth": case "seed": layer[field] = layer[field]!.GetValue<int>() + 1; break;
            case "scaleMax": layer[field] = 2; break;
            case "model-id": layer["models"]![0]!["assetId"] = "tree"; break;
            case "model-weight": layer["models"]![0]!["weight"] = 4; break;
            case "model-order":
                var models = layer["models"]!.AsArray(); JsonNode first = models[0]!.DeepClone(); models.RemoveAt(0); models.Add(first); break;
            case "allowed-material": layer["allowedVisibleMaterials"]!.AsArray().RemoveAt(1); break;
            case "material-id": layer["materials"]![2]!["id"] = 13; break;
            case "material-resource": layer["materials"]![0]!["resourceId"] = "soil-material"; break;
            case "material-kind": layer["materials"]![2]!["kind"] = "Ordinary"; break;
            case "normal-boundary": layer["policy"]!["normalBoundaryHeight"] = 0.25f; break;
            case "excludeIndoors": case "excludeSolidObjects": layer[field] = false; break;
            case "ordinal-surface": layer["compatibilityOrdinals"]!.AsArray().RemoveAt(1); break;
            case "ordinal-value": layer["compatibilityOrdinals"]![1]!["ordinal"] = 1; break;
            case "rule-id": layer["clearanceRules"]![0]!["id"] = "rule-b"; break;
            case "rule-asset": layer["clearanceRules"]![0]!["assetId"] = "reed-a"; break;
            case "rule-tag": layer["clearanceRules"]![0]!["placementTag"] = "entrance"; break;
            case "rule-kind": layer["clearanceRules"]![0]!["kind"] = "Solid"; break;
            case "rule-minX":
            case "rule-minZ":
            case "rule-maxX":
            case "rule-maxZ":
                string edge = field[5..]; layer["clearanceRules"]![0]!["footprint"]![edge] = layer["clearanceRules"]![0]!["footprint"]![edge]!.GetValue<float>() + 0.1f; break;
            case "mask-id": layer["exceptionMasks"]![0]!["id"] = "mask-b"; break;
            case "mask-owner-kind": layer["exceptionMasks"]![0]!["owner"]!["kind"] = "Marker"; break;
            case "mask-support-kind": layer["exceptionMasks"]![0]!["support"] = Space(); break;
            case "mask-minX":
            case "mask-minZ":
            case "mask-maxZ":
                string maskEdge = field[5..]; layer["exceptionMasks"]![0]!["footprint"]![maskEdge] = layer["exceptionMasks"]![0]!["footprint"]![maskEdge]!.GetValue<float>() + 0.1f; break;
            case "mask-owner": layer["exceptionMasks"]![0]!["owner"]!["id"] = "house-2"; break;
            case "mask-support": layer["exceptionMasks"]![0]!["support"]!["surfaceId"] = "upper"; break;
            case "mask-kind": layer["exceptionMasks"]![0]!["kind"] = "Solid"; break;
            case "mask-footprint": layer["exceptionMasks"]![0]!["footprint"]!["maxX"] = 3; break;
            case "owner-kind": layer["ownerSupports"]![0]!["owner"]!["kind"] = "Marker"; break;
            case "owner-support-kind": layer["ownerSupports"]![0]!["support"] = new JsonObject { ["kind"] = "Surface", ["surfaceId"] = "floor" }; break;
            case "owner-spaceId": layer["ownerSupports"]![0]!["support"]!["spaceId"] = "other-hall"; break;
            case "owner-searchBelow": case "owner-searchAbove": layer["ownerSupports"]![0]!["support"]![field[6..]] = 2; break;
            case "owner-reference": layer["ownerSupports"]![0]!["owner"]!["id"] = "house-2"; break;
            case "owner-support": layer["ownerSupports"]![0]!["support"]!["referenceY"] = 2; break;
            case "byte":
                byte[] bytes = Convert.FromBase64String(layer["chunks"]![0]!["density"]!.GetValue<string>()); bytes[0] ^= 1;
                layer["chunks"]![0]!["density"] = Convert.ToBase64String(bytes); break;
            case "explicit-zero": layer["chunks"]![0]!["density"] = Convert.ToBase64String(new byte[1024]); break;
            default: throw new ArgumentException(field);
        }
    }
}
