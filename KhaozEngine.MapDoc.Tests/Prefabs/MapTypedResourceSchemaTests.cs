using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

// F-R3. R5 typed Light and Support payloads opt in with an in-band discriminant written first. Every other byte
// sequence, JSON or not, stays an opaque released resource. Typed consumers refuse an opaque resource clearly.
public sealed class MapTypedResourceSchemaTests
{
    [Theory]
    [InlineData("light", """{"legacy":true}""")]
    [InlineData("surface", """{"legacy":true}""")]
    [InlineData("light", """ {"payloadVersion":1,"id":"glow"}""")]
    [InlineData("surface", """{"payloadVersion":1,"surfaces":[],"patches":[],"references":[]}""")]
    [InlineData("surface", """{"payloadVersion":1,"resourceSchema":"kemap/support-resource"}""")]
    public void LegacyJsonObject_StaysOpaqueOnTheReleasedClosurePath(string id, string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var (roots, source) = NativeAssetFixtures.WithResourceBytes(id, bytes);
        var closure = MapAssetClosure.Load(roots, source);
        Assert.Equal(bytes, closure.GetResource(id).Bytes.ToArray());
        Assert.False(closure.HasLight(id) || closure.HasSupport(id));
        // The identity hash follows the verified graph and digests only, exactly as for any other opaque bytes.
        Assert.Equal(closure.Hash, MapAssetClosure.Load(roots, source).Hash);
        var (baseRoots, baseSource) = NativeAssetFixtures.Valid();
        Assert.NotEqual(MapAssetClosure.Load(baseRoots, baseSource).Hash, closure.Hash);
        var (sameRoots, sameSource) = NativeAssetFixtures.WithResourceBytes(id, Encoding.UTF8.GetBytes(json));
        Assert.Equal(closure.Hash, MapAssetClosure.Load(sameRoots, sameSource).Hash);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("surface")]
    public void TaggedPayload_DecodesOnceWithTheSameHashRule(string id)
    {
        byte[] bytes = id == "light" ? MapLightFile.Write(PrefabFixtures.GlowLight()) : MapSupportResourceFile.Write(PrefabFixtures.DeckSupport());
        var (roots, source) = NativeAssetFixtures.WithResourceBytes(id, bytes);
        var closure = MapAssetClosure.Load(roots, source);
        Assert.True(id == "light" ? closure.HasLight(id) : closure.HasSupport(id));
        if (id == "light") Assert.Equal(PrefabFixtures.GlowLight(), closure.BorrowLight(id));
        Assert.Equal(closure.Hash, MapAssetClosure.Load(roots, source).Hash);
        var (opaqueRoots, opaqueSource) = NativeAssetFixtures.WithResourceBytes(id, Encoding.UTF8.GetBytes("""{"legacy":true}"""));
        Assert.NotEqual(MapAssetClosure.Load(opaqueRoots, opaqueSource).Hash, closure.Hash);
    }

    [Fact]
    public void Writers_EmitTheDiscriminantFirstAndDecodersRequireIt()
    {
        byte[] light = MapLightFile.Write(PrefabFixtures.GlowLight());
        Assert.StartsWith("""{"resourceSchema":"kemap/light-resource",""", Encoding.UTF8.GetString(light));
        Assert.Equal(PrefabFixtures.GlowLight(), MapLightFile.Read(light, "glow-light"));
        byte[] support = MapSupportResourceFile.Write(PrefabFixtures.DeckSupport());
        Assert.StartsWith("""{"resourceSchema":"kemap/support-resource",""", Encoding.UTF8.GetString(support));
        Assert.Equal(support, MapSupportResourceFile.Write(MapSupportResourceFile.Read(support, "deck-support")));
        foreach (var (bytes, read) in new (byte[], Action<byte[]>)[]
        {
            (light, b => MapLightFile.Read(b, "glow-light")),
            (support, b => MapSupportResourceFile.Read(b, "deck-support")),
        })
        {
            var root = JsonNode.Parse(bytes)!.AsObject();
            string schema = root["resourceSchema"]!.GetValue<string>();
            root.Remove("resourceSchema");
            Assert.Contains("resourceSchema", Assert.Throws<MapDocumentException>(() => read(Encoding.UTF8.GetBytes(root.ToJsonString()))).Message);
            root["resourceSchema"] = schema;
            Assert.Contains("resourceSchema", Assert.Throws<MapDocumentException>(() => read(Encoding.UTF8.GetBytes(root.ToJsonString()))).Message);
        }
    }

    [Theory]
    [InlineData("light", "missing-member")]
    [InlineData("light", "unknown-member")]
    [InlineData("light", "wrong-schema")]
    [InlineData("light", "truncated")]
    [InlineData("surface", "missing-member")]
    [InlineData("surface", "unknown-member")]
    [InlineData("surface", "wrong-schema")]
    [InlineData("surface", "truncated")]
    public void MalformedTypedPayload_RefusesTheClosure(string id, string fault)
    {
        byte[] valid = id == "light" ? MapLightFile.Write(PrefabFixtures.GlowLight()) : MapSupportResourceFile.Write(PrefabFixtures.DeckSupport());
        var root = JsonNode.Parse(valid)!.AsObject();
        switch (fault)
        {
            case "missing-member": root.Remove(id == "light" ? "radius" : "references"); break;
            case "unknown-member": root["extra"] = true; break;
            case "wrong-schema": root["resourceSchema"] = id == "light" ? "kemap/support-resource" : "kemap/light-resource"; break;
        }
        string text = root.ToJsonString();
        if (fault == "truncated") text = text[..(text.Length / 2)];
        var (roots, source) = NativeAssetFixtures.WithResourceBytes(id, Encoding.UTF8.GetBytes(text));
        var error = Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
        Assert.Contains("'" + id + "'", error.Message);
    }

    [Theory]
    [InlineData("glow-light", "light")]
    [InlineData("deck-support", "support")]
    public void PrefabLeaf_RefusesAnOpaqueTypedResourceClearly(string id, string role)
    {
        var f = PrefabFixtures.Create("attached");
        var legacy = Encoding.UTF8.GetBytes("""{"legacy":true}""");
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            var assets = PrefabFixtures.PublishPrefab(f.Assets, f.Assets.GetPrefab("hut"), new Dictionary<string, byte[]> { [id] = legacy });
            return PrefabFixtures.Effective(new(f.Document, assets));
        });
        Assert.Contains("'" + id + "'", error.Message);
        Assert.Contains("opaque", error.Message);
        Assert.Contains("kemap/" + role + "-resource", error.Message);
    }
}
