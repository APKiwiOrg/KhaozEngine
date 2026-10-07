using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class SurfaceSourceSnapshotTests
{
    [Fact]
    public void DocumentSurfaceSource_CaptureIsADeepCopy()
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        var key = new MapPatchKey("ground", 0, 0);
        MapDocumentSurfaceSource src = MapDocumentSurfaceSource.Capture(doc);
        string id = src.SnapshotId;
        string root = src.RootSha256;
        Assert.StartsWith("doc:", id);
        Assert.Equal(MapSurfaceSemantics.RootDigest(doc), root);
        doc.Surfaces.Patches[key].Heights[0] = 7;
        MapPatchRead first = src.ReadPatch(key);
        Assert.Equal((MapPatchStatus.Present, 0), (first.Status, first.PagesRead));
        first.Patch!.Heights[0] = 8;
        first.Patch.SetPresent(0, 0, false);
        MapPatchRead second = src.ReadPatch(key);
        Assert.NotSame(first.Patch, second.Patch);
        Assert.Equal(1000, second.Patch!.Heights[0]);
        Assert.True(second.Patch.IsPresent(0, 0));
        Assert.Equal((id, root), (src.SnapshotId, src.RootSha256));
        Assert.NotEqual(id, MapDocumentSurfaceSource.Capture(doc).SnapshotId);
        Assert.NotEqual(root, MapDocumentSurfaceSource.Capture(doc).RootSha256);
        var dependent = new MapPatchKey("ground", 1, 0);
        doc.Surfaces.Patches[dependent].CornerDependencies.Clear();
        src.ReadPatch(dependent).Patch!.CornerDependencies.Clear();
        Assert.Equal(5, src.ReadPatch(dependent).Patch!.CornerDependencies.Count);
        var ridge = new MapPatchKey("ridge", 0, 0);
        doc.Surfaces.Patches[ridge].Records.Clear();
        src.ReadPatch(ridge).Patch!.Records.Clear();
        Assert.Equal("ridge-rim", Assert.Single(src.ReadPatch(ridge).Patch!.Records).Id);
        doc.Surfaces.Refs.Clear();
        Assert.Equal(new[] { "far", "ground", "ridge" }, src.Surfaces.Select(s => s.Id).OrderBy(s => s));
        Assert.Equal((id, root), (src.SnapshotId, src.RootSha256));
        var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(61, 1), new Vector2(62, 2),
            null, null, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
        MapPatchFindResult found = src.FindPatches(scope);
        Assert.Equal((MapFindStatus.Complete, 0), (found.Status, found.PagesRead));
        Assert.Equal(new[] { key, ridge }, found.Patches.Select(p => p.Key));
        found.Patches[0].Patch!.Heights[0] = 9;
        Assert.Equal(1000, src.ReadPatch(key).Patch!.Heights[0]);

        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            MapDocumentSurfaceSource captured = MapDocumentSurfaceSource.Capture(window);
            string capturedId = captured.SnapshotId;
            window.Surfaces.Patches[key].Heights[6] = 1100;
            MapDocumentFile.SaveTiled(window, dir);
            Assert.Equal(capturedId, captured.SnapshotId);
            Assert.Equal(1000, captured.ReadPatch(key).Patch!.Heights[6]);
            MapPatchRead unread = captured.ReadPatch(new("far", 300, 0));
            Assert.Equal((MapPatchStatus.Unloaded, 0), (unread.Status, unread.PagesRead));
            Assert.Null(unread.Patch);
            Assert.NotEqual(capturedId, MapDocumentSurfaceSource.Capture(window).SnapshotId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StoredSurfaceSource_PinsItsGenerationAndReportsSweptFilesMissing()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            var key = new MapPatchKey("far", 300, 0);
            var ground = new MapPatchKey("ground", 0, 0);
            MapStoredSurfaceSource pinned = MapStoredSurfaceSource.Open(dir);
            string id = pinned.SnapshotId;
            string root = pinned.RootSha256;
            Assert.Equal("manifest:" + AssertFixtures.Sha256(Path.Combine(dir, "map.json")), id);
            pinned.ReadPatch(ground).Patch!.Heights[0] = 7;
            Assert.Equal(1000, pinned.ReadPatch(ground).Patch!.Heights[0]);
            var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(61, 1), new Vector2(62, 2),
                null, null, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
            MapPatchFindResult first = pinned.FindPatches(scope);
            Assert.Equal(MapFindStatus.Complete, first.Status);
            Assert.Equal(0, pinned.FindPatches(scope).PagesRead);
            MapDocument whole = MapDocumentFile.LoadTiled(dir);
            whole.Surfaces.Patches[key].Heights[0] = 2100;
            MapDocumentFile.SaveTiled(whole, dir);
            byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
            var files = SurfaceStorageFixtures.SurfaceFiles(dir);
            MapPatchRead swept = pinned.ReadPatch(key);
            Assert.Equal(MapPatchStatus.Missing, swept.Status);
            Assert.Null(swept.Patch);
            Assert.Equal((id, root), (pinned.SnapshotId, pinned.RootSha256));
            Assert.Equal(1000, pinned.ReadPatch(ground).Patch!.Heights[0]);
            MapStoredSurfaceSource current = MapStoredSurfaceSource.Open(dir);
            MapPatchRead fresh = current.ReadPatch(key);
            Assert.Equal((MapPatchStatus.Present, 2100), (fresh.Status, fresh.Patch!.Heights[0]));
            Assert.NotEqual(id, current.SnapshotId);
            Assert.NotEqual(root, current.RootSha256);
            Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
