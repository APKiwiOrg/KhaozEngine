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
    [Fact]
    public void NativeLoader_RefusesAMeshThatNamesAnExternalBuffer()
    {
        var f = NativeEditorAssetFixtures.ExternalBufferMeshAsset();
        string message = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure)).Message;
        Assert.Contains("'external'", message);
        Assert.Contains("external buffer URI 'body.bin'", message);
    }

    [Fact]
    public void NativeLoader_RefusesAMeshThatNamesAnExternalImage()
    {
        var f = NativeEditorAssetFixtures.ExternalImageMeshAsset();
        string message = Assert.Throws<MapDocumentException>(() => NativeMapAssetLoader.Load(f.Asset, f.Closure)).Message;
        Assert.Contains("'external-image'", message);
        Assert.Contains("external image URI 'albedo.png'", message);
    }

    [Fact]
    public void NativeLoader_LoadsAMeshWhoseBufferIsAnEmbeddedDataUri()
    {
        var f = NativeEditorAssetFixtures.DataBufferMeshAsset();
        var mesh = NativeMapAssetLoader.Load(f.Asset, f.Closure);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(2f, NativeEditorAssetFixtures.MaxVertexY(mesh));
    }

    [Fact]
    public void SlightlyTiltedBox_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("upright", Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("tilted-wall", 0f, 1f)).Message);
    }

    [Fact]
    public void SharedCollider_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("shares collider resource 'shared.collider'",
            Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("shared-a", 0f, 1f)).Message);
    }

    [Fact]
    public void NestedOrUndeclaredManifest_RefusesHeightEdits()
    {
        using var f = new NativeCollisionToolFixture();
        Assert.Contains("not declared directly in a root manifest",
            Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("nested-wall", 0f, 1f)).Message);
        Assert.Contains("is not declared in root manifest 'kit'",
            Assert.Throws<MapDocumentException>(() => f.Service.SetHeights("split-wall", 0f, 1f)).Message);
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
        Assert.Contains("already holds other content",
            Assert.Throws<MapDocumentException>(() => writer.WriteResource(bytes, "coll")).Message);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(path));
    }
}
