using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapAccessorRefusalTests
{
    [Theory]
    [InlineData("POSITION", -1)]
    [InlineData("POSITION", 2)]
    [InlineData("indices", -1)]
    [InlineData("indices", 2)]
    public void SelectedInput_InvalidAccessorReferenceRefusesBeforeModelDecode(string member, int index)
    {
        byte[] glb = NativeRenderAssetFixtures.Glb();
        int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var json = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject();
        var primitive = json["meshes"]![0]!["primitives"]![0]!;
        if (member == "POSITION") primitive["attributes"]![member] = index;
        else primitive[member] = index;
        byte[] malformed = NativeRenderAssetFixtures.Pack(json, glb.AsSpan(28 + length).ToArray());
        var source = new Source();
        var mesh = source.Add("mesh", malformed);
        var manifest = JsonNode.Parse("""
            {"payloadVersion":1,"assets":[{"id":"prop","meshResourceId":"mesh","sourceUnitsToMetres":1,
             "renderBounds":{"min":{"x":-10,"y":-10,"z":-10},"max":{"x":10,"y":10,"z":10}},
             "supportResourceIds":[],"materialResourceIds":[],"lodResourceIds":[],"lightResourceIds":[],
             "source":"invented.blend","license":"CC0","textured":true}],
             "resources":[{"kind":"Mesh","dependencies":[],"reference":{}}]}
            """)!;
        manifest["resources"]![0]!["reference"] = new JsonObject
        { ["id"] = mesh.Id, ["path"] = mesh.Path, ["sha256"] = mesh.Sha256, ["payloadVersion"] = 1 };
        var root = source.Add("root", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        var closure = MapAssetClosure.Load([root], source);
        MapAssetExtentProofSet proofs;
        if (member == "indices")
        {
            var validated = MapRenderExtentValidator.Validate(closure, ["prop"], new(new()));
            Assert.Equal(MapRenderAssetStatus.Complete, validated.Status);
            proofs = validated.Proofs!;
        }
        else
        {
            // Isolate selected-input admission from the earlier POSITION extent check, using the fixture's
            // independently known conservative bounds and the malformed resource's verified hash.
            var bounds = closure.GetAsset("prop").RenderBounds;
            proofs = new(closure.Hash, "synthetic-position-proof",
                [new("prop", 1, [new("mesh", mesh.Sha256, bounds, bounds)], null, null, 1)], 1024);
        }
        int entries = GltfDecodeAdmission.ModelDecodeEntries;
        var budget = new GltfDecodeBudget(new());
        var result = MapRenderAssets.LoadSelected(closure, ["prop"], proofs, budget);
        Assert.Equal(MapRenderAssetStatus.Invalid, result.Status);
        Assert.Null(result.Assets);
        Assert.Equal(entries, GltfDecodeAdmission.ModelDecodeEntries);
        Assert.Equal(0, budget.OwnedBytes);
        Assert.Equal(GltfDecodeStatus.Invalid, Assert.Throws<GltfDecodeRefusal>(() =>
            GltfDecodeAdmission.Measure(malformed, budget)).Status);
    }

    sealed class Source : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new();
        internal MapAssetRef Add(string id, byte[] bytes)
        {
            _bytes.Add(id, bytes);
            return new(id, "invented/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes[reference.Id];
    }
}
