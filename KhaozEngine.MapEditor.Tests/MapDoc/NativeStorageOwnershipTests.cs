using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Tiled storage writers own their manifest, temp, lock and tiles namespace and sweep it on save, so a
/// native closure reference inside it must refuse before anything is published or written.</summary>
public sealed class NativeStorageOwnershipTests
{
    [Fact]
    public void NativeStorage_ConversionChecksTheTiledTargetNamespaceForAMonolithicSource()
    {
        // The absolute mesh reference points into the future tiled map's tiles folder. A monolithic source
        // does not own that folder, so the document opens, but the tiled target would sweep it.
        using var f = new NativeLifecycleFixture(absoluteReferences: true, meshRelativePath: "tiled-world/tiles/rock.glb");
        Assert.NotNull(f.Session.Summary().Native);
        string target = Path.Combine(f.ResourceRoot, "tiled-world");
        string hash = f.Session.Summary().Native!.AuthoredHash;
        string mesh = f.MeshPath;
        byte[] bytes = File.ReadAllBytes(mesh);
        File.Move(mesh, Path.Combine(f.ResourceRoot, "rock.glb"));
        Directory.Delete(target, true);

        var ex = Assert.Throws<MapDocumentException>(() => f.Session.ConvertToTiled(target));
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);

        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.Move(Path.Combine(f.ResourceRoot, "rock.glb"), mesh);
        Assert.Equal(bytes, File.ReadAllBytes(mesh));
        Assert.Equal(hash, f.Session.Summary().Native!.AuthoredHash);
    }

    [Fact]
    public void NativeStorage_ConversionIntoAnExistingDirectoryKeepsTheOverwriteGuardAndItsResources()
    {
        using var f = new NativeLifecycleFixture(meshRelativePath: "tiles/props/rock.glb");
        string target = Path.Combine(f.ResourceRoot, "tiled-world");
        f.CopyResourcesTo(target);
        string targetMesh = Path.Combine(target, "tiles", "props", "rock.glb");
        byte[] before = File.ReadAllBytes(targetMesh);

        // MapDocumentFile.DetectForm reports any existing directory as tiled, so the session refuses first.
        var ex = Assert.Throws<MapDocumentException>(() => f.Session.ConvertToTiled(target));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(targetMesh));
        Assert.False(File.Exists(Path.Combine(target, "map.json")));

        // The lifecycle check itself names the explicit target form, independent of the source form.
        var reserved = Assert.Throws<MapDocumentException>(() =>
            NativeDocumentService.Verify(f.SessionDocument, target, MapDocumentForm.Tiled, target));
        Assert.Contains("reserved", reserved.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(targetMesh));
        f.Session.Save();
        Assert.True(File.Exists(f.MeshPath));
    }

    [Theory]
    [InlineData("tiles/props/rock.glb", false)]
    [InlineData("kit/../tiles/props/rock.glb", false)]
    [InlineData("./tiles/rock.glb", false)]
    [InlineData("tiled-world/tiles/rock.glb", true)]
    public void NativeStorage_TiledOpenRefusesResourceInsideItsNamespaceBeforePublishing(string mesh, bool absolute)
    {
        using var f = new NativeLifecycleFixture(absolute, mesh);
        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        MapDocumentFile.SaveTiled(f.Document, tiled);
        if (!absolute) f.CopyResourcesTo(tiled);
        string reserved = absolute ? f.MeshPath : Path.GetFullPath(mesh, tiled);
        byte[] bytes = File.ReadAllBytes(reserved);
        var document = f.SessionDocument;

        // The end-to-end M1 path: before the guard, Open verified, the edit applied and the tiled save's sweep
        // deleted the verified resource. An early refusal is acceptable, losing the bytes never is.
        var fresh = new MapEditSession();
        try
        {
            fresh.Open(tiled);
            new MutationService(fresh).PlacementLabel("gate", "swept");
            fresh.Save();
        }
        catch (MapDocumentException)
        {
        }
        Assert.True(File.Exists(reserved), "a tiled save deleted a verified native resource");
        Assert.Equal(bytes, File.ReadAllBytes(reserved));

        var ex = Assert.Throws<MapDocumentException>(() => f.Session.Open(tiled));
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
        Assert.Same(document, f.SessionDocument);
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        Assert.Equal(bytes, File.ReadAllBytes(reserved));
    }

    [Fact]
    public void NativeStorage_MissingTiledStorageRefusesSaveAndRetileWithoutRecreatingIt()
    {
        using var f = new NativeLifecycleFixture(absoluteReferences: true);
        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        f.Session.ConvertToTiled(tiled);
        var service = new MutationService(f.Session);
        service.PlacementLabel("gate", "unsaved");
        float tileSize = f.SessionDocument.TileSize;
        Directory.Delete(tiled, true);

        Assert.Throws<MapDocumentException>(() => f.Session.Save());
        Assert.Throws<MapDocumentException>(() => f.Session.Retile(tileSize / 2));
        Assert.False(Directory.Exists(tiled));
        Assert.False(File.Exists(tiled));
        Assert.Equal(tileSize, f.SessionDocument.TileSize);
        Assert.True(f.Session.IsDirty);
        Assert.Equal("unsaved", f.SessionDocument.Placements.Single(p => p.Id == "gate").DisplayName);
    }

    [Fact]
    public void NativeStorage_MissingMonolithicRetileRefusesButSaveRecreatesTheFile()
    {
        using var f = new NativeLifecycleFixture();
        float tileSize = f.SessionDocument.TileSize;
        File.Delete(f.ValidPath);
        Assert.Throws<MapDocumentException>(() => f.Session.Retile(tileSize / 2));
        Assert.False(File.Exists(f.ValidPath));
        Assert.False(Directory.Exists(f.ValidPath));
        Assert.Equal(tileSize, f.SessionDocument.TileSize);

        f.Session.Save();
        Assert.Equal(NativeLifecycleFixture.HighWater, MapDocumentFile.Load(f.ValidPath).NumericIdHighWaterMark);
    }

    [Fact]
    public void NativeStorage_MonolithicSaveRefusesWhenItsPathBecameADirectory()
    {
        using var f = new NativeLifecycleFixture();
        File.Delete(f.ValidPath);
        Directory.CreateDirectory(f.ValidPath);
        Assert.Throws<MapDocumentException>(() => f.Session.Save());
        Assert.Empty(Directory.GetFileSystemEntries(f.ValidPath));
    }
}
