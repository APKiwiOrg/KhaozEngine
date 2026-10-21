using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.MapDoc;

internal static partial class NativeRenderAssetFixtures
{
    internal static readonly int[] ModelIndices = [0, 1, 0, 1];
    internal static readonly int[] ExpandedRows = [0, 2, 0, 2, 1, 3];
    internal static readonly int[] ExpandedMeshes = [0, 0, 1, 1, 2, 2];
    internal static readonly Vector4[] ExpectedParameters =
        [new(.1f, 0, 1, .5f), new(.3f, 0, 1, .5f), new(.1f, 0, 1, .5f),
         new(.3f, 0, 1, .5f), new(.2f, -1, .5f, .25f), new(.4f, -1, .5f, .25f)];
    internal static readonly Vector4[] ExpectedMotion =
        [new(.3f, .15f, .4f, .035f), new(.3f, .15f, .4f, .035f),
         new(.3f, .15f, .4f, .035f), new(.3f, .15f, .4f, .035f),
         new(.6f, .2f, .1f, .07f), new(.6f, .2f, .1f, .07f)];
    internal const int PackedStride = 96, ParamsOffset = 64, MotionOffset = 80;
    internal static readonly int[] SwappedExpandedRows = [1, 3, 1, 3, 0, 2];
    internal static readonly SceneFoliagePatch[] ExpectedPatches =
        [new(0, 0, new MeshHandle(0), true, 0, 2), new(0, 0, new MeshHandle(1), true, 2, 2),
         new(0, 0, new MeshHandle(2), false, 4, 2)];

    internal static SceneFoliageSource FoliageSource(bool swapped = false) => new(default,
        Enumerable.Repeat(Matrix4x4.CreateTranslation(1, 0, 1), 4).ToArray(), new float[] { .1f, .2f, .3f, .4f },
        swapped ? new[] { 1, 0, 1, 0 } : ModelIndices,
        [new("A", [new(0), new(1)], new(.3f, .15f, .4f, .035f), true),
         new("B", [new(2)], new(.6f, .2f, .1f, .07f), false)]);

    internal static GltfMeshPart[] FoliageParts() =>
        [FoliagePart(0, 1, .5f), FoliagePart(0, 1, .5f), FoliagePart(-1, 1, .25f)];
    static GltfMeshPart FoliagePart(float root, float top, float cutoff) => new(new GltfMesh(
        [new(new(0, root, 0), Vector3.UnitZ, Vector4.One), new(new(1, root, 0), Vector3.UnitZ, Vector4.One),
         new(new(0, top, 0), Vector3.UnitZ, Vector4.One)], new uint[] { 0, 1, 2 }), new(null, null, null, cutoff));

