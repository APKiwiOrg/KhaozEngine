using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class SparseSurfaceIndexTests
{
    [Fact]
    public void SparseIndex_UnloadedIsNotEmpty()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            MapStoredSurfaceSource stored = MapStoredSurfaceSource.Open(dir);
            Assert.Equal("manifest:" + AssertFixtures.Sha256(Path.Combine(dir, "map.json")), stored.SnapshotId);
            Assert.Equal(MapPatchStatus.Present, stored.ReadPatch(new("ground", 0, 0)).Status);
            Assert.Equal(MapPatchStatus.KnownEmpty, stored.ReadPatch(new("ground", 2, 0)).Status);
            Assert.Equal(MapPatchStatus.KnownEmpty, stored.ReadPatch(new("ground", 40, 40)).Status);
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            MapSurfaceStorageIndex index = window.Tiles!.Surfaces!;
            Assert.Equal(MapPatchStatus.Present, index.StatusOf(new("ground", 0, 0)));
            Assert.Equal(MapPatchStatus.KnownEmpty, index.StatusOf(new("ground", 2, 0)));
            Assert.Equal(MapPatchStatus.KnownEmpty, index.StatusOf(new("ground", 40, 0)));
            Assert.Equal(MapPatchStatus.Unloaded, index.StatusOf(new("far", 300, 0)));
            Assert.Equal(MapPatchStatus.KnownEmpty, index.StatusOf(new("far", 40, 0)));
            Assert.True(index.IsPartial);
            MapDocumentSurfaceSource captured = MapDocumentSurfaceSource.Capture(window);
            MapPatchRead unavailable = captured.ReadPatch(new("far", 300, 0));
            Assert.Equal((MapPatchStatus.Unloaded, 0), (unavailable.Status, unavailable.PagesRead));
            Assert.Null(unavailable.Patch);

            var limits = new MapQueryLimits();
            Assert.Equal((256, 4096, 64, 32, 64, 8), (limits.MaxCandidatePatches,
                limits.MaxInspectedFaces, limits.MaxSupportIntersections, limits.MaxPageReads,
                limits.MaxRecordReads, limits.MaxRecordDepth));
            var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(61, 1), new Vector2(62, 2),
                null, null, new[] { MapSurfaceRole.SupportFloor }, null, limits);
            MapStoredSurfaceSource querySource = MapStoredSurfaceSource.Open(dir);
            MapPatchFindResult found = querySource.FindPatches(scope);
            Assert.Equal((MapFindStatus.Complete, 4), (found.Status, found.PagesRead));
            Assert.Equal(new[] { new MapPatchKey("ground", 0, 0), new("ridge", 0, 0) },
                found.Patches.Select(p => p.Key));
            Assert.All(found.Patches, p => Assert.Equal(MapPatchStatus.Present, p.Status));
            Assert.Empty(found.Unavailable);
            MapPatchFindResult cached = querySource.FindPatches(scope);
            Assert.Equal((MapFindStatus.Complete, 0), (cached.Status, cached.PagesRead));
            Assert.Equal(found.Patches.Select(p => p.Key), cached.Patches.Select(p => p.Key));

            MapPatchFindResult over = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                Limits = new MapQueryLimits(MaxCandidatePatches: 1),
            });
            Assert.Equal((MapFindStatus.CapacityExceeded, 0), (over.Status, over.Patches.Count));
            MapPatchFindResult exactBudget = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                Limits = new MapQueryLimits(MaxCandidatePatches: 2, MaxPageReads: 4),
            });
            Assert.Equal((MapFindStatus.Complete, 2, 4),
                (exactBudget.Status, exactBudget.Patches.Count, exactBudget.PagesRead));
            MapPatchFindResult pageOver = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                Limits = new MapQueryLimits(MaxPageReads: 3),
            });
            Assert.Equal((MapFindStatus.CapacityExceeded, 0), (pageOver.Status, pageOver.Patches.Count));
            Assert.InRange(pageOver.PagesRead, 0, 3);

            MapPatchFindResult capturedFar = captured.FindPatches(scope with
            {
                LocalMin = new Vector2(19201, 1),
                LocalMax = new Vector2(19202, 2),
            });
            Assert.Equal((MapFindStatus.Incomplete, 0, 0),
                (capturedFar.Status, capturedFar.Patches.Count, capturedFar.PagesRead));
            MapPatchFindResult empty = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                LocalMin = new Vector2(2561, 1),
                LocalMax = new Vector2(2562, 2),
            });
            Assert.Equal(MapFindStatus.Complete, empty.Status);
            Assert.Empty(empty.Patches);
            Assert.Empty(empty.Unavailable);
            Assert.NotEmpty(empty.KnownEmpty);
            MapPatchFindResult heightFiltered = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                MinY = 9f,
                MaxY = 11f,
            });
            Assert.Equal(MapFindStatus.Complete, heightFiltered.Status);
            Assert.Equal(new MapPatchKey("ground", 0, 0), Assert.Single(heightFiltered.Patches).Key);
            MapPatchFindResult roleFiltered = MapStoredSurfaceSource.Open(dir).FindPatches(scope with
            {
                Roles = new[] { MapSurfaceRole.Ceiling },
            });
            Assert.Equal(MapFindStatus.Complete, roleFiltered.Status);
            Assert.Empty(roleFiltered.Patches);
            Assert.NotEqual(scope.Digest, (scope with { Limits = new MapQueryLimits(MaxPageReads: 3) }).Digest);
            Assert.Throws<ArgumentException>(() => new MapSurfaceScope(WorldFrame.Origin,
                new Vector2(float.NaN, 1), new Vector2(62, 2), null, null,
                new[] { MapSurfaceRole.SupportFloor }, null, limits));
            Assert.Throws<ArgumentException>(() => new MapSurfaceScope(WorldFrame.Origin,
                new Vector2(63, 1), new Vector2(62, 2), null, null,
                new[] { MapSurfaceRole.SupportFloor }, null, limits));
            Assert.Throws<ArgumentException>(() => new MapSurfaceScope(WorldFrame.Origin,
                new Vector2(61, 1), new Vector2(62, 2), float.PositiveInfinity, null,
                new[] { MapSurfaceRole.SupportFloor }, null, limits));

            SurfaceStorageFixtures.DeletePayload(dir, new("far", -300, 0));
            SurfaceStorageFixtures.FlipPayloadByte(dir, new("far", 300, 0));
            MapStoredSurfaceSource fresh = MapStoredSurfaceSource.Open(dir);
            MapPatchRead missing = fresh.ReadPatch(new("far", -300, 0));
            MapPatchRead corrupt = fresh.ReadPatch(new("far", 300, 0));
            Assert.Equal((MapPatchStatus.Missing, MapPatchStatus.Corrupt), (missing.Status, corrupt.Status));
            Assert.Null(missing.Patch);
            Assert.Null(corrupt.Patch);
            SurfaceStorageFixtures.RewriteIndexSemanticDigest(dir, new("ground", 0, 0), new string('0', 64));
            byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
            var files = SurfaceStorageFixtures.SurfaceFiles(dir);
            MapPatchRead semanticMismatch = MapStoredSurfaceSource.Open(dir).ReadPatch(new("ground", 0, 0));
            Assert.Equal(MapPatchStatus.Corrupt, semanticMismatch.Status);
            Assert.Null(semanticMismatch.Patch);
            Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
            Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
