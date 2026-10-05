using System;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeManifestAdapterTests
{
    [Fact]
    public void NativeLifecycle_BadOpenAndAdapterPreservePublishedSession()
    {
        using var f = new NativeLifecycleFixture();
        string id = f.Session.Summary().Id;
        byte[] before = f.ReadSavedBytes();
        Assert.Throws<MapDocumentException>(() => f.Session.Open(f.BadPath));
        Assert.Equal(id, f.Session.Summary().Id);
        Assert.Equal(before, f.ReadSavedBytes());
        var adapted = MapAssetManifestAdapter.ToAssetEntry(f.Closure, f.Asset.Id, f.ResourceRoot);
        Assert.Equal(f.Asset.Id, adapted.Id);
        Assert.Equal(f.Asset.Source, adapted.Source);
        Assert.Equal(f.Asset.License, adapted.License);
        Assert.True(Path.IsPathRooted(adapted.File));
        Assert.True(File.Exists(adapted.File));
        Assert.Equal((f.Asset.RenderBounds.Max.Y - f.Asset.RenderBounds.Min.Y)
            * f.Asset.SourceUnitsToMetres, adapted.HeightMeters);
        Assert.DoesNotContain(typeof(MapResolver).Assembly.GetReferencedAssemblies(),
            a => a.Name!.Contains("Render3D") || a.Name.Contains("Gpu")
                || a.Name.Contains("MapEditor") || a.Name.Contains("TileWorld"));
    }

    [Fact]
    public void Adapter_ResolvesResourceIdsToVerifiedPathsNotAssetIds()
    {
        using var f = new NativeLifecycleFixture();
        var adapted = MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", f.ResourceRoot);
        Assert.Equal(Path.Combine(f.ResourceRoot, NativeLifecycleFixture.MeshRelativePath), adapted.File);
        Assert.Equal(Path.Combine(f.ResourceRoot, NativeLifecycleFixture.LodRelativePath), adapted.LodFile);
        Assert.Equal(2f, adapted.HeightMeters);
        Assert.True(adapted.Textured);
        Assert.Equal("fence", adapted.Category);
        Assert.Null(adapted.Collider);
        Assert.Null(adapted.CollisionShape);
        Assert.Null(adapted.CollisionProxy);
        Assert.Null(adapted.Heightmap);
        Assert.False(adapted.Surface);
        Assert.Throws<MapDocumentException>(() =>
            MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop.mesh", f.ResourceRoot));
    }

    [Fact]
    public void Adapter_RequiresAnExplicitAbsoluteRoot()
    {
        using var f = new NativeLifecycleFixture();
        Assert.Throws<ArgumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", "kit"));
        Assert.Throws<ArgumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", ""));
        Assert.Throws<ArgumentNullException>(() => MapAssetManifestAdapter.ToAssetEntry(null!, "prop", f.ResourceRoot));
    }

    [Fact]
    public void Adapter_RejectsStaleOrMissingMeshAndLodFiles()
    {
        using var f = new NativeLifecycleFixture();
        string other = Path.Combine(f.ResourceRoot, "other-root");
        Directory.CreateDirectory(other);
        Assert.Throws<MapDocumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", other));
        f.CopyResourcesTo(other);
        Assert.Equal(Path.Combine(other, NativeLifecycleFixture.MeshRelativePath),
            MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", other).File);

        f.CorruptLod();
        Assert.Throws<MapDocumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", f.ResourceRoot));
        string otherMesh = Path.Combine(other, NativeLifecycleFixture.MeshRelativePath);
        File.WriteAllText(otherMesh, "mesh-v2");
        Assert.Throws<MapDocumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", other));
        File.Delete(otherMesh);
        Assert.Throws<MapDocumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", other));
        File.Delete(Path.Combine(other, NativeLifecycleFixture.LodRelativePath));
        File.Delete(Path.Combine(f.ResourceRoot, NativeLifecycleFixture.LodRelativePath));
        Assert.Throws<MapDocumentException>(() => MapAssetManifestAdapter.ToAssetEntry(f.Closure, "prop", f.ResourceRoot));
    }
}
