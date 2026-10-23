using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.Tests.MapDoc;

internal static partial class NativeRenderAssetFixtures
{
    internal static MapAssetClosure OffsetPlacements()
    {
        var source = new Source();
        var assets = new JsonArray(); var resources = new JsonArray();
        foreach (var (id, minX) in new[] { ("prop-a", -.5f), ("prop-b", 2f) })
        {
            byte[] glb = Glb();
            int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
            var json = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject();
            byte[] bin = glb.AsSpan(28 + length).ToArray();
            float[] positions = [minX, 0, 0, minX + 1, 0, 0, minX, 1, 0];
            for (int part = 0; part < 2; part++)
            {
                for (int i = 0; i < positions.Length; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(part * 36 + i * 4), positions[i]);
                json["accessors"]![part]!["min"] = new JsonArray(minX, 0, 0);
                json["accessors"]![part]!["max"] = new JsonArray(minX + 1, 1, 0);
            }
            foreach (var (suffix, kind) in new[] { ("mesh", "Mesh"), ("lod-1", "Lod"), ("lod-2", "Lod") })
            {
                var reference = source.Add(id + "-" + suffix, Pack(json, bin));
                resources.Add(new JsonObject
                {
                    ["reference"] = new JsonObject { ["id"] = reference.Id, ["path"] = reference.Path, ["sha256"] = reference.Sha256, ["payloadVersion"] = 1 },
                    ["kind"] = kind,
                    ["dependencies"] = new JsonArray()
                });
            }
            var bounds = new JsonObject
            {
                ["min"] = new JsonObject { ["x"] = minX, ["y"] = 0, ["z"] = 0 },
                ["max"] = new JsonObject { ["x"] = minX + 1, ["y"] = 1, ["z"] = 0 }
            };
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = id + "-mesh",
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = new JsonArray(id + "-lod-1", id + "-lod-2"),
                ["lightResourceIds"] = new JsonArray(),
                ["sourceUnitsToMetres"] = 1,
                ["renderBounds"] = bounds,
                ["lodBounds"] = bounds.DeepClone(),
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = true
            });
        }
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString()));
        return MapAssetClosure.Load([root], source);
    }
}
