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
            var missing = Assert.Throws<MapDocumentException>(() => read(Encoding.UTF8.GetBytes(root.ToJsonString())));
            Assert.Equal(MapDocumentRefusal.SchemaValueRequired, missing.Reason);
            Assert.Equal("$.resourceSchema", missing.FieldPath);
            Assert.Equal(schema, missing.ExpectedSchema);
            Assert.Equal(schema == MapTypedResourceSchema.Light ? "glow-light" : "deck-support", missing.ResourceId);
            read(bytes);
            root["resourceSchema"] = schema;
            var misplaced = Assert.Throws<MapDocumentException>(() => read(Encoding.UTF8.GetBytes(root.ToJsonString())));
            Assert.Equal(MapDocumentRefusal.SchemaValueRequired, misplaced.Reason);
            Assert.Equal("$.resourceSchema", misplaced.FieldPath);
            Assert.Equal(schema, misplaced.ExpectedSchema);
            Assert.Equal(missing.ResourceId, misplaced.ResourceId);
            read(bytes);
            // A root that is not an object is a shape refusal at the root, not a missing discriminant. The support
            // reader refuses it before the discriminant check, so only the shape and the resource are common.
            var array = Assert.Throws<MapDocumentException>(() => read("[]"u8.ToArray()));
            Assert.Equal(MapDocumentRefusal.SchemaObjectRequired, array.Reason);
            Assert.Equal("$", array.FieldPath);
            Assert.Equal(missing.ResourceId, array.ResourceId);
            read(bytes);
        }
    }

    // #1793. A missing light member is told apart from malformed JSON, as a missing support member already was. A
    // member missing inside a support file's surfaces is located in the file, as a null one is.
    [Theory]
    [InlineData("light", "missing-member", MapDocumentRefusal.SchemaMemberMissing, "$.radius")]
    [InlineData("light", "unknown-member", MapDocumentRefusal.SchemaMalformed, null)]
    [InlineData("light", "wrong-schema", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("light", "truncated", MapDocumentRefusal.SchemaMalformed, null)]
    [InlineData("light", "unknown-contains-id", MapDocumentRefusal.SchemaMalformed, null)]
    [InlineData("surface", "missing-member", MapDocumentRefusal.SchemaMemberMissing, "$.references")]
    [InlineData("surface", "missing-nested-member", MapDocumentRefusal.SchemaMemberMissing, "$.surfaces[0].semanticSha256")]
    [InlineData("surface", "unknown-member", MapDocumentRefusal.SchemaMemberUnknown, null)]
    [InlineData("surface", "wrong-schema", MapDocumentRefusal.SchemaValueRequired, null)]
    [InlineData("surface", "truncated", MapDocumentRefusal.SchemaMalformed, null)]
    public void MalformedTypedPayload_RefusesTheClosure(string id, string fault, MapDocumentRefusal reason, string? fieldPath)
    {
        byte[] valid = id == "light" ? MapLightFile.Write(PrefabFixtures.GlowLight()) : MapSupportResourceFile.Write(PrefabFixtures.DeckSupport());
        var root = JsonNode.Parse(valid)!.AsObject();
        switch (fault)
        {
            case "missing-member": root.Remove(id == "light" ? "radius" : "references"); break;
            case "missing-nested-member": root["surfaces"]![0]!.AsObject().Remove("semanticSha256"); break;
            case "unknown-member": root["extra"] = true; break;
            case "unknown-contains-id": root["'light'"] = true; break;
            case "wrong-schema": root["resourceSchema"] = id == "light" ? "kemap/support-resource" : "kemap/light-resource"; break;
        }
        string text = root.ToJsonString();
        if (fault == "truncated") text = text[..(text.Length / 2)];
        var (roots, source) = NativeAssetFixtures.WithResourceBytes(id, Encoding.UTF8.GetBytes(text));
        var error = Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
        Assert.Equal(reason, error.Reason);
        if (fieldPath is not null) Assert.Equal(fieldPath, error.FieldPath);
        Assert.Equal(id, error.ResourceId);
        var (controlRoots, controlSource) = NativeAssetFixtures.WithResourceBytes(id, valid);
        Assert.True(id == "light" ? MapAssetClosure.Load(controlRoots, controlSource).HasLight(id)
            : MapAssetClosure.Load(controlRoots, controlSource).HasSupport(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeafLightOwner_RequiresTheLocalPlacementToken(bool read)
    {
        var valid = PrefabFixtures.GlowLight();
        var invalid = valid with { Owner = new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "other") };
        MapDocumentException error;
        if (read)
        {
            var root = JsonNode.Parse(MapLightFile.Write(valid))!.AsObject();
            root["owner"]!["id"] = "other";
            error = Assert.Throws<MapDocumentException>(() => MapLightFile.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), "leaf"));
        }
        else error = Assert.Throws<MapDocumentException>(() => MapLightFile.Write(invalid));
        Assert.Equal(MapDocumentRefusal.PrefabLightOwnerInvalid, error.Reason);
        Assert.Equal(read ? "leaf" : valid.Id, error.ResourceId);
        Assert.Equal("$.owner", error.FieldPath);
        Assert.Equal(valid, MapLightFile.Read(MapLightFile.Write(valid), "leaf"));
        var unowned = valid with { Owner = null };
        Assert.Equal(unowned, MapLightFile.Read(MapLightFile.Write(unowned), "leaf"));
    }

    [Fact]
    public void A_light_refusal_carries_its_resource_when_the_failed_member_shares_its_name()
    {
        byte[] valid = MapLightFile.Write(PrefabFixtures.GlowLight());
        var root = JsonNode.Parse(valid)!.AsObject();
        root["radius"] = null;
        var error = Assert.Throws<MapDocumentException>(() => MapLightFile.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), "radius"));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, error.Reason);
        Assert.Equal("radius", error.ResourceId);
        Assert.Equal(PrefabFixtures.GlowLight(), MapLightFile.Read(valid, "radius"));
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
        Assert.Equal(MapDocumentRefusal.AssetTypedPayloadOpaque, error.Reason);
        Assert.Equal(id, error.ResourceId);
        Assert.Equal(role, error.SubjectKind);
        Assert.Equal("kemap/" + role + "-resource", error.ExpectedSchema);
        Assert.NotNull(PrefabFixtures.Effective(new(f.Document, f.Assets)));
    }
}
