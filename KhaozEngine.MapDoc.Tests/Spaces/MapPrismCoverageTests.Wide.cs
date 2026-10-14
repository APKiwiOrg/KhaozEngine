using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed partial class MapPrismCoverageTests
{
    [Theory]
    [InlineData(0.8f, false)]
    [InlineData(1.2f, true)]
    public void TransformedCanonicalPartition_RetainsEveryPositiveIntersection(float scale, bool far)
    {
        var fixture = PrefabFixtures.PlacedPrisms("dry-flooded", scale, far);
        // A world-aligned rational grid retains the wide-intersection control after T5-R1's parent correction.
        PrefabFixtures.AuthorWorldParent(fixture, fixture.Document.Prefabs!.Instances.Single().Transform,
            cellUnit: new(int.MaxValue - 1, int.MaxValue));
        var r = PrefabFixtures.Effective(fixture);
        var view = r.CompleteView();
        var prism = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapPrismDomain>().Single();
        var domain = MapPrismGeometry.Compile(view, prism);
        var ring = domain.Pieces.Single().Ring.Select(MapWideXz.From).ToArray();
        var faces = view.Witness.Present.Where(p => p.Key.SurfaceId == "exterior").SelectMany(p =>
        {
            var compiled = MapEffectiveGeometry.Patch(view, p.Key);
            return compiled.Faces.Select(f => new MapBoundFace(f.Key, compiled.ExactTriangle(f)));
        }).ToArray();
        var expected = faces.Select(f => MapEffectiveGeometry.Intersect(
            MapRefinementPolygon.Triangle(f.Triangle).Select(MapWideXz.From).ToArray(), ring))
            .Where(p => MapEffectiveGeometry.AreaWide(p).Sign > 0).ToArray();
        Assert.NotEmpty(expected);
        Assert.Contains(expected.SelectMany(p => p), p => !p.TryNarrow(out _));

        var partition = MapOccupiedPartitions.BuildWide(view, domain.Min, domain.Max, new(), false);
        Assert.Equal(MapRefinementStatus.Complete, partition.Status);
        var rooms = partition.Pieces.Where(p => p.SpaceId == "a/room").ToArray();
        Assert.Equal(expected.Length, rooms.Length);
        Assert.Equal(expected.Select(Key).Order(StringComparer.Ordinal), rooms.Select(p => Key(p.Polygon)).Order(StringComparer.Ordinal));
        Assert.All(rooms, p => Assert.Equal(Key(p.Polygon), string.Join("|", p.Polygon.Select(v => v.ToString()))));
        Assert.Equal(MapEffectiveGeometry.AreaWide(ring), rooms.Aggregate(default(MapWideRational),
            (sum, p) => sum.Add(MapEffectiveGeometry.AreaWide(p.Polygon))));
        Assert.Empty(MapSpaceCoverageValidator.Validate(view));

        var refined = MapCommonRefinement.RefineWide(faces, null, ring, new());
        Assert.Equal(MapRefinementStatus.Complete, refined.Status);
        Assert.Equal(expected.Length, refined.Faces.Count);
        Assert.Equal(MapEffectiveGeometry.AreaWide(ring), refined.LowerArea);
        Assert.Equal(MapRefinementStatus.NotRepresentable, refined.Narrow().Status);
        Assert.Equal(MapRefinementStatus.NotRepresentable, partition.Narrow().Status);
    }

    [Theory]
    [InlineData("uneven-parent", 0.8f, false)]
    [InlineData("uneven-parent", 1.2f, true)]
    [InlineData("nested", 0.8f, false)]
    [InlineData("nested", 1.2f, true)]
    [InlineData("reflex-storeys", 0.8f, false)]
    [InlineData("reflex-storeys", 1.2f, true)]
    public void WidePartitions_PublishAndAgreeWithScalarProvenance(string variant, float scale, bool far)
    {
        var fixture = PrefabFixtures.PlacedPrisms(variant, scale, far);
        var r = PrefabFixtures.Effective(fixture);
        var view = r.CompleteView();
        var extent = view.Scope.WorldRectangle();
        var result = MapOccupiedPartitions.BuildWide(view, new(extent.MinX, extent.MinZ), new(extent.MaxX, extent.MaxZ), new(), true);
        Assert.Equal(MapRefinementStatus.Complete, result.Status);
        Assert.NotEmpty(result.Pieces);
        Assert.Empty(MapSpaceCoverageValidator.Validate(view));
        foreach (var (x, y, z) in new[] { (1f, 1f, -1f), (2f, 1.5f, -2f), (3f, 1f, -3f), (2f, 4f, -2f) })
        {
            var point = PrefabFixtures.RoomProbe(r, x, y, z);
            var m = new MapSpaceMembership(view).QueryExact(point);
            Assert.Equal(MapMembershipStatus.Resolved, m.Status);
            var xz = MapWideXz.From(new(point.X, point.Z));
            var height = MapWideRational.From(point.Y);
            var pieces = result.Pieces.Where(p => MapWaterPolygons.RingContains(p.Polygon, xz) &&
                height.CompareTo(p.Bottom(xz)) >= 0 && (p.Top(xz) is not { } top || height.CompareTo(top) < 0)).ToArray();
            Assert.Single(pieces);
            Assert.Equal(m.SpaceId, pieces[0].SpaceId);
            Assert.Equal(m.Fragment!.Id, pieces[0].FragmentId);
            Assert.Equal(m.Fragment.Lower, pieces[0].LowerBoundary);
            Assert.Equal(m.Fragment.Upper, pieces[0].UpperBoundary);
            Assert.Equal(m.Fragment, MapSpaceIntervals.AtExact(view, point).Fragment);
        }
        var floorPoint = PrefabFixtures.RoomProbe(r, 1, 0, -1);
        var supportPoint = PrefabFixtures.RoomProbe(r, 1, 1, -1);
        var floorOwner = new MapSpaceMembership(view).QueryExact(floorPoint);
        Assert.Equal(MapMembershipStatus.Resolved, floorOwner.Status);
        var supportQuery = new MapSupportQuery(view);
        var support = supportQuery.SelectExact(new(MapEffectiveGeometry.Presentation(view, supportPoint), "a/floor", null, null, 0, 4, null), supportPoint);
        Assert.Equal(MapSupportStatus.Supported, support.Status);
        Assert.Equal("a/floor", support.Face!.Value.OwnerId);
        Assert.Equal(floorOwner.SpaceId, support.SpaceId);
        Assert.Equal(floorOwner.SpaceId, supportQuery.SampleSurfaceExact(supportPoint, "a/floor").SpaceId);
        Assert.Contains(result.Pieces, p => p.FragmentId == floorOwner.Fragment!.Id &&
            MapWaterPolygons.RingContains(p.Polygon, MapWideXz.From(new(floorPoint.X, floorPoint.Z))));
        var reload = PrefabFixtures.Effective(PrefabFixtures.ReloadPrisms(fixture));
        var repeated = MapOccupiedPartitions.BuildWide(reload.CompleteView(), new(extent.MinX, extent.MinZ), new(extent.MaxX, extent.MaxZ), new(), true);
        Assert.Equal(result.Pieces.Select(p => p.FragmentId + ":" + Key(p.Polygon)).Order(StringComparer.Ordinal),
            repeated.Pieces.Select(p => p.FragmentId + ":" + Key(p.Polygon)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, "a/room", "exterior-alias")]
    [InlineData(true, "prefab/a/Record/inner-room", "prefab/a/Record/room-alias")]
    public void AliasParents_ResolveOccupancyAndKeepAuthoredProvenance(bool roomAlias, string child, string parent)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.AliasParent(roomAlias));
        var view = r.CompleteView();
        var p = PrefabFixtures.Point(r, 2, 1.5f, -2);
        var m = new MapSpaceMembership(view).Query(p);
        Assert.Equal(MapMembershipStatus.Resolved, m.Status);
        Assert.Equal(child, m.SpaceId);
        Assert.Equal(parent, m.ParentSpaceIds[0]);
        Assert.Equal(m.Fragment, MapSpaceIntervals.At(view, p).Fragment);
        var intervals = MapSpaceIntervals.Column(view, p);
        Assert.All(intervals, i => Assert.Equal(MapMembershipStatus.Resolved, i.Status));
        Assert.Contains(intervals, i => i.SpaceId == child && i.Fragment == m.Fragment);
        Assert.Empty(MapSpaceCoverageValidator.Validate(view));
        var result = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), new(), true);
        Assert.Equal(MapRefinementStatus.Complete, result.Status);
        var pieces = result.Pieces.Where(piece => piece.SpaceId == child).ToArray();
        Assert.NotEmpty(pieces);
        Assert.All(pieces, piece =>
        {
            Assert.Equal(parent, piece.AuthoredParent!.Id);
            Assert.Contains(parent, piece.Path);
        });
        Assert.Contains(pieces, piece => piece.FragmentId == m.Fragment!.Id);
    }

    [Theory]
    [InlineData("pairs")]
    [InlineData("faces")]
    [InlineData("vertices")]
    public void WidePartitionLimits_RefuseWithoutPartialPieces(string budget)
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Create());
        var limits = budget switch
        {
            "pairs" => new MapRefinementLimits(MaxPairChecks: 0),
            "faces" => new MapRefinementLimits(MaxRefinementFaces: 0),
            _ => new MapRefinementLimits(MaxRefinementVertices: 0),
        };
        var result = MapOccupiedPartitions.BuildWide(r.CompleteView(), new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), limits, true);
        Assert.Equal(MapRefinementStatus.CapacityExceeded, result.Status);
        Assert.Empty(result.Pieces);
        Assert.Empty(result.Narrow().Pieces);
    }

    [Fact]
    public void WidePartitionIncompleteAcquisition_IsMissingGeometry()
    {
        var r = PrefabFixtures.Effective(PrefabFixtures.Cave("transformed"));
        var view = PrefabFixtures.AcquireWithout(r, "parent-floor");
        var result = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(-4, 1)), new(new(4, 1), new(0, 1)), new(), true);
        Assert.Equal(MapRefinementStatus.MissingGeometry, result.Status);
        Assert.Empty(result.Pieces);
        Assert.False(result.Witness.Complete);
    }

    // Independent canonical ordering of the exact polygons used in equality assertions.
    static string Key(IReadOnlyList<MapWideXz> points)
    {
        var ordered = points.ToList();
        if (MapEffectiveGeometry.AreaWide(ordered).Sign < 0) ordered.Reverse();
        var first = ordered.Select((point, index) => (point, index)).OrderBy(p => p.point.Z).ThenBy(p => p.point.X).First().index;
        return string.Join("|", Enumerable.Range(0, ordered.Count).Select(i => ordered[(first + i) % ordered.Count].ToString()));
    }
}
