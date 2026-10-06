using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
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
        Assert.Equal(Path.GetFullPath(NativeLifecycleFixture.MeshRelativePath, f.ResourceRoot), adapted.File);
        Assert.Equal(Path.GetFullPath(NativeLifecycleFixture.LodRelativePath, f.ResourceRoot), adapted.LodFile);
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
        Assert.Equal(Path.GetFullPath(NativeLifecycleFixture.MeshRelativePath, other),
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

    // Absolute fixture references on Windows carry backslashes and a quote is the other character JSON must
    // escape. The fixture manifest must hand both references to the closure exactly. The source is in memory,
    // so no platform has to create a file with these names.
    [Fact]
    public void Fixture_ManifestCarriesBackslashAndQuotePathsExactly()
    {
        var source = new PathKeyedSource();
        MapAssetRef mesh = source.Add("prop.mesh", @"C:\Users\runner\kit\meshes\prop-body.glb", "mesh-v1");
        MapAssetRef lod = source.Add("prop.lod0", @"\\server\share\kit\say ""far"".glb", "lod-v1");
        MapAssetRef root = source.Add("kit", @"C:\Users\runner\kit\props.manifest.json",
            NativeLifecycleFixture.Manifest(mesh, lod));

        var closure = MapAssetClosure.Load(new[] { root }, source);

        Assert.Equal(mesh, closure.GetResource("prop.mesh").Reference);
        Assert.Equal(lod, closure.GetResource("prop.lod0").Reference);
        Assert.Equal(MapResourceKind.Mesh, closure.GetResource("prop.mesh").Kind);
        Assert.Equal(MapResourceKind.Lod, closure.GetResource("prop.lod0").Kind);
        Assert.Empty(closure.GetResource("prop.mesh").Dependencies);
        Assert.Empty(closure.GetResource("prop.lod0").Dependencies);
        Assert.Equal("prop.mesh", closure.GetAsset("prop").MeshResourceId);
        Assert.Equal(new[] { "prop.lod0" }, closure.GetAsset("prop").LodResourceIds);
        Assert.Equal(new[] { root.Path, mesh.Path, lod.Path }.Order(StringComparer.Ordinal),
            source.Reads.Order(StringComparer.Ordinal));
    }

    // Serves bytes by exact reference path, so a path altered on its way through the manifest cannot be read.
    sealed class PathKeyedSource : IMapAssetSource
    {
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = new();

        public MapAssetRef Add(string id, string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _bytes.Add(path, bytes);
            return new(id, path, NativeLifecycleFixture.Digest(bytes), 1);
        }

        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            Reads.Add(reference.Path);
            return _bytes[reference.Path];
        }
    }
}
