using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.Tests.MapDoc;

internal static class NativeAssetFixtures
{
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) Valid() => Create();
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) MissingLod() => Create("missing-lod");
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) Cycle() => Create("cycle");
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) StaleDigest() => Create("stale");
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) FuturePayload() => Create("future");
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) MissingMaterial() => Create("missing-material");
    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) DuplicateId() => Create("duplicate");

    internal static JsonObject Asset() => JsonNode.Parse("""
        {"id":"tree","meshResourceId":"mesh","collisionResourceId":"collider",
         "selectionResourceId":"selection","supportResourceIds":["surface"],
         "materialResourceIds":["material"],"lodResourceIds":["lod"],"lightResourceIds":["light"],
         "sourceUnitsToMetres":0.01,"renderBounds":{"min":{"x":-2,"y":0.5,"z":-3},"max":{"x":2,"y":8,"z":3}},
         "lodBounds":{"min":{"x":-1,"y":0,"z":-1},"max":{"x":1,"y":7,"z":1}},
         "lightBounds":{"min":{"x":-4,"y":0,"z":-4},"max":{"x":4,"y":9,"z":4}},
         "source":"authored/tree.blend","license":"CC0","category":"trees","textured":true}
        """)!.AsObject();

    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) Create(string? fault = null)
    {
        var source = new NativeMemoryAssetSource();
        var first = new JsonArray();
        var second = new JsonArray();
        foreach (var (id, kind, target) in new[] {
            ("mesh", "Mesh", first), ("collider", "Collider", first), ("selection", "Selection", first),
            ("surface", "Surface", first), ("light", "Light", first), ("lod", "Lod", second),
            ("lod-mesh", "Mesh", second), ("material", "Material", second) })
        {
            MapAssetRef reference = source.Add(id, Encoding.UTF8.GetBytes("bytes:" + id));
            var deps = id == "mesh" ? new[] { "material", "lod" } :
                id == "lod" ? new[] { "lod-mesh" } :
                id == "material" && fault == "cycle" ? new[] { "mesh" } : Array.Empty<string>();
            if ((id == "lod" && fault == "missing-lod") || (id == "material" && fault == "missing-material")) continue;
            target.Add(Resource(reference, kind, deps));
        }
        if (fault == "duplicate") second.Add(first[0]!.DeepClone());
        var rootA = source.Add("root-a", new JsonObject
        {
            ["payloadVersion"] = fault == "future" ? 2 : 1,
            ["assets"] = new JsonArray(Asset()),
            ["resources"] = first
        }.ToJsonString());
        var rootB = source.Add("root-b", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = second
        }.ToJsonString());
        if (fault == "stale") source.Corrupt("mesh");
        return (new[] { rootA, rootB }, source);
    }

    internal static JsonObject Resource(MapAssetRef reference, string kind, params string[] dependencies) => new()
    {
        ["reference"] = new JsonObject
        {
            ["id"] = reference.Id,
            ["path"] = reference.Path,
            ["sha256"] = reference.Sha256,
            ["payloadVersion"] = reference.PayloadVersion
        },
        ["kind"] = kind,
        ["dependencies"] = new JsonArray(dependencies.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
    };

    internal static (IReadOnlyList<MapAssetRef> Roots, NativeMemoryAssetSource Source) Change(Action<JsonObject> change)
    {
        var (roots, source) = Valid();
        var root = JsonNode.Parse(source.Read(roots[0]).Span)!.AsObject();
        change(root);
        return (new[] { source.Add("root-a", root.ToJsonString()), roots[1] }, source);
    }
}

internal sealed class NativeMemoryAssetSource : IMapAssetSource
{
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    internal MapAssetRef Add(string id, string text) => Add(id, Encoding.UTF8.GetBytes(text));
    internal MapAssetRef Add(string id, byte[] bytes)
    {
        _bytes[id] = bytes.ToArray();
        return new MapAssetRef(id, "assets/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
    }
    public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes.TryGetValue(reference.Id, out var bytes)
        ? bytes : throw new MapDocumentException("Missing fixture resource " + reference.Id);
    internal void Corrupt(string id) => _bytes[id][0] ^= 0xff;
}
