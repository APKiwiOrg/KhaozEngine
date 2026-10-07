using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class SurfaceStorageNamespaceTests
{
    [Fact]
    public void SurfaceStorage_AuthoredSurfacesResourcesSurviveAndReservedPathsRefuse()
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            string mesh = Path.Combine(dir, "surfaces", "rock.mesh");
            Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
            byte[] authored = { 1, 2, 3, 4 };
            File.WriteAllBytes(mesh, authored);
            for (int i = 0; i < 2; i++)
            {
                MapDocument d = MapDocumentFile.LoadTiled(dir);
                d.Surfaces.Patches[new("ridge", 0, 0)].Heights[0] += 1;
                MapDocumentFile.SaveTiled(d, dir);
            }
            Assert.Equal(authored, File.ReadAllBytes(mesh));
            var guarded = new MapStorageGuardedAssetSource(dir, MapDocumentForm.Tiled);
            Assert.Throws<MapDocumentException>(() => guarded.Read(new MapAssetRef("x", "tiles/surfaces/p/aa/x.json", "", 1)));
            Assert.Throws<MapDocumentException>(() => guarded.Read(new MapAssetRef("x", "tiles/surfaces/i/x.json", "", 1)));
            Assert.Throws<MapDocumentException>(() => guarded.Read(new MapAssetRef("x", "tiles/surfaces/d/x.json", "", 1)));
            Assert.True(MapDocumentStorage.IsReserved(dir, MapDocumentForm.Tiled,
                Path.Combine(dir, "tiles", "surfaces", "d", "x.json")));
            Assert.True(MapDocumentStorage.IsReserved(dir, MapDocumentForm.Tiled,
                Path.Combine(dir, "surfaces", "..", "tiles", "surfaces", "i", "x.json")));
            Assert.False(MapDocumentStorage.IsReserved(dir, MapDocumentForm.Tiled, mesh));
            Assert.Equal(authored, guarded.Read(new MapAssetRef("rock", "surfaces/rock.mesh", "", 1)).ToArray());

            Assert.NotEmpty(doc.NativeAssets);
            Assert.All(doc.NativeAssets, asset => Assert.False(File.Exists(Path.GetFullPath(asset.Path, dir))));
            Assert.Throws<MapDocumentException>(() => new MapDirectoryAssetSource(dir).Read(doc.NativeAssets[0]));
            byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
            var files = SurfaceStorageFixtures.SurfaceFiles(dir);
            MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
            MapPatchRead patch = source.ReadPatch(new("ground", 0, 0));
            Assert.Equal((MapPatchStatus.Present, 1000), (patch.Status, patch.Patch!.Heights[0]));
            Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
            Assert.Equal(authored, File.ReadAllBytes(mesh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