    internal static MapAssetClosure Create(string variant = "valid")
    {
        var source = new Source();
        var resources = new JsonArray();
        void Resource(string id, string kind, byte[] bytes, params string[] deps)
        {
            MapAssetRef reference = source.Add(id, bytes);
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["id"] = id,
                    ["path"] = reference.Path,
                    ["sha256"] = reference.Sha256,
                    ["payloadVersion"] = 1
                },
                ["kind"] = kind,
                ["dependencies"] = new JsonArray(deps.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            });
        }
        Resource("image", "Material", Png(variant == "resident" ? 256 : 1, variant == "resident" ? 1024 : 1));
        string material = variant == "terrain-extra-maps" ? Material().Replace("\"normal\":null,\"roughnessMap\":null",
            "\"normal\":\"image\",\"roughnessMap\":\"image\"", StringComparison.Ordinal) : Material();
        Resource("paint", "Material", Encoding.UTF8.GetBytes(variant == "opaque" ? "opaque" : material), "image");
        if (variant == "terrain-mismatched-albedo")
        {
            Resource("wide-image", "Material", Png(2, 1));
            Resource("wide-paint", "Material", Encoding.UTF8.GetBytes(Material().Replace("\"image\"", "\"wide-image\"", StringComparison.Ordinal)), "wide-image");
        }
        Resource("light", "Light", Encoding.UTF8.GetBytes("""
            {"resourceSchema":"kemap/light-resource","payloadVersion":1,"id":"bulb","owner":null,
             "position":{"x":0,"y":1,"z":0},"colour":{"x":1,"y":0,"z":0},
             "intensity":2,"radius":3,"castShadows":true}
            """));
        int count = variant is "distant" or "lying-distant" ? 24 : 1;
        var assets = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            string prefix = i == 0 ? "" : i + "-";
            int size = count == 24 ? 256 : 1;
            byte[] mesh = Glb(variant is "bad-image" or "corrupt-image" ? variant : "valid", size, count == 24 ? 1024 : 1, i);
            Resource(prefix + "mesh", "Mesh", mesh);
            Resource(prefix + "lod-1", "Lod", Glb("valid"));
            Resource(prefix + "lod-2", "Lod", Glb("valid"));
            bool lie = variant == "lying" || (variant == "lying-distant" && i == 23);
            assets.Add(new JsonObject
            {
                ["id"] = variant == "escaped-id" ? new string('\u0001', 20000) : i == 0 ? "prop-a" : "prop-" + i,
                ["meshResourceId"] = prefix + "mesh",
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray("paint"),
                ["lodResourceIds"] = new JsonArray(prefix + "lod-1", prefix + "lod-2"),
                ["lightResourceIds"] = new JsonArray("light"),
                ["sourceUnitsToMetres"] = .01f,
                ["renderBounds"] = Bounds(lie ? 0 : 10),
                ["lodBounds"] = Bounds(variant == "lying-lod" ? 0 : 10),
                ["lightBounds"] = Bounds(400),
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = true
            });
        }
        MapAssetRef root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString()));
        return MapAssetClosure.Load([root], source);
    }

    static JsonObject Bounds(int v) => new()
    {
        ["min"] = new JsonObject { ["x"] = -v, ["y"] = -v, ["z"] = -v },
        ["max"] = new JsonObject { ["x"] = v, ["y"] = v, ["z"] = v }
    };

    internal static string Material() => """
        {"schema":"kemap/render-material","payloadVersion":1,"baseColour":[1,0.5,0.25,1],
         "roughness":0.7,"metallic":0.1,"alphaCutoff":0.5,"albedo":"image","normal":null,"roughnessMap":null}
        """;

    internal static (MapAssetClosure Closure, string[] MaterialIds) TexturelessMaterials(int count)
    {
        var source = new Source();
        var resources = new JsonArray();
        var ids = new string[count];
        for (int i = 0; i < count; i++)
        {
            string id = ids[i] = "mat-" + i;
            MapAssetRef reference = source.Add(id, Encoding.UTF8.GetBytes(Material().Replace("\"albedo\":\"image\"", "\"albedo\":null", StringComparison.Ordinal)));
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject { ["id"] = id, ["path"] = reference.Path, ["sha256"] = reference.Sha256, ["payloadVersion"] = 1 },
                ["kind"] = "Material",
                ["dependencies"] = new JsonArray()
            });
        }
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = new JsonArray(), ["resources"] = resources }.ToJsonString()));
        return (MapAssetClosure.Load([root], source), ids);
    }

    internal static byte[] UnsupportedTexture(string channel, string fault)
    {
        byte[] glb = Glb();
        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root = JsonNode.Parse(glb.AsSpan(20, jsonLength))!.AsObject();
        byte[] extra = fault == "corrupt" ? Png()[..33] : Png(2, 2);
        root["images"]!.AsArray().Add(new JsonObject { ["uri"] = "data:image/png;base64," + Convert.ToBase64String(extra) });
        root["textures"]!.AsArray().Add(new JsonObject { ["source"] = 1 });
        if (channel == "extension")
        {
            root["extensionsUsed"] = new JsonArray("KHR_materials_clearcoat");
            root["materials"]![0]!["extensions"] = new JsonObject
            { ["KHR_materials_clearcoat"] = new JsonObject { ["clearcoatTexture"] = new JsonObject { ["index"] = 1 } } };
        }
        else
        {
            root["materials"]![0]![channel] = new JsonObject { ["index"] = 1 };
            if (channel == "emissiveTexture") root["materials"]![0]!["emissiveFactor"] = new JsonArray(1, 1, 1);
        }
        return Pack(root, glb.AsSpan(28 + jsonLength).ToArray());
    }

    internal static byte[] Glb(string variant = "valid", int width = 1, int height = 1, int seed = 0)
    {
        byte[] image = Png(width, height, (byte)(220 + seed % 30));
        if (variant == "large-image")
        {
            BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(16), 32768);
            BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(20), 32768);
            uint crc = uint.MaxValue;
            foreach (byte value in image.AsSpan(12, 17))
            {
                crc ^= value;
                for (int n = 0; n < 8; n++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(29), ~crc);
        }
        if (variant == "bad-image") image = [1, 2, 3, 4];
        if (variant == "corrupt-image") image = image[..33];
        var bin = new byte[72 + image.Length];
        float[] positions = [-2, 3, 4, 2, 3, 4, -2, 5, 4, -2, 6, 4, 2, 6, 4, -2, 8, 4];
        positions[0] += seed * .01f;
        for (int i = 0; i < positions.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(i * 4), positions[i]);
        image.CopyTo(bin, 72);
        var root = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0},{"attributes":{"POSITION":1},"material":1}]}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[-2,3,4],"max":[2,5,4]},
                          {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3","min":[-2,6,4],"max":[2,8,4]}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":36},
                            {"buffer":0,"byteOffset":72,"byteLength":0}],
             "buffers":[{"byteLength":0}],"images":[{"bufferView":2,"mimeType":"image/png"}],
             "textures":[{"source":0}],"materials":[
               {"alphaMode":"MASK","alphaCutoff":0.5,"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}},
               {"alphaMode":"MASK","alphaCutoff":0.25,"pbrMetallicRoughness":{"baseColorFactor":[0,0,1,1]}}]}
            """)!.AsObject();
        root["bufferViews"]![2]!["byteLength"] = image.Length;
        root["buffers"]![0]!["byteLength"] = bin.Length;
        if (variant == "large-accessor") root["accessors"]![0]!["count"] = 1048577;
        if (variant == "external") root["images"]![0] = new JsonObject { ["uri"] = "unverified.png" };
        if (variant == "instanced") root["nodes"]!.AsArray().Add(new JsonObject { ["mesh"] = 0 });
        return Pack(root, bin);
    }

    internal static byte[] Pack(JsonObject root, byte[] bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int j = (json.Length + 3) & ~3, b = (bin.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546c67u); writer.Write(2); writer.Write(28 + j + b);
        writer.Write(j); writer.Write(0x4e4f534au); writer.Write(json);
        for (int i = json.Length; i < j; i++) writer.Write((byte)' ');
        writer.Write(b); writer.Write(0x004e4942u); writer.Write(bin);
        for (int i = bin.Length; i < b; i++) writer.Write((byte)0);
        return stream.ToArray();
    }

    internal static byte[] Png(int width = 1, int height = 1, byte red = 220)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] bytes)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length); stream.Write(word);
            byte[] kind = Encoding.ASCII.GetBytes(name); stream.Write(kind); stream.Write(bytes);
            uint crc = uint.MaxValue;
            foreach (byte value in kind.Concat(bytes))
            {
                crc ^= value;
                for (int n = 0; n < 8; n++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); stream.Write(word);
        }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; Chunk("IHDR", header);
        using var data = new MemoryStream();
        var row = new byte[checked(1 + width * 4)];
        for (int x = 0; x < width; x++)
        { row[x * 4 + 1] = red; row[x * 4 + 2] = 20; row[x * 4 + 3] = 20; row[x * 4 + 4] = 255; }
        using (var z = new ZLibStream(data, CompressionLevel.Fastest, true))
            for (int y = 0; y < height; y++) z.Write(row);
        Chunk("IDAT", data.ToArray()); Chunk("IEND", []);
        return stream.ToArray();
    }

    sealed class Source : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        internal MapAssetRef Add(string id, byte[] bytes)
        {
            _data[id] = bytes;
            return new(id, "invented/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _data[reference.Id];
    }
}
