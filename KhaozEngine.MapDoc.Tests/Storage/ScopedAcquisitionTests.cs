using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Storage;

public sealed class ScopedAcquisitionTests
{
    static MapSurfaceScope FarScope(MapQueryLimits? limits = null) =>
        ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f, limits: limits);

    [Fact]
    public void Acquire_ResolvesFarAnchoredRecordsWithBoundedReads()
    {
        string dir = AnchorFixtures.FarAnchors();
        try
        {
            MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(dir);
            MapScopedSurfaces s = ScopeFixtures.Acquire(source, FarScope());
            Assert.Equal(MapAcquireStatus.Complete, s.Status);
            // Scope directories and indices cost four pages. The outside anchor adds one ground index.
            Assert.Equal(5, s.PagesRead);
            // Only ground(0,0) and roof(41,0) require additional anchor patch reads.
            Assert.Equal(2, s.RecordReads);
            Assert.Equal(new[] { new MapPatchKey("ground", 0, 0), new("ground", 40, 0), new("roof", 40, 0), new("roof", 41, 0) },
                s.Witness.Present.Select(p => p.Key).OrderBy(k => k));
            Assert.Contains(new MapRecordRef("door-top", new("roof", 41, 0)), s.Witness.Records);
            // This strip is reachable through the incident index, not through the excluded Walls list.
            Assert.Contains(new MapRecordRef("annex-east", new("roof", 41, 0)), s.Witness.Records);
            Assert.True(s.TryRecord(new("outside", new("ground", 0, 0)), out MapTopologyRecord? outside, out _) && outside is MapSpaceDoc);
            Assert.Equal((source.RootSha256, AnchorFixtures.FarFrame, true), (s.Identity.RootSha256, s.Identity.Frame, s.ReadWitness.Complete));
            Assert.Equal(s.Witness.Present, s.Identity.Patches);
            Assert.Empty(s.Witness.Unavailable);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Acquire_MissingAnchorIsUnavailableNotEmpty()
    {
        string dir = AnchorFixtures.FarAnchors();
        try
        {
            SurfaceStorageFixtures.DeletePayload(dir, new("roof", 41, 0));
            MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), FarScope());
            Assert.Equal(MapAcquireStatus.Incomplete, s.Status);
            Assert.Contains(new MapUnavailable("door-top", new("roof", 41, 0), MapPatchStatus.Missing), s.Witness.Unavailable);
            Assert.Equal((false, false, false), (s.Identity.Complete, s.ReadWitness.Complete, s.Witness.Complete));
            Assert.DoesNotContain(s.Witness.KnownEmpty, r => r.SurfaceId == "roof" && r.Slots.MinX <= 41 && r.Slots.MaxXExclusive > 41 && r.Slots.MinZ <= 0 && r.Slots.MaxZExclusive > 0);
            Assert.Contains("incomplete", Assert.Throws<MapDocumentException>(s.Identity.RequireComplete).Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        string corruptDir = AnchorFixtures.FarAnchors();
        try
        {
            SurfaceStorageFixtures.FlipPayloadByte(corruptDir, new("roof", 41, 0));
            MapScopedSurfaces corrupt = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(corruptDir), FarScope());
            Assert.Equal(MapAcquireStatus.Incomplete, corrupt.Status);
            Assert.Contains(new MapUnavailable("door-top", new("roof", 41, 0), MapPatchStatus.Corrupt), corrupt.Witness.Unavailable);
            Assert.Equal((false, false, false), (corrupt.Identity.Complete, corrupt.ReadWitness.Complete, corrupt.Witness.Complete));
            Assert.Contains("incomplete", Assert.Throws<MapDocumentException>(corrupt.Identity.RequireComplete).Message);
        }
        finally
        {
            Directory.Delete(corruptDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(4, 64, 8)]
    [InlineData(32, 1, 8)]
    [InlineData(32, 64, 0)]
    public void Acquire_PageRecordAndDepthBudgetsRefuse(int pages, int records, int depth)
    {
        string dir = AnchorFixtures.FarAnchors();
        try
        {
            MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir),
                FarScope(new MapQueryLimits(MaxPageReads: pages, MaxRecordReads: records, MaxRecordDepth: depth)));
            Assert.Equal((MapAcquireStatus.CapacityExceeded, 0), (s.Status, s.Witness.Present.Count));
            Assert.Empty(s.Witness.Records);
            Assert.Empty(s.Witness.KnownEmpty);
            Assert.Empty(s.Witness.Unavailable);
            Assert.Empty(s.Identity.Patches);
            Assert.Equal((false, false, false), (s.Identity.Complete, s.ReadWitness.Complete, s.Witness.Complete));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LatticeRanges_ArePositiveAreaHalfOpenAndExact()
    {
        var half = new MapLatticeFrame(new(1, 2), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var third = half with { CellUnitMetres = new(1, 3), HeightUnitMetres = new(1, 300) };
        MapExactXz[] r = MapLatticeRanges.CellRect(third, new MapPatchKey("third", 1, 0), 31);
        Assert.Equal(new[] { new MapExactXz(new(95, 3), new(0, 1)), new MapExactXz(new(32, 1), new(1, 3)) }, r);
        Assert.Equal(new MapCellRect(63, 0, 64, 1), MapLatticeRanges.CellRange(half, r[0], r[1]));
        Assert.Equal(new MapCellRect(-1, -1, 0, 0), MapLatticeRanges.CellRange(MapLatticeFrame.ImportedMetreCentimetre,
            new MapExactXz(new(-1, 2), new(0, 1)), new MapExactXz(new(0, 1), new(1, 2))));
    }

    [Fact]
    public void Acquire_EnumeratesExactlyThePositiveAreaBoundSlotsOfAMixedFootprint()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
        Assert.Equal(MapAcquireStatus.Complete, s.Status);
        Assert.Equal(new[] { new MapPatchKey("half", 0, 0), new("third", 1, 0) }, s.Witness.Present.Select(p => p.Key).OrderBy(k => k));
    }

    [Fact]
    public void Acquire_BoundSlotBudgetRefusesBeforeReading()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.MicroBound()), AcquisitionBoundFixtures.MicroScope);
        Assert.Equal((MapAcquireStatus.CapacityExceeded, 0), (s.Status, s.Witness.Present.Count));
        Assert.Contains("bound slots", s.Detail);
        Assert.Empty(s.Witness.Records);
        Assert.Empty(s.Witness.KnownEmpty);
        Assert.Empty(s.Witness.Unavailable);
        Assert.Empty(s.Identity.Patches);
        Assert.False(s.ReadWitness.Complete);
    }

    [Fact]
    public void Acquire_UnrepresentableBoundRangeIsNotRepresentableNotCapacity()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.AdversarialUnits()), AcquisitionBoundFixtures.AdversarialScope);
        Assert.Equal((MapAcquireStatus.NotRepresentable, 0, false), (s.Status, s.Witness.Present.Count, s.ReadWitness.Complete));
        Assert.Contains("overflow", s.Detail);
        Assert.Empty(s.Witness.Records);
        Assert.Empty(s.Witness.KnownEmpty);
        Assert.Empty(s.Witness.Unavailable);
        Assert.Empty(s.Identity.Patches);
    }

    [Fact]
    public void Acquisition_IsImmutableAgainstReturnedCopiesAndLaterSweeps()
    {
        string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
        try
        {
            var ground = new MapPatchKey("ground", 0, 0);
            var ridge = new MapPatchKey("ridge", 0, 0);
            var ground1 = new MapPatchKey("ground", 1, 0);
            MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), ScopeFixtures.Around(WorldFrame.Origin, 62, 2));
            Assert.Equal(MapAcquireStatus.Complete, s.Status);
            string digest = s.Identity.Digest;
            string root = s.Identity.RootSha256;
            string snapshot = s.ReadWitness.SnapshotId;
            MapSurfacePatch first = s.Patch(ground).Patch!;
            first.Heights[0] = 7;
            first.Cells[0] = default;
            first.SetPresent(0, 0, false);
            Assert.Throws<NotSupportedException>(() => ((IList<KeyValuePair<MapPatchKey, string>>)s.Witness.Present).Add(default));
            s.Patch(ground1).Patch!.CornerDependencies.Clear();
            MapSurfacePatch ridgeCopy = s.Patch(ridge).Patch!;
            MapBoundaryChain chain = Assert.IsType<MapBoundaryChain>(Assert.Single(ridgeCopy.Records));
            ((IList<MapChainVertex>)chain.Vertices)[0] = chain.Vertices[0] with { HeightUnits = 7 };
            ridgeCopy.Records.Clear();

            MapDocument whole = MapDocumentFile.LoadTiled(dir);
            whole.Surfaces.Patches[ground].Heights[0] = 1234;
            MapDocumentFile.SaveTiled(whole, dir);
            MapSurfacePatch second = s.Patch(ground).Patch!;
            Assert.NotSame(first, second);
            Assert.Equal(1000, second.Heights[0]);
            Assert.True(second.IsPresent(0, 0));
            Assert.Equal(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), second.Cells[0]);
            Assert.Equal(5, s.Patch(ground1).Patch!.CornerDependencies.Count);
            MapBoundaryChain acquiredChain = Assert.IsType<MapBoundaryChain>(Assert.Single(s.Patch(ridge).Patch!.Records));
            Assert.Equal(1500, acquiredChain.Vertices[0].HeightUnits);
            Assert.Equal((digest, digest, root, snapshot), (s.Identity.Digest, s.ReadWitness.ScopedDigest, s.Identity.RootSha256, s.ReadWitness.SnapshotId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
