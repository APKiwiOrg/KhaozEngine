using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceAcquisitionBudgetTests
{
    static readonly MapPatchKey Seed = new("seed", 0, 0), Anchor = new("anchor", 0, 0), Bound = new("bound", 0, 0);
    static readonly MapPatchKey Deleted = new("ground", 0, 0), Resident = new("ground", 1, 0);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredSession_AllPhasesShareVisitsBeforeCacheChecks(bool warm)
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(ThreeKeys());
        try
        {
            MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
            Assert.Equal(3, source.Index.Directory.Count);
            Warm(source, warm);
            var work = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(Scope(5), work);
            Assert.Equal(0, work.PageVisits);
            Assert.Equal(0, work.DecodeAttempts);
            MapPatchFindResult found = session.FindPatches();
            Assert.Equal(MapFindStatus.Complete, found.Status);
            Assert.Equal(Seed, Assert.Single(found.Patches).Key);
            Assert.Equal(2, work.PageVisits);
            Assert.Equal(1, work.DirectoryCallbacks);
            Assert.Equal(1, work.IndexCallbacks);
            Assert.Equal(MapPatchStatus.Present, session.ReadPatch(Anchor).Status);
            Assert.Equal(4, work.PageVisits);
            Assert.Equal(2, work.DirectoryCallbacks);
            Assert.Equal(2, work.IndexCallbacks);
            Assert.Throws<MapSurfaceCapacityException>(() => session.ReadPatch(Bound));
            Assert.Equal(5, work.PageVisits);
            Assert.Equal(3, work.DirectoryCallbacks);
            Assert.Equal(2, work.IndexCallbacks);
            Assert.Equal(5, work.DirectoryCallbacks + work.IndexCallbacks);
            Assert.Equal(warm ? 0 : 5, work.DecodeAttempts);
            Assert.Equal(warm ? 0 : 5, session.PagesRead);
            Assert.Equal(2, work.PayloadReads);
            Assert.Equal(new[] { Anchor, Bound, Seed }, session.ReservedPatchKeys);
            MapDirectoryPageRef boundDirectory = source.Index.Directory.Single(d => d.SurfaceId == "bound");
            MapIndexPageRef boundIndex = Assert.Single(source.Index.DirectoryPages[boundDirectory.Sha256]);
            Assert.Equal(warm, source.Index.IndexPages.ContainsKey(boundIndex.Sha256));

            MapStoredSurfaceSource enoughSource = MapStoredSurfaceSource.Open(dir);
            Warm(enoughSource, warm);
            var enoughWork = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession enough = enoughSource.OpenAcquisition(Scope(6), enoughWork);
            Assert.Equal(MapFindStatus.Complete, enough.FindPatches().Status);
            Assert.Equal(MapPatchStatus.Present, enough.ReadPatch(Anchor).Status);
            Assert.Equal(MapPatchStatus.Present, enough.ReadPatch(Bound).Status);
            Assert.Equal(6, enoughWork.PageVisits);
            Assert.Equal(3, enoughWork.DirectoryCallbacks);
            Assert.Equal(3, enoughWork.IndexCallbacks);
            Assert.Equal(warm ? 0 : 6, enoughWork.DecodeAttempts);
            Assert.Equal(warm ? 0 : 6, enough.PagesRead);
            Assert.Equal(3, enoughWork.PayloadReads);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void StoredSession_OperationDeltasAndNewBudgetsAreIndependent()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(ThreeKeys());
        try
        {
            MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
            string snapshot = source.SnapshotId, root = source.RootSha256;
            var firstWork = new MapSurfaceAcquisitionWork();
            using (IMapSurfaceAcquisitionSession first = source.OpenAcquisition(Scope(6), firstWork))
            {
                MapPatchFindResult found = first.FindPatches();
                Assert.Equal(MapFindStatus.Complete, found.Status);
                Assert.Equal(Seed, Assert.Single(found.Patches).Key);
                Assert.Equal(2, found.PagesRead);
                MapPatchRead anchor = first.ReadPatch(Anchor), bound = first.ReadPatch(Bound);
                Assert.Equal((MapPatchStatus.Present, 2), (anchor.Status, anchor.PagesRead));
                Assert.Equal((MapPatchStatus.Present, 2), (bound.Status, bound.PagesRead));
                Assert.Equal(6, first.PagesRead);
                Assert.Equal(6, firstWork.PageVisits);
                Assert.Equal(6, firstWork.DecodeAttempts);
                Assert.Equal((snapshot, root), (first.SnapshotId, first.RootSha256));
            }
            Assert.Equal(1, firstWork.Disposals);
            Assert.Equal(3, source.Index.ReadDirectoryPages.Count);
            Assert.Equal(3, source.Index.ReadIndexPages.Count);
            var nextWork = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession next = source.OpenAcquisition(Scope(6), nextWork);
            MapPatchFindResult nextFound = next.FindPatches();
            Assert.Equal(MapFindStatus.Complete, nextFound.Status);
            Assert.Equal(Seed, Assert.Single(nextFound.Patches).Key);
            Assert.Equal(0, nextFound.PagesRead);
            MapPatchRead nextAnchor = next.ReadPatch(Anchor), nextBound = next.ReadPatch(Bound);
            Assert.Equal((MapPatchStatus.Present, 0), (nextAnchor.Status, nextAnchor.PagesRead));
            Assert.Equal((MapPatchStatus.Present, 0), (nextBound.Status, nextBound.PagesRead));
            Assert.Equal(0, next.PagesRead);
            Assert.Equal(6, nextWork.PageVisits);
            Assert.Equal(0, nextWork.DecodeAttempts);
            Assert.Equal(3, nextWork.DirectoryCallbacks);
            Assert.Equal(3, nextWork.IndexCallbacks);
            Assert.Equal((snapshot, root), (next.SnapshotId, next.RootSha256));
            Assert.Equal((snapshot, root), (source.SnapshotId, source.RootSha256));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CapturedSession_DeletedSeedAndResidentSeedShareTheCandidateCap()
    {
        string dir = TemporaryDirectory();
        try
        {
            MapDocument doc = SessionConformanceFixtures.DeletedSeedAndResidentSeed(dir);
            Assert.True(doc.Tiles!.IsPartial);
            Assert.False(doc.Surfaces.Patches.ContainsKey(new("ground", 300, 0)));
            MapDocumentSurfaceSource source = MapDocumentSurfaceSource.Capture(doc);
            var work = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(Scope(4, 1, 68), work);
            AssertCapacity(session.FindPatches());
            var reserved = Assert.IsType<ReadOnlyCollection<MapPatchKey>>(session.ReservedPatchKeys);
            Assert.Equal(new[] { Deleted }, reserved);
            Assert.NotSame(reserved, session.ReservedPatchKeys);
            Assert.Throws<NotSupportedException>(() => ((IList<MapPatchKey>)reserved)[0] = Resident);
            Assert.Equal(1, work.PayloadReads);
            Assert.Throws<InvalidOperationException>(() => session.TryGetIncidentRecords(Resident, out _));
            Assert.Equal(1, work.PayloadReads);

            var enoughWork = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession enough = source.OpenAcquisition(Scope(4, 2, 68), enoughWork);
            MapPatchFindResult found = enough.FindPatches();
            Assert.Equal(MapFindStatus.Complete, found.Status);
            Assert.Equal(Resident, Assert.Single(found.Patches).Key);
            Assert.Equal(new[] { Deleted, Resident }, enough.ReservedPatchKeys);
            Assert.Equal(2, enoughWork.PayloadReads);
            Assert.Contains(found.KnownEmpty, r => r.SurfaceId == "ground" && r.Slots.Contains(0, 0));
            Assert.DoesNotContain(found.KnownEmpty, r => r.SurfaceId == "ground" && r.Slots.Contains(1, 0));
            Assert.Empty(found.Unavailable);
            Assert.Equal(new[] { Deleted }, reserved);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CapturedSession_BothPassesShareVisitsWithZeroDecodes()
    {
        string dir = TemporaryDirectory();
        try
        {
            MapDocument doc = SessionConformanceFixtures.DeletedSeedAndResidentSeed(dir);
            MapDocumentSurfaceSource source = MapDocumentSurfaceSource.Capture(doc);
            var work = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession session = source.OpenAcquisition(Scope(3, 2, 68), work);
            AssertCapacity(session.FindPatches());
            Assert.Equal(3, work.PageVisits);
            Assert.Equal(2, work.DirectoryCallbacks);
            Assert.Equal(1, work.IndexCallbacks);
            Assert.Equal(3, work.DirectoryCallbacks + work.IndexCallbacks);
            Assert.Equal(0, work.DecodeAttempts);
            Assert.Equal(0, session.PagesRead);
            Assert.Equal(1, work.PayloadReads);
            Assert.Equal(new[] { Deleted }, session.ReservedPatchKeys);
            var enoughWork = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession enough = source.OpenAcquisition(Scope(4, 2, 68), enoughWork);
            MapPatchFindResult found = enough.FindPatches();
            Assert.Equal(MapFindStatus.Complete, found.Status);
            Assert.Equal(Resident, Assert.Single(found.Patches).Key);
            Assert.Equal(new[] { Deleted, Resident }, enough.ReservedPatchKeys);
            Assert.Equal(4, enoughWork.PageVisits);
            Assert.Equal(2, enoughWork.DirectoryCallbacks);
            Assert.Equal(2, enoughWork.IndexCallbacks);
            Assert.Equal(0, enoughWork.DecodeAttempts);
            Assert.Equal(0, enough.PagesRead);
            Assert.Equal(2, enoughWork.PayloadReads);
            Assert.Contains(found.KnownEmpty, r => r.SurfaceId == "ground" && r.Slots.Contains(0, 0));
            Assert.DoesNotContain(found.KnownEmpty, r => r.SurfaceId == "ground" && r.Slots.Contains(1, 0));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Session_MetadataAllowancePrecedesIndexedWork()
    {
        MapDocument doc = SessionConformanceFixtures.ManyExcludedRefs(1024);
        Assert.Equal(1024, doc.Surfaces.Refs.Count);
        Assert.All(doc.Surfaces.Refs, s => Assert.Equal(MapSurfaceRole.Ceiling, s.Role));
        var key = new MapPatchKey("session-ref-0000", 0, 0);
        Assert.Equal(key, Assert.Single(doc.Surfaces.Patches).Key);
        var work = new MapSurfaceAcquisitionWork();
        using IMapSurfaceAcquisitionSession session = MapDocumentSurfaceSource.Capture(doc).OpenAcquisition(Scope(1), work);
        MapPatchFindResult found = session.FindPatches();
        Assert.Equal(MapFindStatus.Complete, found.Status);
        Assert.Empty(found.Patches);
        Assert.Empty(found.KnownEmpty);
        Assert.Empty(found.Unavailable);
        Assert.Equal(1024, work.MetadataChecks);
        Assert.Equal(0, work.RangeOperations);
        Assert.Equal(0, work.PageVisits);
        Assert.True(session.TryGetSurface("session-ref-0000", out _));
        Assert.Equal(1025, work.MetadataChecks);
        Assert.Equal(1, work.SurfaceLookups);
        Assert.Throws<MapSurfaceCapacityException>(() => session.TryGetSurface("session-ref-0001", out _));
        Assert.Throws<MapSurfaceCapacityException>(() => session.ReadPatch(key));
        Assert.Equal(1025, work.MetadataChecks);
        Assert.Equal(1, work.SurfaceLookups);
        Assert.Equal(0, work.LookupNodeInspections);
        Assert.Equal(0, work.DirectoryCallbacks);
        Assert.Equal(0, work.IndexCallbacks);
        Assert.Equal(0, work.PayloadReads);
        Assert.Equal(0, work.PageVisits);
        Assert.Equal(0, work.DecodeAttempts);
        Assert.Equal(0, session.PagesRead);
    }

    [Fact]
    public void Session_RangeAllowancePrecedesBookkeeping()
    {
        MapDocument doc = SessionConformanceFixtures.ManyEmptyRefs(1024);
        Assert.Equal(1024, doc.Surfaces.Refs.Count);
        Assert.All(doc.Surfaces.Refs, s => Assert.Equal(MapSurfaceRole.SupportFloor, s.Role));
        Assert.Empty(doc.Surfaces.Patches);
        var work = new MapSurfaceAcquisitionWork();
        using IMapSurfaceAcquisitionSession session = MapDocumentSurfaceSource.Capture(doc).OpenAcquisition(Scope(1), work);
        MapPatchFindResult found = session.FindPatches();
        Assert.Equal(MapFindStatus.Complete, found.Status);
        Assert.Empty(found.Patches);
        Assert.Empty(found.Unavailable);
        Assert.Equal(1024, found.KnownEmpty.Count);
        Assert.Equal(1024, work.MetadataChecks);
        Assert.Equal(1024, work.RangeOperations);
        MapPatchRead empty = session.ReadPatch(new("session-ref-0000", 300, 0));
        Assert.Equal(MapPatchStatus.KnownEmpty, empty.Status);
        Assert.Equal(0, empty.PagesRead);
        Assert.Equal(1025, work.RangeOperations);
        Assert.Throws<MapSurfaceCapacityException>(() => session.ReadPatch(new("session-ref-0000", 301, 0)));
        Assert.Equal(1025, work.RangeOperations);
        Assert.Equal(0, work.LookupNodeInspections);
        Assert.Equal(0, work.DirectoryCallbacks);
        Assert.Equal(0, work.IndexCallbacks);
        Assert.Equal(0, work.PayloadReads);
        Assert.Equal(0, work.PageVisits);
        Assert.Equal(0, work.DecodeAttempts);
        Assert.Equal(0, session.PagesRead);
    }

    [Fact]
    public void Session_DecodeAttemptsPrecedeStorageIo()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(ThreeKeys());
        try
        {
            MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
            MapDirectoryPageRef first = source.Index.Directory.Single(d => d.SurfaceId == "seed");
            MapDirectoryPageRef second = source.Index.Directory.Single(d => d.SurfaceId == "anchor");
            var work = new MapSurfaceAcquisitionWork();
            var budget = new MapPageBudget(1, work);
            Assert.Equal(MapPatchStatus.Present, source.Directory(first, budget));
            Assert.Equal(1, work.DecodeAttempts);
            Assert.Equal(1, budget.Reads);
            File.Delete(MapSurfaceStorageLayout.PathOf(dir, 'd', second.Sha256));
            Assert.Throws<MapSurfaceCapacityException>(() => source.Directory(second, budget));
            Assert.Equal(1, work.DecodeAttempts);
            Assert.Equal(1, budget.Reads);
            Assert.Equal(0, work.PageVisits);
            Assert.True(source.Index.DirectoryPages.ContainsKey(first.Sha256));
            Assert.False(source.Index.DirectoryPages.ContainsKey(second.Sha256));
            var controlWork = new MapSurfaceAcquisitionWork();
            var controlBudget = new MapPageBudget(1, controlWork);
            Assert.Equal(MapPatchStatus.Missing, source.Directory(second, controlBudget));
            Assert.Equal(1, controlWork.DecodeAttempts);
            Assert.Equal(0, controlBudget.Reads);
            Assert.False(source.Index.DirectoryPages.ContainsKey(second.Sha256));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_NormalizesCoordinateOverflow(bool stored)
    {
        MapDocument doc = SessionConformanceFixtures.OnePatch();
        MapSurfaceRef surface = doc.Surfaces.Refs[0];
        doc.Surfaces.Refs[0] = surface with { Frame = surface.Frame with { CellUnitMetres = new(1, int.MaxValue) } };
        string dir = SurfaceStorageFixtures.SaveToTemp(doc);
        try
        {
            var scope = Scope(4) with { LocalMin = new(float.MaxValue / 2, 0), LocalMax = new(float.MaxValue, 4) };
            var work = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession session = stored
                ? MapStoredSurfaceSource.Open(dir).OpenAcquisition(scope, work)
                : MapDocumentSurfaceSource.Capture(doc).OpenAcquisition(scope, work);
            Assert.Throws<MapExactOverflowException>(() => session.FindPatches());
            Assert.Equal(0, work.PayloadReads);
            Assert.Equal(0, work.PageVisits);
            Assert.Equal(0, work.DecodeAttempts);
            Assert.Empty(session.ReservedPatchKeys);
        }
        finally { Directory.Delete(dir, recursive: true); }

        string corruptDir = SurfaceStorageFixtures.SaveToTemp(SessionConformanceFixtures.OnePatch());
        try
        {
            var key = new MapPatchKey("ground", 0, 0);
            SurfaceStorageFixtures.FlipPayloadByte(corruptDir, key);
            var work = new MapSurfaceAcquisitionWork();
            using IMapSurfaceAcquisitionSession session = MapStoredSurfaceSource.Open(corruptDir).OpenAcquisition(Scope(2), work);
            MapPatchFindResult found = session.FindPatches();
            Assert.Equal(MapFindStatus.Incomplete, found.Status);
            Assert.Empty(found.Patches);
            Assert.Equal((key, MapPatchStatus.Corrupt), (Assert.Single(found.Unavailable).Key, found.Unavailable[0].Status));
            Assert.Equal(1, work.PayloadReads);
            Assert.Equal(2, found.PagesRead);
        }
        finally { Directory.Delete(corruptDir, recursive: true); }
    }

    static MapDocument ThreeKeys()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        MapSurfaceRef template = doc.Surfaces.Refs[0];
        doc.Surfaces.Refs.Clear();
        foreach (MapPatchKey key in new[] { Seed, Anchor, Bound })
        {
            doc.Surfaces.Refs.Add(template with { Id = key.SurfaceId, Role = key == Seed ? MapSurfaceRole.SupportFloor : MapSurfaceRole.Ceiling });
            doc.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
        }
        return doc;
    }

    static void Warm(MapStoredSurfaceSource source, bool warm)
    {
        if (!warm) return;
        foreach (MapPatchKey key in new[] { Seed, Anchor, Bound })
        {
            MapPatchRead read = source.ReadPatch(key);
            Assert.Equal((MapPatchStatus.Present, 2), (read.Status, read.PagesRead));
        }
        Assert.Equal(3, source.Index.ReadDirectoryPages.Count);
        Assert.Equal(3, source.Index.ReadIndexPages.Count);
    }

    static MapSurfaceScope Scope(int pages, int candidates = 3, float maxX = 4) =>
        new(WorldFrame.Origin, Vector2.Zero, new Vector2(maxX, 4), null, null,
            new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits(MaxCandidatePatches: candidates, MaxPageReads: pages));

    static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "mapdoc-session-synthetic-" + Guid.NewGuid().ToString("N"));

    static void AssertCapacity(MapPatchFindResult found)
    {
        Assert.Equal(MapFindStatus.CapacityExceeded, found.Status);
        Assert.Empty(found.Patches);
        Assert.Empty(found.KnownEmpty);
        Assert.Empty(found.Unavailable);
    }
}
