using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class SurfaceAcquisitionSessionTests
{
    [Fact]
    public void Session_PinsMetadataAndRejectsOutOfOrderAndDisposedWork()
    {
        MapDocument doc = SessionConformanceFixtures.OnePatch();
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            var key = new MapPatchKey("ground", 0, 0);
            var unserved = new MapPatchKey("ground", 1, 0);
            IMapSurfaceAcquisitionSource[] sources =
            {
                MapDocumentSurfaceSource.Capture(doc), MapStoredSurfaceSource.Open(dir),
            };
            var scope = Scope();
            using IMapSurfaceAcquisitionSession captured = sources[0].OpenAcquisition(scope);
            using IMapSurfaceAcquisitionSession stored = sources[1].OpenAcquisition(scope);
            IMapSurfaceAcquisitionSession[] sessions = { captured, stored };
            for (int i = 0; i < sources.Length; i++)
            {
                IMapSurfaceAcquisitionSource source = sources[i];
                IMapSurfaceAcquisitionSession session = sessions[i];
                string snapshot = source.SnapshotId, root = source.RootSha256;
                Assert.Equal((snapshot, root), (session.SnapshotId, session.RootSha256));
                Assert.Equal(0, session.PagesRead);
                IReadOnlyList<MapPatchKey> beforeFind = session.ReservedPatchKeys;
                Assert.Empty(beforeFind);
                Assert.Throws<InvalidOperationException>(() => session.ReadPatch(key));
                Assert.Throws<InvalidOperationException>(() => session.TryGetIncidentRecords(unserved, out _));
                Assert.Empty(session.ReservedPatchKeys);
                MapPatchFindResult found = session.FindPatches();
                Assert.Equal(MapFindStatus.Complete, found.Status);
                Assert.Equal(key, Assert.Single(found.Patches).Key);
                Assert.Empty(beforeFind);
                Assert.Equal(new[] { key }, session.ReservedPatchKeys);
                Assert.Throws<InvalidOperationException>(() => session.TryGetIncidentRecords(unserved, out _));
                Assert.Throws<InvalidOperationException>(() => session.FindPatches());
            }
            doc.Surfaces.Patches[key].Heights[0]++;
            MapDocumentFile.SaveTiled(doc, dir);
            IMapSurfaceSource[] newer =
            {
                MapDocumentSurfaceSource.Capture(doc), MapStoredSurfaceSource.Open(dir),
            };
            for (int i = 0; i < sources.Length; i++)
            {
                IMapSurfaceAcquisitionSource source = sources[i];
                IMapSurfaceAcquisitionSession session = sessions[i];
                string snapshot = source.SnapshotId, root = source.RootSha256;
                Assert.NotEqual(snapshot, newer[i].SnapshotId);
                Assert.NotEqual(root, newer[i].RootSha256);
                Assert.Equal((snapshot, root), (session.SnapshotId, session.RootSha256));
                int pages = session.PagesRead;
                session.Dispose();
                session.Dispose();
                Assert.Throws<ObjectDisposedException>(() => session.FindPatches());
                Assert.Throws<ObjectDisposedException>(() => session.ReadPatch(key));
                Assert.Throws<ObjectDisposedException>(() => session.TryGetSurface("ground", out _));
                Assert.Throws<ObjectDisposedException>(() => session.TryGetIncidentRecords(key, out _));
                Assert.Equal(pages, session.PagesRead);
                Assert.Equal((snapshot, root), (session.SnapshotId, session.RootSha256));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Session_SurfaceLookupReturnsDetachedNestedMetadata()
    {
        MapDocument doc = SessionConformanceFixtures.OnePatch();
        var tags = new List<string> { "captured" };
        doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with
        {
            IndoorSpan = new("room-span", new("room", new("ground", 0, 0)), 0, 100, tags),
        };
        doc.Surfaces.Patches[new("ground", 0, 0)].Records.Add(new MapSpaceDoc("room", MapSpaceKind.Cave,
            null, null, Array.Empty<string>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(),
            Array.Empty<MapRecordRef>()));
        MapDocumentSurfaceSource source = MapDocumentSurfaceSource.Capture(doc);
        using IMapSurfaceAcquisitionSession session = ((IMapSurfaceAcquisitionSource)source).OpenAcquisition(Scope());
        Assert.True(session.TryGetSurface("ground", out MapSurfaceRef? surface));
        Assert.NotNull(surface);
        var detached = Assert.IsType<ReadOnlyCollection<string>>(surface.IndoorSpan!.DomainTags);
        Assert.NotSame(tags, detached);
        Assert.Equal(new[] { "captured" }, detached);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)detached)[0] = "returned-edit");
        tags[0] = "caller-edit";
        doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with { IndoorSpan = null };
        Assert.Equal(new[] { "captured" }, detached);
        Assert.True(session.TryGetSurface("ground", out MapSurfaceRef? again));
        Assert.Equal(new[] { "captured" }, again!.IndoorSpan!.DomainTags);
        Assert.False(session.TryGetSurface("undeclared", out MapSurfaceRef? absent));
        Assert.Null(absent);
        Assert.Equal(0, session.PagesRead);
        Assert.Empty(session.ReservedPatchKeys);
    }

    [Fact]
    public void Session_IncidentKnowledgeDistinguishesCompleteExistingAndNewPartialKeys()
    {
        var seed = new MapPatchKey("ground", 0, 0);
        using (IMapSurfaceAcquisitionSession complete = ((IMapSurfaceAcquisitionSource)
            MapDocumentSurfaceSource.Capture(SessionConformanceFixtures.OnePatch())).OpenAcquisition(Scope()))
        {
            MapPatchFindResult found = complete.FindPatches();
            Assert.Equal(MapFindStatus.Complete, found.Status);
            Assert.Equal(seed, Assert.Single(found.Patches).Key);
            Assert.True(complete.TryGetIncidentRecords(seed, out IReadOnlyList<MapRecordRef>? records));
            Assert.Empty(Assert.IsType<ReadOnlyCollection<MapRecordRef>>(records));
        }

        MapDocument doc = SurfaceStorageFixtures.RecordReferrer();
        var remote = new MapPatchKey("ground", 300, 0);
        MapSurfacePatch anchor = doc.Surfaces.Patches[remote];
        var footprint = Assert.IsType<MapSpaceFootprint>(Assert.Single(anchor.Records));
        anchor.Records[0] = footprint with { Lattice = seed };
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            Assert.True(window.Tiles!.IsPartial);
            Assert.False(window.Surfaces.Patches.ContainsKey(remote));
            var added = new MapPatchKey("ground", 1, 0);
            window.Surfaces.Patches.Add(added, SurfaceStorageFixtures.FlatPatch(added, 1000));
            using IMapSurfaceAcquisitionSession session = ((IMapSurfaceAcquisitionSource)
                MapDocumentSurfaceSource.Capture(window)).OpenAcquisition(Scope(68));
            MapPatchFindResult found = session.FindPatches();
            Assert.Equal(MapFindStatus.Complete, found.Status);
            Assert.Equal(new[] { seed, added }, found.Patches.Select(p => p.Key));
            Assert.True(session.TryGetIncidentRecords(seed, out IReadOnlyList<MapRecordRef>? existing));
            Assert.Equal(new MapRecordRef("yard-far", remote), Assert.Single(existing!));
            Assert.IsType<ReadOnlyCollection<MapRecordRef>>(existing);
            Assert.False(session.TryGetIncidentRecords(added, out IReadOnlyList<MapRecordRef>? unknown));
            Assert.Null(unknown);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static MapSurfaceScope Scope(float maxX = 4) => new(WorldFrame.Origin, Vector2.Zero, new Vector2(maxX, 4),
        null, null, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
}
