using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc;

internal static partial class NativeRenderAssetFixtures
{
    static byte[] PlacementLod(int tier, bool originVertex = false)
    {
        byte[] glb = Glb();
        int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject();
        byte[] bin = glb.AsSpan(28 + length).ToArray();
        float[] positions = tier == 1
            ? new float[] { -5, 3, 4, 5, 3, 4, -5, 10, 4, -5, 6, 4, 5, 6, 4, -5, 12, 4 }
            : new float[] { -8, 3, 4, 8, 3, 4, -8, 16, 4, -8, 6, 4, 8, 6, 4, -8, 18, 4 };
        float extent = tier == 1 ? 5 : 8;
        if (originVertex) positions[0] = 0;
        for (int i = 0; i < positions.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 4), positions[i]);
        root["accessors"]![0]!["min"] = new JsonArray(-extent, 3, 4);
        root["accessors"]![0]!["max"] = new JsonArray(extent, positions[7], 4);
        root["accessors"]![1]!["min"] = new JsonArray(-extent, 6, 4);
        root["accessors"]![1]!["max"] = new JsonArray(extent, positions[16], 4);
        return Pack(root, bin);
    }

    internal static MapAssetClosure Placements(bool sharedRoofAsset = false, bool originVertex = false, string? shadowExclusionJson = null, bool leafLightResource = false,
        byte[]? coarsestLod = null)
    {
        var source = new Source();
        var resources = new JsonArray();
        void Resource(string id, string kind, byte[] bytes, params string[] dependencies)
        {
            var reference = source.Add(id, bytes);
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject { ["id"] = id, ["path"] = reference.Path, ["sha256"] = reference.Sha256, ["payloadVersion"] = 1 },
                ["kind"] = kind,
                ["dependencies"] = new JsonArray(dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
            });
        }
        Resource("mesh", "Mesh", Glb());
        Resource("lod-1", "Lod", PlacementLod(1));
        Resource("lod-2", "Lod", coarsestLod ?? PlacementLod(2, originVertex));
        if (leafLightResource)
        {
            var light = JsonNode.Parse(MapLightFile.Write(new(1, "fixture-light",
                new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "$owner"),
                new(0, .5f, 0), new(1, 1, 1), 2, 3, true)))!;
            if (shadowExclusionJson is not null) light["shadowExclusion"] = JsonNode.Parse(shadowExclusionJson);
            Resource("fixture-light", "Light", Encoding.UTF8.GetBytes(light.ToJsonString()));
        }
        var children = new List<MapPrefabChild>();
        foreach (var (id, asset, x, y) in new[] { ("leaf-a", "prop-a", .125f, .25f), ("leaf-b", "prop-a", 2f, 0f),
            ("leaf-c", "prop-b", 4f, 0f), ("roof-a", sharedRoofAsset ? "prop-a" : "cap-a", 0f, 3f),
            ("roof-b", sharedRoofAsset ? "prop-a" : "cap-a", 4f, 3f) })
            children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, id), new MapPlacement
            {
                Id = id,
                AssetId = asset,
                Kind = "fixture-kind",
                X = x,
                Y = y,
                Z = id == "leaf-a" ? -.375f : 0,
                Yaw = id == "leaf-a" ? .2f : 0,
                Scale = id == "leaf-a" ? .75f : 1,
                Tags = new() { "second", "first" }
            }));
        for (int i = 0; i < 17; i++)
            children.Add(new MapPrefabLightChild(new(MapPrefabChildKind.Light, "emitter-" + i.ToString("D2")),
                new(1, "emitter-" + i.ToString("D2"), new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "leaf-a"),
                    new(i * .125f, .5f, -.25f), new(1, .25f, .5f), 2 + i, 3, i % 2 == 0)));
        children.Add(new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "pin"),
            new(1, "pin", "invented pin", new(.25f, 1, .5f), 0, "fixture-role", true, new[] { "later", "earlier" })));
        var prefab = new MapPrefabAsset(1, "assembly", children)
        {
            Roofs = new[] { new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof-a"),
                new MapPrefabReference(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "roof-b") }
        };
        byte[] prefabBytes = MapPrefabFile.Write(prefab);
        if (shadowExclusionJson is not null && !leafLightResource)
        {
            var json = JsonNode.Parse(prefabBytes)!;
            foreach (var child in json["children"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "Light"))
                child!["value"]!["shadowExclusion"] = JsonNode.Parse(shadowExclusionJson);
            prefabBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        Resource("assembly", "Prefab", prefabBytes, "mesh", "lod-1", "lod-2");
        var assets = new JsonArray();
        string[] assetIds = leafLightResource ? ["prop-a", "prop-b", "cap-a", "lamp"] : ["prop-a", "prop-b", "cap-a"];
        foreach (string id in assetIds)
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = "mesh",
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = new JsonArray("lod-1", "lod-2"),
                ["lightResourceIds"] = id == "lamp" ? new JsonArray("fixture-light") : new JsonArray(),
                ["sourceUnitsToMetres"] = .01f,
                ["renderBounds"] = Bounds(10),
                ["lodBounds"] = Bounds(20),
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = true
            });
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString(MapJsonTextOptions.CreateSerializer(false))));
        return MapAssetClosure.Load(new[] { root }, source);
    }
}
