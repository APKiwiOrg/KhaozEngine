using System;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed class MapPrismMembershipTests
{
    [Fact]
    public void RoomIsOccupied_ExteriorIsOnlyItsParent()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var p = PrefabFixtures.Point(r, 2, 1, -2);
        var m = new MapSpaceMembership(r.CompleteView()).Query(p);
        Assert.Equal(MapMembershipStatus.Resolved, m.Status);
        Assert.Equal("a/room", m.SpaceId);
        Assert.Equal(new[] { "exterior" }, m.ParentSpaceIds);
        Assert.Contains("exterior", m.DomainKeys);
        var interval = MapSpaceIntervals.At(r.CompleteView(), p);
        Assert.Equal(m.SpaceId, interval.SpaceId);
        Assert.Equal(m.Fragment, interval.Fragment);
        Assert.NotNull(m.Fragment);
    }

    [Fact]
    public void SharedEdgesReflexCornersAndStoreys_HaveOneOwner()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("reflex-storeys"));
        foreach (var p in PrefabFixtures.RoomBoundaryPoints(r))
        {
            var m = new MapSpaceMembership(r.CompleteView()).QueryExact(p);
            Assert.Equal(MapMembershipStatus.Resolved, m.Status);
            Assert.Equal(PrefabFixtures.ExpectedBoundaryOwner(r, p), m.SpaceId);
            Assert.Equal(m.Fragment, MapSpaceIntervals.AtExact(r.CompleteView(), p).Fragment);
        }
        var atThree = MapSpaceIntervals.At(r.CompleteView(), PrefabFixtures.Point(r, 2, 3, -2));
        Assert.Equal("prefab/a/Record/upper-room", atThree.SpaceId);
    }

    [Fact]
    public void TruePeerOverlap_IsAmbiguousAndCannotPublish()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("peer-overlap"));
        Assert.Equal(MapMembershipStatus.Ambiguous,
            new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
        Assert.Contains(MapSpaceCoverageValidator.Validate(r.CompleteView()),
            e => e.Contains("peer occupied overlap", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("prism")]
    [InlineData("parent-floor")]
    [InlineData("remote-anchor")]
    public void MissingParentOrPrism_IsMissingGeometry(string fact)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("transformed"));
        Assert.Equal(KhaozEngine.MapDoc.Storage.MapAcquireStatus.Complete, PrefabFixtures.AcquireAll(r).Status);
        var view = PrefabFixtures.AcquireWithout(r, fact);
        Assert.Contains(fact == "prism" ? "room-domain" : fact, PrefabFixtures.UnavailableIds(view));
        Assert.True(PrefabFixtures.UnrelatedFactsRemainReadable(view));
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(view).QueryExact(PrefabFixtures.RoomProbe(r, 2, 1, -2)).Status);
    }

    [Fact]
    public void PrismOverUnevenParent_UsesCanonicalIntersection()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("uneven-parent"));
        var view = r.CompleteView();
        Assert.Equal("a/room", MapSpaceIntervals.At(view, PrefabFixtures.Point(r, 1, 1, -2)).SpaceId);
        Assert.Equal(MapMembershipStatus.Outside, new MapSpaceMembership(view).Query(PrefabFixtures.Point(r, 3, 0.1f, -2)).Status);
        Assert.Equal(new MapExactValue(1, 4), MapSpaceIntervals.At(view, PrefabFixtures.Point(r, 3, 1, -2)).Lower);
    }

    [Fact]
    public void Column_PartitionsParentAroundNestedStoreys()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("dry-flooded"));
        var view = r.CompleteView();
        var intervals = MapSpaceIntervals.Column(view, PrefabFixtures.Point(r, 2, 1, -2));
        Assert.Equal(new[] { "exterior", "a/room", "exterior" }, intervals.Select(i => i.SpaceId));
        Assert.Equal(new[] { new MapExactValue(-2, 1), new(0, 1), new(3, 1) }, intervals.Select(i => i.Lower));
        Assert.NotEqual(intervals[0].Fragment!.Id, intervals[2].Fragment!.Id);
        foreach (var pair in intervals.Zip(new[] { -1f, 1f, 4f }))
            Assert.Equal(pair.First.Fragment, MapSpaceIntervals.At(view, PrefabFixtures.Point(r, 2, pair.Second, -2)).Fragment);
        var again = PrefabFixtures.Effective(PrefabFixtures.ReloadPrisms(PrefabFixtures.Cave("dry-flooded")));
        Assert.Equal(intervals.Select(i => i.Fragment!.Id), MapSpaceIntervals.Column(again.CompleteView(), PrefabFixtures.Point(again, 2, 1, -2)).Select(i => i.Fragment!.Id));
    }

    [Fact]
    public void RoomPortalPlane_UsesPrismOwnershipAfterYaw()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.PlacedPrisms("west-portal", 1.2f, false));
        var view = r.CompleteView();
        var first = PrefabFixtures.RoomProbe(r, 0, 1, 0);
        var last = PrefabFixtures.RoomProbe(r, 0, 1, -4);
        var point = new MapExactPoint(first.X.Add(last.X).Divide(new(2, 1)), first.Y, first.Z.Add(last.Z).Divide(new(2, 1)));
        var membership = new MapSpaceMembership(view);
        var portal = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapCavePortal>().Single();
        Assert.Equal("exterior", portal.ToSpace.Id);
        Assert.True(membership.OnPortal(portal, new(point.X, point.Z)));
        Assert.Equal("a/room", membership.QueryExact(point).SpaceId);
        Assert.Equal("a/room", MapSpaceIntervals.AtExact(view, point).SpaceId);
        Assert.Equal(MapMembershipStatus.Resolved, membership.QueryExact(point, Array.Empty<MapBoundFaceDemand>(), null, out var context).Status);
        Assert.Equal(MapMembershipStatus.Resolved, membership.Column(MapEffectiveGeometry.Presentation(view, point), point, context!, out var column).Status);
        Assert.Equal("a/room", column.Single(p => point.Y.CompareTo(p.Lower) >= 0 && (p.Upper is not { } upper || point.Y.CompareTo(upper) < 0)).SpaceId);
    }

    [Fact]
    public void LatticePortalPlane_KeepsReleasedToSpaceOwnership()
    {
        var doc = CaveFixtures.RampChamberShaft();
        var view = KhaozEngine.MapDoc.Storage.MapScopedSurfaces.CompleteView(doc.Surfaces);
        var p = new MapFramePoint(view.Scope.Frame, new(4, 8.5f, 3));
        Assert.Equal("outside", new MapSpaceMembership(view).Query(p).SpaceId);
        Assert.Equal("outside", MapSpaceIntervals.At(view, p).SpaceId);
    }

    [Fact]
    public void RoomSupport_UsesPhysicalFacesInItsOccupiedFragment()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var view = r.CompleteView();
        var p = PrefabFixtures.Point(r, 2, 1, -2);
        var support = new MapSupportQuery(view).Select(new(p, null, null, null, 0, 4, null));
        Assert.Equal(MapSupportStatus.Supported, support.Status);
        Assert.Equal("a/floor", support.Face!.Value.OwnerId);
        Assert.Equal("a/room", support.SpaceId);
        Assert.Equal("a/room", new MapSupportQuery(view).SampleSurface(PrefabFixtures.Point(r, 2, 4, -2), "a/floor").SpaceId);
    }

    [Fact]
    public void OccupiedPieces_AndPointColumnsShareStableFragments()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var view = r.CompleteView();
        var pieces = MapOccupiedPartitions.Pieces(view, "exterior", new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), new());
        var columns = MapSpaceIntervals.Column(view, PrefabFixtures.Point(r, 2, 1, -2));
        Assert.Equal(columns.Where(i => i.SpaceId == "exterior").Select(i => i.Fragment!.Id).Order(), pieces.Select(p => p.FragmentId).Distinct().Order());
        var room = MapOccupiedPartitions.Pieces(view, "a/room", new(new(1, 1), new(-3, 1)), new(new(3, 1), new(-1, 1)), new());
        Assert.NotEmpty(room);
        Assert.All(room, p => Assert.Equal(columns[1].Fragment!.Id, p.FragmentId));
        Assert.All(room, p => Assert.Empty(p.LowerFaces));
    }

    [Fact]
    public void SelectedCaveModel_ComposesFloorsCeilingsAndVolumes()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("stacked"));
        var view = r.CompleteView();
        foreach (var (id, y, floor, ceiling) in new[] { ("deep", -55f, new MapExactValue(-119, 2), new MapExactValue(-99, 2)),
            ("high", -35f, new MapExactValue(-79, 2), new MapExactValue(-59, 2)) })
        {
            var point = PrefabFixtures.Point(r, 2, y, -2);
            var m = new MapSpaceMembership(view).Query(point);
            Assert.Equal(id + "-room", m.SpaceId);
            Assert.Equal(new[] { id + "-cave" }, m.ParentSpaceIds);
            var column = MapSpaceIntervals.Column(view, point).Where(i => i.SpaceId!.StartsWith(id, StringComparison.Ordinal)).ToArray();
            Assert.Equal(floor, column[0].Lower);
            Assert.Equal(ceiling, column[^1].Upper);
            Assert.Equal(new MapExactValue(6, 1), column[1].Upper!.Value.Subtract(column[1].Lower));
        }
        Assert.Empty(MapSpaceCoverageValidator.Validate(view));
    }

    [Theory]
    [InlineData(0.8f, false)]
    [InlineData(1.2f, false)]
    [InlineData(1.2f, true)]
    public void FreeYawReflexCorners_UseWorldOwnership(float scale, bool far)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.PlacedPrisms("reflex-storeys", scale, far));
        var view = r.CompleteView();
        foreach (var point in PrefabFixtures.RoomBoundaryPoints(r))
        {
            var m = new MapSpaceMembership(view).QueryExact(point);
            Assert.Equal(MapMembershipStatus.Resolved, m.Status);
            Assert.Equal(PrefabFixtures.ExpectedBoundaryOwner(r, point), m.SpaceId);
            Assert.Equal(m.Fragment, MapSpaceIntervals.AtExact(view, point).Fragment);
        }
    }

    [Theory]
    [InlineData(0.8f, false)]
    [InlineData(1.2f, false)]
    [InlineData(1.2f, true)]
    public void TwoInstances_SharedFrozenWallHasOneOwner(float scale, bool far)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.AdjacentInstances(scale, far));
        var view = r.CompleteView();
        var a = PrefabFixtures.RoomProbe(r, 4, 1, -4); var b = PrefabFixtures.RoomProbe(r, 4, 1, 0);
        var p = new MapExactPoint(a.X.Add(b.X).Divide(new(2, 1)), a.Y, a.Z.Add(b.Z).Divide(new(2, 1)));
        Assert.Equal("b/room", new MapSpaceMembership(view).QueryExact(p).SpaceId);
        Assert.Equal("b/room", MapSpaceIntervals.AtExact(view, p).SpaceId);
    }

    [Fact]
    public void MissingPhysicalFloor_DoesNotFabricatePrismSupport()
    {
        var f = PrefabFixtures.Cave("floor-hole");
        var r = PrefabFixtures.Effective(f);
        var p = PrefabFixtures.Point(r, 2, 1, -2);
        var view = r.CompleteView();
        Assert.Equal("a/room", new MapSpaceMembership(view).Query(p).SpaceId);
        Assert.Equal(MapSupportStatus.NoSupport, new MapSupportQuery(view).Select(new(p, null, null, null, 0, 4, null)).Status);
    }

    [Fact]
    public void PrismGeometryBudget_RefusesBeforeUnboundedCompilation()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("oversized-prism"));
        Assert.Equal(MapMembershipStatus.CapacityExceeded, new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
    }

    [Fact]
    public void RepackedSource_PreservesLogicalFragmentIds()
    {
        var f = PrefabFixtures.Create();
        var original = PrefabFixtures.Effective(f);
        var stored = PrefabFixtures.Stored(f.Document);
        var packed = KhaozEngine.MapDoc.Prefabs.MapPrefabResolver.Expand(f.Document, f.Assets, new(), null, stored);
        Assert.Equal(MapSpaceIntervals.Column(original.CompleteView(), PrefabFixtures.Point(original, 2, 1, -2)).Select(i => i.Fragment!.Id),
            MapSpaceIntervals.Column(packed.CompleteView(), PrefabFixtures.Point(packed, 2, 1, -2)).Select(i => i.Fragment!.Id));
    }

    [Theory]
    [InlineData("uneven-parent", "canonical-floor")]
    [InlineData("uneven-reverse", "prism-lower")]
    public void EqualPrismAndParentPlanes_UseWorldDirectionForFragmentOwnership(string variant, string role)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave(variant));
        var view = r.CompleteView();
        var point = new MapExactPoint(new(2, 1), new(1, 1), new(-2, 1));
        var m = new MapSpaceMembership(view).QueryExact(point);
        Assert.Equal(role, m.Fragment!.Lower.Role);
        var pieces = MapOccupiedPartitions.Pieces(view, "a/room", new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), new());
        var containing = pieces.Where(p => MapWaterPolygons.RingContains(p.Polygon, MapWideXz.From(new(point.X, point.Z)))).ToArray();
        Assert.Single(containing);
        Assert.Equal(m.Fragment.Id, containing[0].FragmentId);
    }

    [Fact]
    public void GrandchildPrism_CarvesOnlyOverlappingAncestorSlabs()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("nested"));
        var view = r.CompleteView();
        var column = MapSpaceIntervals.Column(view, PrefabFixtures.Point(r, 2, 1.5f, -2));
        Assert.Equal(new[] { "exterior", "a/room", "prefab/a/Record/inner-room", "a/room", "exterior" }, column.Select(i => i.SpaceId));
        Assert.Equal(new[] { "a/room", "exterior" }, new MapSpaceMembership(view).Query(PrefabFixtures.Point(r, 2, 1.5f, -2)).ParentSpaceIds);
        var pieces = column.Select(i => i.SpaceId!).Distinct().SelectMany(space => MapOccupiedPartitions.Pieces(view, space,
            new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), new())).ToArray();
        var xz = new MapExactXz(new(2, 1), new(-2, 1));
        foreach (var y in new[] { -1f, 0.5f, 1.5f, 2.5f, 4f })
        {
            var height = MapExactValue.FromSingle(y);
            var at = pieces.Where(p => MapWaterPolygons.RingContains(p.Polygon, MapWideXz.From(xz)) &&
                MapWideRational.From(height).CompareTo(p.Bottom(MapWideXz.From(xz))) >= 0 && (p.Top(MapWideXz.From(xz)) is not { } top || MapWideRational.From(height).CompareTo(top) < 0)).ToArray();
            Assert.Single(at);
            Assert.Equal(MapSpaceIntervals.At(view, PrefabFixtures.Point(r, 2, y, -2)).Fragment!.Id, at[0].FragmentId);
        }
    }

    [Theory]
    [InlineData(-4f, "a/room")]
    [InlineData(0f, "exterior")]
    public void CollinearVertices_KeepTheWorldEdgeOwner(float z, string owner)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("collinear"));
        Assert.Equal(owner, new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.Point(r, 2, 1, z)).SpaceId);
    }

    [Fact]
    public void GeometryDrift_ChangesBoundaryDigestWithoutReallocatingFragment()
    {
        var a = PrefabFixtures.Effective(PrefabFixtures.Create());
        var b = PrefabFixtures.Effective(PrefabFixtures.Cave("geometry-drift"));
        var original = MapSpaceIntervals.At(a.CompleteView(), PrefabFixtures.Point(a, 2, 1, -2)).Fragment!;
        var changed = MapSpaceIntervals.At(b.CompleteView(), PrefabFixtures.Point(b, 2, 1, -2)).Fragment!;
        Assert.Equal(original.Id, changed.Id);
        Assert.NotEqual(original.Lower.SemanticDigest, changed.Lower.SemanticDigest);
    }

    [Fact]
    public void AcquiredRecordCopies_DetachPrismFootprintCollections()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var view = r.CompleteView();
        var reference = new MapRecordRef("a/room-domain", new("a/floor", 0, 0));
        Assert.True(view.TryRecord(reference, out var first, out _));
        ((MapWaterRect[])((MapPrismDomain)first!).Footprint.Rects)[0] = new(20, 20, 24, 24);
        Assert.True(view.TryRecord(reference, out var second, out _));
        Assert.Equal(new MapWaterRect(0, 0, 4, 4), ((MapPrismDomain)second!).Footprint.Rects.Single());
        Assert.Equal("a/room", new MapSpaceMembership(view).Query(PrefabFixtures.Point(r, 2, 1, -2)).SpaceId);
    }

    [Fact]
    public void RoomWithASecondLatticeDomain_RefusesCompetingGeometryAuthority()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("second-room-domain"));
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(r.CompleteView()).Query(PrefabFixtures.Point(r, 2, 1, -2)).Status);
    }
}
