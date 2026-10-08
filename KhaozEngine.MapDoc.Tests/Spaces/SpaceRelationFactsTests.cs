using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SpaceRelationFactsTests
{
    [Fact]
    public void ConnectedDistinctSupports_AreTheSameSpace()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        MapRelationResult r = RelationFixtures.Relate(cave, new(1.5f, 10.5f, 1.5f), new(3.5f, 9.5f, 2.5f));
        Assert.Equal((MapRelationStatus.Resolved, MapGeometricRelation.SameSpace), (r.Status, r.Relation));
        Assert.Equal(("outer", "cave-floor"), (SupportFixtures.Select(cave, SupportFixtures.Request(1.5f, 10.5f, 1.5f, 0.5f, 1f)).Face!.Value.OwnerId,
                                               SupportFixtures.Select(cave, SupportFixtures.Request(3.5f, 9.5f, 2.5f, 0.5f, 1f)).Face!.Value.OwnerId));
    }
    [Fact]
    public void StackedSpacesSharingXz_ConnectOnlyThroughTheirPortal()
    {
        MapRelationResult r = RelationFixtures.Relate(CaveFixtures.RampChamberShaft(), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), out MapScopedSurfaces acquired);
        Assert.Equal(("outside", "chamber", MapGeometricRelation.ConnectedThroughPortals), (r.MembershipA.SpaceId, r.MembershipB.SpaceId, r.Relation));
        Assert.Equal((new Vector3(2.5f, 10.5f, 4.5f), new Vector3(2.5f, 5.5f, 4.5f)), (r.A.Local, r.B.Local));
        MapPortalFact mouth = Assert.Single(r.Path);
        Assert.Equal((new MapRecordRef("mouth", new("cave-floor", 0, 0)), MapPortalStateSource.AuthoredOpen), (mouth.Record, mouth.State));
        Assert.Equal(Enumerable.Range(2, 5).Select(x => (new MapExactPoint(new(x, 1), new(8, 1), new(3, 1)), (MapExactPoint?)new MapExactPoint(new(x, 1), new(9, 1), new(3, 1)))),
            mouth.Geometry.Columns.Select(c => (c.Bottom, c.Top)));
        Assert.Equal((8.0f, (float?)9.0f, (float?)1.0f, 4.0f), (mouth.Summary.BottomY, mouth.Summary.TopY, mouth.Summary.MinClearHeight, mouth.Summary.Width));
        Assert.All(mouth.Geometry.Provenance, p => Assert.Contains(p, acquired.Witness.Records));
        Assert.Equal((MapPhysicalCertainty.NotEvaluated, MapPhysicalCertainty.NotEvaluated), (r.Occlusion, r.Clearance));
        Assert.Equal(MapGeometricRelation.NotConnectedWithinBound,
            RelationFixtures.Relate(CaveFixtures.RampChamberShaft(), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), new MapRelationQuery(MaxPortalHops: 0)).Relation);
    }
    [Fact]
    public void ShaftConnectsThroughTheVerticalLink()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        MapRelationResult r = RelationFixtures.Relate(cave, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f));
        Assert.Equal(MapGeometricRelation.ConnectedThroughVerticalLink, r.Relation);
        Assert.Equal(new[] { "shaft-top", "shaft-bottom" }, r.Path.Select(p => p.Record.Id));
        Assert.All(r.Path, p => Assert.Equal((new MapRecordRef("shaft-link", new("cave-floor", 0, 0)), 8), (p.ViaLink!, p.Geometry.PlaneTriangles.Count)));
        Assert.Equal(MapGeometricRelation.NotConnectedWithinBound,
            RelationFixtures.Relate(cave, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f), new MapRelationQuery(IncludeVerticalLinks: false)).Relation);
    }
    [Fact]
    public void FarAnchoredPortal_SuppliesExactApertureFromAnOutOfWindowChain()
    {
        string dir = AnchorFixtures.FarAnchors();
        MapSurfaceScope scope = ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f);
        MapRelationResult r = RelationFixtures.RelateIn(MapStoredSurfaceSource.Open(dir), scope, new(0.5f, 10.5f, 1.5f), new(2.5f, 12f, 1.5f));
        Assert.Equal(MapGeometricRelation.ConnectedThroughPortals, r.Relation);
        MapPortalFact door = Assert.Single(r.Path);
        Assert.Equal("door", door.Record.Id);
        Assert.Equal(new[] { 0, 4 }.Select(z => (new MapExactPoint(new(2562, 1), new(10, 1), new(z, 1)), (MapExactPoint?)new MapExactPoint(new(2562, 1), new(14, 1), new(z, 1)))),
            door.Geometry.Columns.Select(c => (c.Bottom, c.Top)));
        Assert.Contains(new MapRecordRef("door-top", new("roof", 41, 0)), door.Geometry.Provenance);
        SurfaceStorageFixtures.DeletePayload(dir, new("roof", 41, 0));
        MapRelationResult missing = RelationFixtures.RelateIn(MapStoredSurfaceSource.Open(dir), scope, new(0.5f, 10.5f, 1.5f), new(2.5f, 12f, 1.5f));
        Assert.Equal((MapRelationStatus.MissingGeometry, MapGeometricRelation.Undetermined), (missing.Status, missing.Relation));
        Assert.Contains("door-top", missing.Detail);
    }
    [Fact]
    public void MissingAndAmbiguousScopesStayUnresolved()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        var filtered = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(cave), new MapPatchKey("cave-ceiling", 0, 0));
        MapRelationResult m = RelationFixtures.Relate(cave, new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), source: filtered);
        Assert.Equal((MapRelationStatus.MissingGeometry, MapGeometricRelation.Undetermined), (m.Status, m.Relation));
        Assert.Equal(MapRelationStatus.Ambiguous, RelationFixtures.Relate(CaveFixtures.WithPeerChamber(null), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f)).Status);
    }
    [Fact]
    public void OneAcquisition_SharesOneWitnessAcrossMembershipSupportAndRelations()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(CaveFixtures.RampChamberShaft()), ScopeFixtures.Around(WorldFrame.Origin, 3, 4, half: 6));
        var p = new MapFramePoint(WorldFrame.Origin, new(2.5f, 5.5f, 4.5f));
        Assert.Same(s.ReadWitness, new MapSpaceMembership(s).Query(p).Witness);
        Assert.Same(s.ReadWitness, new MapSupportQuery(s).Select(new MapSupportRequest(p, null, null, null, 0.5f, 1f, null)).Witness);
        Assert.Same(s.ReadWitness, new MapSpaceRelations(s).Relate(p, new MapFramePoint(WorldFrame.Origin, new(2.5f, 10.5f, 4.5f)), new()).Witness);
        Assert.Throws<ArgumentException>(() => new MapSpaceMembership(s).Query(new MapFramePoint(new WorldFrame(1, 0), p.Local)));
    }
    [Fact]
    public void Witness_IsStableForOneSnapshotAndChangesWithGeometry()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        MapDocumentSurfaceSource src = MapDocumentSurfaceSource.Capture(cave);
        MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 3, 4, half: 6);
        Assert.Equal(ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest, ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest);
        cave.Surfaces.Patches[new("cave-ceiling", 0, 0)].Heights[0] = 905;
        Assert.NotEqual(ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest,
                        ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(cave), scope).ReadWitness.ScopedDigest);
    }
    [Fact]
    public void RelationFacts_ExposeNoEligibilityPolicy()
    {
        string[] banned = { "CanHear", "CanSee", "CanInteract", "IsAudible", "IsVisible", "FloorId" };
        var members = typeof(MapSpaceRelations).Assembly.GetExportedTypes()
            .Where(t => t.Namespace is "KhaozEngine.MapDoc.Spaces" or "KhaozEngine.MapDoc.Support")
            .SelectMany(t => t.GetMembers()).Select(m => m.Name);
        Assert.DoesNotContain(members, banned.Contains);
    }
    [Fact]
    public void QueryResults_AreFactoryOnly()
    {
        foreach (Type t in new[] { typeof(MapMembershipResult), typeof(MapSupportResult), typeof(MapSupportCandidateSet), typeof(MapRelationResult) })
        {
            Assert.Empty(t.GetConstructors());
            Assert.DoesNotContain(t.GetProperties(), p => p.SetMethod is { IsPublic: true });
        }
    }
}
