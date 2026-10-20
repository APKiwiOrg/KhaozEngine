using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>Complete foliage wire DTOs: every density byte, position, quaternion and matrix component of a reachable
/// preview against independent binary32 expectations, and an arbitrary pose through the direct mapping and options.</summary>
[Collection(McpWireCollection.Name)]
public sealed class FoliageWireTests
{
    // The released channel salts in JitterX, JitterZ, Density, Model, Scale, Yaw and Rank order, copied here so the
    // expectations never call the product evaluator.
    static readonly ulong[] Salts =
    [
        0x60B7A3D53B14C2A1UL, 0x91E10DA5C79E7B1DUL, 0xD18A40B4F35062C7UL, 0xA4FBCB9528163E09UL,
        0x9C3C8D7E5F2A61B4UL, 0x3D17F0A58C6B42E9UL, 0xE2A86149B7D30C5FUL,
    ];

    static readonly string[] MatrixNames =
        ["m11", "m12", "m13", "m14", "m21", "m22", "m23", "m24", "m31", "m32", "m33", "m34", "m41", "m42", "m43", "m44"];

    [Fact]
    public async Task FoliageWire_ContainsEveryByteQuaternionAndMatrixComponent()
    {
        FoliageFixture f = FoliageFixtures.ReachableWirePreview();
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await FoliageToolTests.Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();
            string before = FoliageEditorFixtures.State(live);
            var args = new Dictionary<string, object?>
            {
                ["layerId"] = FoliageFixtures.WireLayerId,
                ["x"] = 0,
                ["z"] = 0,
                ["width"] = 2,
                ["depth"] = 2,
                ["previewChunkJson"] = "{\"x\":0,\"z\":0}",
            };
            JsonElement body = FoliageToolTests.Body(await FoliageToolTests.Call(harness, "foliage_get", args));
            Assert.Equal("layer", body.GetProperty("mode").GetString());
            Assert.Equal(FoliageFixtures.WireLayerId, body.GetProperty("layer").GetProperty("id").GetString());

            // All 1024 density bytes of the requested chunk travel as base64, with the chunk digest.
            byte[] expected1024Bytes = FoliageFixtures.WireDensity();
            JsonElement chunk = Assert.Single(body.GetProperty("chunks").EnumerateArray());
            Assert.Equal((0, 0), (chunk.GetProperty("x").GetInt32(), chunk.GetProperty("z").GetInt32()));
            JsonElement densityJson = chunk.GetProperty("density");
            Assert.Equal(JsonValueKind.String, densityJson.ValueKind);
            Assert.Equal(expected1024Bytes, Convert.FromBase64String(densityJson.GetString()!));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected1024Bytes)), chunk.GetProperty("sha256").GetString());

            // Independent channels at lattice address (1, 1), seed 17: signed packing, modulo 2^64 avalanche, released salts.
            uint[] high = Salts.Select(salt => High24(1, 1, 17, salt)).ToArray();
            Assert.Equal(new uint[] { 4333905, 10960137, 4442925, 13264764, 16452242, 951027, 13291187 }, high);
            float jitterX = high[0] / 16_777_216f, jitterZ = high[1] / 16_777_216f;
            float x = (1 + jitterX - 0.5f) * 0.5f;
            float z = (1 + jitterZ - 0.5f) * 0.5f;
            Assert.Equal(0.37916040420532227, (double)x);
            Assert.Equal(0.5766375064849854, (double)z);
            // Strictly inside the raster [0, 1], the floor domain [-4, 4] and preview chunk (0, 0) at [0, 32).
            foreach (float axis in new[] { x, z }) Assert.True(axis > 0f && axis < 1f && axis > -4f && axis < 4f && axis < 32f);
            // Four 255 samples interpolate to density 1, which accepts the density channel.
            Assert.True(high[2] / 16_777_216f < 1f);
            float scale = 1.2f;
            float rank = 13291187f / 16_777_216f;
            float yaw = (951027f / 16_777_216f) * MathF.Tau;
            Assert.Equal(0.3561663031578064, (double)yaw);
            var position = new Vector3(x, 2.5f, z);
            // The UnitY normal aligns exactly with diag(-1, 1, -1, 1).
            var alignment = new Matrix4x4(-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1);
            Matrix4x4 transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * alignment * Matrix4x4.CreateTranslation(x, 2.5f, z);
            Assert.True(Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _));
            Quaternion orientation = Quaternion.Normalize(rotation);
            Assert.NotEqual(Quaternion.Identity, orientation);

            JsonElement preview = body.GetProperty("preview");
            Assert.Equal("Complete", preview.GetProperty("status").GetString());
            Assert.Equal(FoliageFixtures.WireLayerId, preview.GetProperty("layerId").GetString());
            Assert.Equal((0, 0), (preview.GetProperty("chunkX").GetInt32(), preview.GetProperty("chunkZ").GetInt32()));
            Assert.Equal(64, preview.GetProperty("dependencyDigest").GetString()!.Length);
            JsonElement sample = Assert.Single(preview.GetProperty("samples").EnumerateArray(), s =>
            {
                JsonElement address = s.GetProperty("key").GetProperty("address");
                return address.GetProperty("x").GetInt32() == 1 && address.GetProperty("z").GetInt32() == 1 &&
                    address.GetProperty("supportOwnerId").GetString() == FoliageFixtures.WireFloorId;
            });
            JsonElement key = sample.GetProperty("key");
            Assert.Equal(FoliageFixtures.WireLayerId, key.GetProperty("address").GetProperty("layerId").GetString());
            Assert.Equal(1, key.GetProperty("distributionVersion").GetInt32());
            Assert.Equal("legacy-tile", key.GetProperty("policy").GetProperty("id").GetString());
            Assert.Equal(1, key.GetProperty("policy").GetProperty("version").GetInt32());
            Assert.Equal(JsonValueKind.Null, sample.GetProperty("occupiedSpaceId").ValueKind);
            Assert.Equal(FoliageFixtures.WireFloorId, sample.GetProperty("face").GetProperty("ownerId").GetString());
            Assert.Equal(FoliageFixtures.WireModelId, sample.GetProperty("assetId").GetString());
            Assert.Equal(["material"], sample.GetProperty("materialResourceIds").EnumerateArray().Select(m => m.GetString()));
            Assert.Equal(10, sample.GetProperty("visibleMaterial").GetInt32());

            JsonElement positionJson = sample.GetProperty("position");
            Assert.Equal(["x", "y", "z"], positionJson.EnumerateObject().Select(p => p.Name));
            Bits(position.X, positionJson.GetProperty("x"));
            Bits(position.Y, positionJson.GetProperty("y"));
            Bits(position.Z, positionJson.GetProperty("z"));
            JsonElement orientationJson = sample.GetProperty("orientation");
            Assert.Equal(4, orientationJson.EnumerateObject().Count());
            Assert.Equal(["x", "y", "z", "w"], orientationJson.EnumerateObject().Select(p => p.Name));
            Bits(orientation.X, orientationJson.GetProperty("x"));
            Bits(orientation.Y, orientationJson.GetProperty("y"));
            Bits(orientation.Z, orientationJson.GetProperty("z"));
            Bits(orientation.W, orientationJson.GetProperty("w"));
            var returned = new Quaternion(orientationJson.GetProperty("x").GetSingle(), orientationJson.GetProperty("y").GetSingle(),
                orientationJson.GetProperty("z").GetSingle(), orientationJson.GetProperty("w").GetSingle());
            Assert.NotEqual(Quaternion.Identity, returned);
            JsonElement transformJson = sample.GetProperty("transform");
            Assert.Equal(16, transformJson.EnumerateObject().Count());
            Assert.Equal(MatrixNames, transformJson.EnumerateObject().Select(p => p.Name));
            float[] components = Components(transform);
            for (int i = 0; i < 16; i++) Bits(components[i], transformJson.GetProperty(MatrixNames[i]));
            Assert.Equal(1f, transformJson.GetProperty("m44").GetSingle());
            Bits(scale, sample.GetProperty("scale"));
            Bits(rank, sample.GetProperty("thinningRank"));

            // Decoded arrays are detached: changing them changes no session state, and the next read is unchanged.
            byte[] decoded = Convert.FromBase64String(densityJson.GetString()!);
            decoded[0] = 7;
            JsonElement again = FoliageToolTests.Body(await FoliageToolTests.Call(harness, "foliage_get", args));
            Assert.Equal(expected1024Bytes, Convert.FromBase64String(again.GetProperty("chunks")[0].GetProperty("density").GetString()!));
            Assert.Equal(before, FoliageEditorFixtures.State(live));
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    [Fact]
    public void FoliageDto_ArbitraryPoseRoundTripsAllComponents()
    {
        float yaw = 0.371f, tilt = 0.2f, scale = 1.2f;
        var translation = new Vector3(-1.25f, 2.5f, 3.75f);
        Matrix4x4 transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(tilt) *
            Matrix4x4.CreateTranslation(translation);
        Assert.True(Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _));
        Quaternion orientation = Quaternion.Normalize(rotation);
        var key = new MapFoliageSampleKey(new MapFoliageAddress("dto-cover", "dto-floor", -3, 5), 1, new MapFoliagePolicy("legacy-tile", 1, 0));
        var face = new MapFaceKey("dto-floor", new MapPatchKey("dto-floor", -1, 2), 3, 1, 2, MapSide.Front);
        var sample = new MapFoliageSample(key, "dto-room", face, "dto-reed", ["dto-material"], 11, translation, orientation, scale, transform, 0.625f);
        var batch = new MapFoliageSampleBatch("dto-cover", new(-1, 0), MapFoliageStatus.Complete, new string('a', 64), [sample],
            new MapFoliageWork(1, 1, 0, 0, 0, 0, 0, 256));
        MapFoliageLayer layer = FoliageFixtures.BasicLayer(decorated: true);
        var info = new MapFoliageLayerInfo(layer);
        Assert.Equal(layer.ChunkDigests.Select(d => d.Key), info.ChunkKeys);
        var result = new MapFoliageReadResult(MapFoliageReadMode.Layer, [], info, layer.Chunks, batch);

        FoliageReadWireResult wire = FoliageWire.ToWire(result);
        wire.Chunks[0].Density[0] ^= 0xFF;
        Assert.Equal(layer.Chunks[0].CopyDensity(), FoliageWire.ToWire(result).Chunks[0].Density);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(FoliageWire.ToWire(result), FoliageWire.ToolOptions));
        JsonElement root = json.RootElement;
        Assert.Equal("layer", root.GetProperty("mode").GetString());

        JsonElement s = Assert.Single(root.GetProperty("preview").GetProperty("samples").EnumerateArray());
        Assert.Equal("Front", s.GetProperty("face").GetProperty("side").GetString());
        Assert.Equal(-3, s.GetProperty("key").GetProperty("address").GetProperty("x").GetInt32());
        Assert.Equal("dto-room", s.GetProperty("occupiedSpaceId").GetString());
        JsonElement p = s.GetProperty("position");
        Bits(translation.X, p.GetProperty("x"));
        Bits(translation.Y, p.GetProperty("y"));
        Bits(translation.Z, p.GetProperty("z"));
        JsonElement q = s.GetProperty("orientation");
        Assert.Equal(["x", "y", "z", "w"], q.EnumerateObject().Select(m => m.Name));
        Bits(orientation.X, q.GetProperty("x"));
        Bits(orientation.Y, q.GetProperty("y"));
        Bits(orientation.Z, q.GetProperty("z"));
        Bits(orientation.W, q.GetProperty("w"));
        Assert.True(orientation.X != 0 && orientation.Y != 0 && orientation.Z != 0, "the tilted pose rotates about every axis");
        JsonElement m = s.GetProperty("transform");
        Assert.Equal(MatrixNames, m.EnumerateObject().Select(e => e.Name));
        float[] components = Components(transform);
        for (int i = 0; i < 16; i++) Bits(components[i], m.GetProperty(MatrixNames[i]));
        // Zero-valued members are written explicitly.
        foreach (string name in new[] { "m14", "m24", "m34" }) Assert.Equal(0f, m.GetProperty(name).GetSingle());
        Assert.Equal(1f, m.GetProperty("m44").GetSingle());
        Bits(scale, s.GetProperty("scale"));
        Bits(0.625f, s.GetProperty("thinningRank"));
        Assert.Equal(11, s.GetProperty("visibleMaterial").GetInt32());

        // Layer metadata carries chunk keys and no chunk memory, with closed support bindings of either kind.
        JsonElement layerJson = root.GetProperty("layer");
        Assert.False(layerJson.TryGetProperty("chunks", out _));
        Assert.Equal(layer.ChunkDigests.Count, layerJson.GetProperty("chunkKeys").GetArrayLength());
        Assert.Equal(["kind", "surfaceId"], layerJson.GetProperty("support").EnumerateObject().Select(e => e.Name));
        JsonElement space = Assert.Single(layerJson.GetProperty("ownerSupports").EnumerateArray()).GetProperty("support");
        Assert.Equal(["kind", "spaceId", "referenceY", "searchBelow", "searchAbove"], space.EnumerateObject().Select(e => e.Name));
        MapSupportBinding binding = layer.OwnerSupports[0].Support;
        Assert.Equal(binding, JsonSerializer.Deserialize<MapSupportBinding>(space.GetRawText(), FoliageWire.ToolOptions));
        Assert.Equal(layer.Support, JsonSerializer.Deserialize<MapSupportBinding>(layerJson.GetProperty("support").GetRawText(), FoliageWire.ToolOptions));
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<MapSupportBinding>("{\"kind\":\"Surface\",\"surfaceId\":\"floor\",\"spaceId\":null}",
            FoliageWire.ToolOptions));
        Assert.Equal(Convert.ToBase64String(layer.Chunks[0].CopyDensity()), root.GetProperty("chunks")[0].GetProperty("density").GetString());
    }

    static uint High24(int x, int z, int seed, ulong salt)
    {
        unchecked
        {
            ulong key = ((ulong)(uint)x << 32) | (uint)z;
            ulong value = key ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ salt;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (uint)(value >> 40);
        }
    }

    static float[] Components(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    static void Bits(float expected, JsonElement actual) =>
        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual.GetSingle()));
}
