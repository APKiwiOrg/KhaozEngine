using System;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The collider edit's refusals and edge cases, and the native mesh loader's self-containment rule.</summary>
public class NativeCollisionRefusalTests
{
    [Theory]
    [InlineData(false, "body.bin", "$.buffers[0].uri")]
    [InlineData(true, "albedo.png", "$.images[0].uri")]
    public void NativeLoader_RefusesExternalUris(bool image, string uri, string field)
    {
        var f = image ? NativeEditorAssetFixtures.ExternalImageMeshAsset() : NativeEditorAssetFixtures.ExternalBufferMeshAsset();
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure));
        Assert.Equal(MapDocumentRefusal.AssetGlbExternalUri, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Equal(f.Asset.MeshResourceId, error.ResourceId);
        Assert.Equal(uri, error.Uri);
        Assert.Equal(field, error.FieldPath);
        var control = NativeEditorAssetFixtures.DataBufferMeshAsset();
        Assert.NotEmpty(NativeMapAssetLoader.Load(control.Asset, control.Closure).Vertices);
    }

    [Fact]
    public void NativeLoader_RejectsADescriptorFromAnotherClosure()
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var other = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(other.Asset, f.Closure));
        Assert.Equal(MapDocumentRefusal.AssetDescriptorMismatch, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.NotEmpty(NativeMapAssetLoader.Load(f.Asset, f.Closure).Vertices);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("chunk")]
    [InlineData("json")]
    [InlineData("mesh")]
    public void NativeLoader_ReportsMalformedAndUnreadableMeshes(string fault)
    {
        byte[] bytes = fault == "header" ? new byte[19] : NativeEditorAssetFixtures.GlbWithJson(fault == "json" ? "{" : "{}");
        if (fault == "chunk") bytes[16] = 0;
        var f = NativeEditorAssetFixtures.MeshAsset("malformed", bytes);
        var error = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure));
        Assert.Equal(fault == "mesh" ? MapDocumentRefusal.AssetPayloadUnreadable : MapDocumentRefusal.SchemaMalformed, error.Reason);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Equal(f.Asset.MeshResourceId, error.ResourceId);
        var control = NativeEditorAssetFixtures.DataBufferMeshAsset();
        Assert.NotEmpty(NativeMapAssetLoader.Load(control.Asset, control.Closure).Vertices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeLoader_KeepsKnownExternalLoaderRefusals(bool nested)
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var original = new MapDocumentException(MapDocumentRefusal.SchemaMemberDuplicate, "known refusal")
        {
            ResourceId = "nested-resource", SourceName = "nested-source", FieldPath = "$.member",
            ResourceRoot = "nested-root", SupportedVersions = [1, 3], Uri = "nested-uri", ShapeType = typeof(MapDocument),
        };
        var error = NativeMapAssetLoader.MeshLoadFailure(nested ? new InvalidOperationException("loader", original) : original,
            f.Asset, f.Closure.GetResource(f.Asset.MeshResourceId));
        Assert.Equal(original.Reason, error.Reason);
        Assert.Equal(original.ResourceId, error.ResourceId);
        Assert.Equal(original.SourceName, error.SourceName);
        Assert.Equal(original.FieldPath, error.FieldPath);
        Assert.Equal(original.ResourceRoot, error.ResourceRoot);
        Assert.Equal(original.SupportedVersions, error.SupportedVersions);
        Assert.Equal(original.Uri, error.Uri);
        Assert.Equal(original.ShapeType, error.ShapeType);
        Assert.Equal(f.Asset.Id, error.AssetId);
        Assert.Same(original, error.InnerException);
        Assert.NotEmpty(NativeMapAssetLoader.Load(f.Asset, f.Closure).Vertices);
    }

    [Fact]
    public void NativeLoader_LoadsAMeshWhoseBufferIsAnEmbeddedDataUri()
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var mesh = NativeMapAssetLoader.Load(f.Asset, f.Closure);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(2f, NativeEditorAssetFixtures.MaxVertexY(mesh));
    }

    [Theory]
    [InlineData("tilted-wall", "compound boxes")]
    [InlineData("shared-a", "shared.collider")]
    [InlineData("nested-wall", "nested-wall")]
    [InlineData("split-wall", "kit")]
    [InlineData("rock-mesh", "compound boxes")]
    public void UnsupportedColliderOrManifest_RefusesHeightEdits(string assetId, string diagnostic)
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();

        var error = Assert.Throws<MapDocumentException>(() => f.Service.SetHeights(assetId, 0f, 1f));

        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        Assert.Contains(assetId, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.False(f.Session.IsDirty);
    }

    [Fact]
    public void LoneBox_BecomesACompoundAtTheRequestedHeights()
    {
        using var f = new NativeCollisionToolFixture();
        var edit = f.Service.SetHeights("lone-box", 0.2f, 1.7f, dryRun: false);
        Assert.True(edit.Applied);
        var m = f.Service.Measure("lone-1");
        Assert.Equal(edit.AfterSha256, m.ColliderSha256);
        Assert.Equal(0.2f, m.EffectiveBottom, 5);
        Assert.Equal(1.7f, m.EffectiveTop, 5);
    }

    [Fact]
    public void UnchangedEdit_AppliesNothing()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] before = f.ReadAssetDirectoryDigest();
        var edit = f.Service.SetHeights("wall-variant", 0f, 2.5f, dryRun: false);
        Assert.False(edit.Applied);
        Assert.Equal(edit.BeforeSha256, edit.AfterSha256);
        Assert.Equal(MapNativeInvalidation.None, edit.Effects.Invalidates);
        Assert.Equal(before, f.ReadAssetDirectoryDigest());
        Assert.False(f.Session.IsDirty);
    }

    [Fact]
    public void Writer_RefusesAnOccupiedAddressWithoutOverwriting()
    {
        using var f = new NativeCollisionToolFixture();
        byte[] bytes = { 1, 2, 3, 4 };
        string digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string path = Path.Combine(f.Root, MapAssetFileWriter.ContentDirectory, digest + ".coll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 9, 9 });
        var writer = new MapAssetFileWriter(f.Root);
        Assert.Throws<MapDocumentException>(() => writer.WriteResource(bytes, "coll"));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
    }
}
