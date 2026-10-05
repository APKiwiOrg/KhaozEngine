using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeAssetClosureTests
{
    [Fact]
    public void NativeClosure_RejectsMissingCyclicStaleAndFutureBeforePublish()
    {
        var valid = NativeAssetFixtures.Valid();
        var a = MapAssetClosure.Load(valid.Roots, valid.Source);
        var b = MapAssetClosure.Load(valid.Roots.Reverse().ToArray(), valid.Source);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Matches("^[0-9a-f]{64}$", a.Hash);
        foreach (var bad in new[] { NativeAssetFixtures.MissingLod(), NativeAssetFixtures.Cycle(),
            NativeAssetFixtures.StaleDigest(), NativeAssetFixtures.FuturePayload(), NativeAssetFixtures.MissingMaterial(),
            NativeAssetFixtures.DuplicateId() })
            Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(bad.Roots, bad.Source));
    }

    [Fact]
    public void NativeClosure_DescriptorFieldsAndReferencesSurvive()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        var closure = MapAssetClosure.Load(roots, source);
        var asset = Assert.Single(closure.Assets);
        Assert.Same(asset, closure.GetAsset("tree"));
        Assert.Equal("mesh", asset.MeshResourceId);
        Assert.Equal("collider", asset.CollisionResourceId);
        Assert.Equal("selection", asset.SelectionResourceId);
        Assert.Equal(new[] { "surface" }, asset.SupportResourceIds);
        Assert.Equal(new[] { "material" }, asset.MaterialResourceIds);
        Assert.Equal(new[] { "lod" }, asset.LodResourceIds);
        Assert.Equal(new[] { "light" }, asset.LightResourceIds);
        Assert.Equal(0.01f, asset.SourceUnitsToMetres);
        Assert.Equal(new Vector3(-2, 0.5f, -3), asset.RenderBounds.Min);
        Assert.Equal(new Vector3(2, 8, 3), asset.RenderBounds.Max);
        Assert.Equal(new Vector3(1, 7, 1), asset.LodBounds!.Value.Max);
        Assert.Equal(new Vector3(4, 9, 4), asset.LightBounds!.Value.Max);
        Assert.Equal("authored/tree.blend", asset.Source);
        Assert.Equal("CC0", asset.License);
        Assert.Equal("trees", asset.Category);
        Assert.True(asset.Textured);
        Assert.Equal("assets/mesh", closure.GetResource(asset.MeshResourceId).Reference.Path);
        Assert.Equal(new[] { "material", "lod" }, closure.GetResource("mesh").Dependencies);
        Assert.Equal(MapResourceKind.Mesh, closure.GetResource("lod-mesh").Kind);
        Assert.Equal(MapResourceKind.Manifest, closure.GetResource("root-b").Kind);
        Assert.Throws<MapDocumentException>(() => closure.GetAsset("absent"));
        Assert.Throws<MapDocumentException>(() => closure.GetResource("absent"));
        Assert.DoesNotContain(typeof(MapAssetClosure).Assembly.GetReferencedAssemblies(),
            a => a.Name!.Contains("Render3D") || a.Name.Contains("Gpu") || a.Name.Contains("TileWorld"));
    }

    [Fact]
    public void NativeClosure_SourceAndReadApisCannotMutatePublishedState()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        var closure = MapAssetClosure.Load(roots, source);
        var resource = closure.GetResource("mesh");
        byte[] before = resource.Bytes.ToArray();
        source.Corrupt("mesh");
        Assert.Equal(before, resource.Bytes.ToArray());
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
        if (MemoryMarshal.TryGetArray(resource.Bytes, out ArraySegment<byte> exposed))
            exposed.Array![exposed.Offset] ^= 0xff;
        Assert.Equal(before, resource.Bytes.ToArray());
        Assert.False(closure.Assets is MapResolvedAsset[]);
        Assert.Throws<NotSupportedException>(() => ((IList<MapResolvedAsset>)closure.Assets).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)resource.Dependencies)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)closure.GetAsset("tree").MaterialResourceIds).Clear());
    }

    [Fact]
    public void NativeClosure_SnapshotIsTakenBeforeLaterSourceReads()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        byte[] before = source.Read(new MapAssetRef("mesh", "", "", 1)).ToArray();
        var closure = MapAssetClosure.Load(roots, new MutatingSource(source));
        Assert.Equal(before, closure.GetResource("mesh").Bytes.ToArray());
    }

    sealed class MutatingSource(NativeMemoryAssetSource source) : IMapAssetSource
    {
        bool _meshRead;
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            if (_meshRead) { source.Corrupt("mesh"); _meshRead = false; }
            if (reference.Id == "mesh") _meshRead = true;
            return source.Read(reference);
        }
    }

    [Theory]
    [InlineData("payloadVersion")]
    [InlineData("assets")]
    [InlineData("resources")]
    public void NativeManifest_RequiredRootMembersCannotDefault(string field) => Reject(root => root.Remove(field));

    [Theory]
    [InlineData("id")]
    [InlineData("meshResourceId")]
    [InlineData("sourceUnitsToMetres")]
    [InlineData("renderBounds")]
    [InlineData("source")]
    [InlineData("license")]
    [InlineData("textured")]
    [InlineData("supportResourceIds")]
    [InlineData("materialResourceIds")]
    [InlineData("lodResourceIds")]
    [InlineData("lightResourceIds")]
    public void NativeManifest_RequiredDescriptorMembersCannotDefault(string field) =>
        Reject(root => root["assets"]![0]!.AsObject().Remove(field));

    [Theory]
    [InlineData("reference")]
    [InlineData("kind")]
    [InlineData("dependencies")]
    public void NativeManifest_RequiredResourceMembersCannotDefault(string field) =>
        Reject(root => root["resources"]![0]!.AsObject().Remove(field));

    [Theory]
    [InlineData("id")]
    [InlineData("path")]
    [InlineData("sha256")]
    [InlineData("payloadVersion")]
    public void NativeManifest_RequiredReferenceMembersCannotDefault(string field) =>
        Reject(root => root["resources"]![0]!["reference"]!.AsObject().Remove(field));

    [Theory]
    [InlineData("unknown")]
    [InlineData("null-assets")]
    [InlineData("null-asset")]
    [InlineData("null-resource")]
    [InlineData("null-list")]
    [InlineData("null-dependency")]
    [InlineData("wrong-kind")]
    [InlineData("unknown-kind")]
    [InlineData("numeric-kind")]
    [InlineData("prefab")]
    [InlineData("zero-units")]
    [InlineData("negative-units")]
    [InlineData("overflow-units")]
    [InlineData("inverted-bounds")]
    [InlineData("missing-bound")]
    [InlineData("unknown-bound")]
    [InlineData("unknown-reference")]
    [InlineData("duplicate-asset")]
    [InlineData("missing-dependency")]
    [InlineData("blank-provenance")]
    [InlineData("uppercase-digest")]
    [InlineData("future-reference")]
    public void NativeManifest_InvalidPayloadRefuses(string fault) => Reject(root =>
    {
        var asset = root["assets"]![0]!;
        var resource = root["resources"]![0]!;
        switch (fault)
        {
            case "unknown": asset["typo"] = 1; break;
            case "null-assets": root["assets"] = null; break;
            case "null-asset": root["assets"]![0] = null; break;
            case "null-resource": root["resources"]![0] = null; break;
            case "null-list": asset["supportResourceIds"] = null; break;
            case "null-dependency": resource["dependencies"]![0] = null; break;
            case "wrong-kind": asset["meshResourceId"] = "material"; break;
            case "unknown-kind": resource["kind"] = "FutureKind"; break;
            case "numeric-kind": resource["kind"] = 1; break;
            case "prefab": resource["kind"] = "Prefab"; break;
            case "zero-units": asset["sourceUnitsToMetres"] = 0; break;
            case "negative-units": asset["sourceUnitsToMetres"] = -1; break;
            case "overflow-units": asset["sourceUnitsToMetres"] = JsonNode.Parse("1e100"); break;
            case "inverted-bounds": asset["renderBounds"]!["min"]!["x"] = 10; break;
            case "missing-bound": asset["renderBounds"]!.AsObject().Remove("min"); break;
            case "unknown-bound": asset["renderBounds"]!["extra"] = 1; break;
            case "unknown-reference": resource["reference"]!["extra"] = 1; break;
            case "duplicate-asset": root["assets"]!.AsArray().Add(asset.DeepClone()); break;
            case "missing-dependency": resource["dependencies"]![0] = "missing"; break;
            case "blank-provenance": asset["license"] = " "; break;
            case "uppercase-digest": resource["reference"]!["sha256"] = resource["reference"]!["sha256"]!.GetValue<string>().ToUpperInvariant(); break;
            case "future-reference": resource["reference"]!["payloadVersion"] = 2; break;
        }
    });

    [Fact]
    public void NativeManifest_DuplicateJsonPropertiesRefuse()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        string json = System.Text.Encoding.UTF8.GetString(source.Read(roots[0]).Span);
        var replacement = source.Add("root-a", json.Replace("\"textured\":true", "\"textured\":false,\"textured\":true", StringComparison.Ordinal));
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(new[] { replacement, roots[1] }, source));
    }

    [Fact]
    public void NativeClosure_OrderedListsAffectHashAndPreserveOrder()
    {
        var first = NativeAssetFixtures.Change(root => root["assets"]![0]!["materialResourceIds"] = new JsonArray("material", "lod-mesh"));
        // Use a second real material, so the order assertion cannot pass by failing kind validation.
        var node = JsonNode.Parse(first.Source.Read(first.Roots[1]).Span)!;
        node["resources"]![1]!["kind"] = "Material";
        var rootB = first.Source.Add("root-b", node.ToJsonString());
        var roots = new[] { first.Roots[0], rootB };
        var a = MapAssetClosure.Load(roots, first.Source);
        var rootA = JsonNode.Parse(first.Source.Read(roots[0]).Span)!;
        rootA["assets"]![0]!["materialResourceIds"] = new JsonArray("lod-mesh", "material");
        var changed = first.Source.Add("root-a", rootA.ToJsonString());
        var b = MapAssetClosure.Load(new[] { changed, rootB }, first.Source);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(new[] { "lod-mesh", "material" }, b.GetAsset("tree").MaterialResourceIds);
    }

    [Fact]
    public void NativeClosure_PublishesAssetsInOrdinalOrderAndCopiesRootList()
    {
        var (roots, source) = NativeAssetFixtures.Change(root =>
        {
            var other = NativeAssetFixtures.Asset();
            other["id"] = "A-tree";
            root["assets"]!.AsArray().Add(other);
        });
        var mutableRoots = roots.ToList();
        var closure = MapAssetClosure.Load(mutableRoots, source);
        mutableRoots.Clear();
        Assert.Equal(new[] { "A-tree", "tree" }, closure.Assets.Select(x => x.Id));
        Assert.Equal(MapResourceKind.Manifest, closure.GetResource("root-a").Kind);
    }

    [Fact]
    public void NativeClosure_ResourceContentAndReferenceChangesAffectIdentity()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        var before = MapAssetClosure.Load(roots, source);
        var reference = source.Add("mesh", "changed mesh bytes");
        var root = JsonNode.Parse(source.Read(roots[0]).Span)!;
        root["resources"]![0]!["reference"]!["sha256"] = reference.Sha256;
        root["resources"]![0]!["reference"]!["path"] = "other/mesh";
        var changed = source.Add("root-a", root.ToJsonString());
        var after = MapAssetClosure.Load(new[] { changed, roots[1] }, source);
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.Equal("other/mesh", after.GetResource("mesh").Reference.Path);
        Assert.Equal("changed mesh bytes", System.Text.Encoding.UTF8.GetString(after.GetResource("mesh").Bytes.Span));
    }

    [Fact]
    public void NativeClosure_DescriptorReferencesParticipateInCrossManifestCycles()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        var rootA = JsonNode.Parse(source.Read(roots[0]).Span)!;
        var rootB = JsonNode.Parse(source.Read(roots[1]).Span)!;
        rootA["resources"]![0]!["dependencies"]!.AsArray().Add("root-a");
        foreach (var resource in rootA["resources"]!.AsArray())
            rootB["resources"]!.AsArray().Add(resource!.DeepClone());
        rootA["resources"] = new JsonArray();
        var a = source.Add("root-a", rootA.ToJsonString());
        var b = source.Add("root-b", rootB.ToJsonString());
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(new[] { a, b }, source));
    }

    [Fact]
    public void NativeClosure_NestedManifestIsLoadedAndDuplicateRootsRefuse()
    {
        var (roots, source) = NativeAssetFixtures.Valid();
        var root = JsonNode.Parse(source.Read(roots[0]).Span)!;
        root["resources"]!.AsArray().Add(NativeAssetFixtures.Resource(roots[1], "Manifest"));
        var parent = source.Add("root-a", root.ToJsonString());
        var closure = MapAssetClosure.Load(new[] { parent }, source);
        Assert.Equal(MapResourceKind.Material, closure.GetResource("material").Kind);
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(new[] { parent, parent }, source));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"x\":1,\"y\":2}")]
    [InlineData("{\"x\":1,\"y\":2,\"z\":3,\"w\":4}")]
    [InlineData("{\"x\":1,\"x\":2,\"y\":2,\"z\":3}")]
    [InlineData("{\"X\":1,\"y\":2,\"z\":3}")]
    [InlineData("{\"x\":1e100,\"y\":2,\"z\":3}")]
    [InlineData("{\"x\":\"1\",\"y\":2,\"z\":3}")]
    public void NativeVectorPayload_RefusesInvalidComponents(string json)
    {
        var options = Options();
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector3>(json, options));
    }

    [Fact]
    public void NativeVectorPayload_RoundTripsNonzeroWithoutChangingOtherPrimitives()
    {
        var options = Options();
        var v3 = new Vector3(0.23f, 1.5f, -0.17f);
        var v2 = new Vector2(0.23f, -0.17f);
        Assert.Equal(v3, JsonSerializer.Deserialize<Vector3>(JsonSerializer.Serialize(v3, options), options));
        Assert.Equal(v2, JsonSerializer.Deserialize<Vector2>(JsonSerializer.Serialize(v2, options), options));
        Assert.Equal("{\"x\":0.23,\"y\":-0.17}", JsonSerializer.Serialize(v2, options));
        Assert.Equal("9007199254740993", JsonSerializer.Serialize(9007199254740993L, options));
        Assert.False(options.IncludeFields);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new Vector3(float.NaN, 0, 0), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new Vector2(0, float.PositiveInfinity), options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector2>("{\"x\":1}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Vector2>("{\"x\":1,\"y\":2,\"z\":3}", options));
    }

    [Fact]
    public void NativeFileSource_UsesExplicitAbsoluteRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "native-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "assets", "mesh"), new byte[] { 1, 2, 3 });
            var source = new MapDirectoryAssetSource(root);
            Assert.Equal(new byte[] { 1, 2, 3 }, source.Read(new MapAssetRef("mesh", "assets/mesh", "", 1)).ToArray());
            Assert.Throws<ArgumentException>(() => new MapDirectoryAssetSource("relative"));
            Assert.Throws<MapDocumentException>(() => source.Read(new MapAssetRef("mesh", "missing", "", 1)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NativeVectors_DocumentSerializerUsesConvertersForRegisteredFeaturePayloads()
    {
        var registry = MapDocRegistry.CreateDefault();
        registry.RegisterFeature("vector-probe", typeof(VectorFeature), _ => throw new InvalidOperationException("Builder is not used by document roundtrip"));
        var doc = MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
        doc.Terrain.Features.Add(new VectorFeature { Offset = new Vector3(2, 3, 4), Direction = new Vector2(-1, 5) });
        string json = MapDocumentFile.SaveText(doc, registry);
        var loaded = MapDocumentFile.LoadText(json, new MapDocumentLoadOptions { Registry = registry });
        var feature = Assert.IsType<VectorFeature>(Assert.Single(loaded.Terrain.Features));
        Assert.Equal(new Vector3(2, 3, 4), feature.Offset);
        Assert.Equal(new Vector2(-1, 5), feature.Direction);
    }

    public sealed class VectorFeature : MapFeature
    {
        public override string Type => "vector-probe";
        public Vector3 Offset { get; set; }
        public Vector2 Direction { get; set; }
    }

    [Fact]
    public void NativeResolvedResource_CopiesConstructorInputs()
    {
        byte[] bytes = { 1, 2, 3 };
        var dependencies = new List<string> { "original" };
        var resource = new MapResolvedResource(new MapAssetRef("mesh", "mesh", "digest", 1), MapResourceKind.Mesh, dependencies, bytes);
        dependencies[0] = "changed";
        bytes[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, resource.Bytes.ToArray());
        Assert.Equal(new[] { "original" }, resource.Dependencies);
    }

    static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        MapNativeJson.Configure(options);
        return options;
    }

    static void Reject(Action<JsonObject> change)
    {
        var (roots, source) = NativeAssetFixtures.Change(change);
        Assert.Throws<MapDocumentException>(() => MapAssetClosure.Load(roots, source));
    }
}
