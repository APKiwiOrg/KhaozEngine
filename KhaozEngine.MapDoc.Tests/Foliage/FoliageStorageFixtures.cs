using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static class FoliageStorageFixtures
{
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Json(MapFoliageSet set) => JsonSerializer.Serialize(set);
    internal static MapFoliageLayer Change(MapFoliageLayer layer, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(Json(new(1, [layer])))!.AsObject();
        change(root["layers"]![0]!.AsObject());
        return JsonSerializer.Deserialize<MapFoliageSet>(root.ToJsonString())!.Layers[0];
    }
    internal static FoliageFixture RegionFixture()
    {
        FoliageFixture fixture = FoliageFixtures.Create();
        MapFoliageLayer layer = Change(FoliageFixtures.BasicLayer([]), l =>
        {
            l["originX"] = 2; l["originZ"] = 0; l["width"] = 320; l["depth"] = 32;
            l["chunks"] = new JsonArray(new[] { 0, 7, 8 }.Select(x => (JsonNode)new JsonObject
            {
                ["x"] = x,
                ["z"] = 0,
                ["density"] = Convert.ToBase64String(Enumerable.Repeat((byte)(x + 10), 1024).ToArray()),
            }).ToArray());
        });
        fixture.Document.Foliage = new(1, [layer]);
        fixture.Document.TileSize = 512;
        return fixture;
    }
    internal static FoliageFixture ManyPacks(int count = 64, bool withTiles = false)
    {
        FoliageFixture fixture = FoliageFixtures.Create();
        var layer = Change(FoliageFixtures.BasicLayer([]), l =>
        {
            l["originX"] = 0; l["originZ"] = 0; l["width"] = count * 32; l["depth"] = 32;
            l["chunks"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["x"] = i,
                ["z"] = 0,
                ["density"] = Convert.ToBase64String(Enumerable.Repeat((byte)(i + 1), 1024).ToArray()),
            }).ToArray());
        });
        fixture.Document.Foliage = new(1, [layer]);
        fixture.Document.TileSize = 64;
        if (withTiles)
            for (int i = 0; i < count; i++) fixture.Document.Placements.Add(new MapPlacement
            {
                Id = "tree-" + i,
                AssetId = "tree",
                Kind = "scenery",
                NumericId = MapNumericIds.Allocate(fixture.Document),
                X = i * 64 + 4,
                Z = 4,
                Y = 1,
            });
        return fixture;
    }

    internal static JsonObject Manifest(string root) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "map.json")))!.AsObject();
    internal static string[] Packs(string root) => Directory.GetFiles(Path.Combine(root, "tiles", "foliage"), "*.pack", SearchOption.AllDirectories);
    internal sealed class DirectoryScope : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "foliage-storage-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
