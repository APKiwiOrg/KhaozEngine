using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerMembershipTests
{
    [Fact]
    public void UndergroundMarker_ExplicitHeightAndLayerSurviveReload()
    {
        foreach (bool tiled in new[] { false, true })
        {
            MapEffectiveResources resources = MarkerFixtures.Effective(MarkerFixtures.Reload(MarkerFixtures.Cave(), tiled));
            MapEffectiveMarker underground = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "underground");
            MapMembershipResult membership = MapMarkerSpaces.Query(resources.CompleteView(), underground);

            Assert.Equal(-57.75f, underground.Position.Y.ToSingle());
            Assert.Equal(MarkerFixtures.Exact(2, -57.75f, -2), underground.Position);
            Assert.Equal(new MapMarkerHeightProvenance(MapMarkerHeightMode.Explicit, null, null, null, null, null), underground.HeightProvenance);
            Assert.Empty(resources.MarkerSupports);
            Assert.Equal(MapMembershipStatus.Resolved, membership.Status);
            Assert.Equal("deep-room", membership.SpaceId);
            Assert.Equal(new[] { "deep-cave" }, membership.ParentSpaceIds);
        }
    }

    [Fact]
    public void LocalCaveMarker_TransformsOnceAndKeepsVolumeAssociation()
    {
        MapEffectiveResources resources = MarkerFixtures.Effective(MarkerFixtures.LocalCave());
        MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "a/room-marker");
        MapMembershipResult membership = MapMarkerSpaces.Query(resources.CompleteView(), marker);

        Assert.Equal(new MapExactPoint(new(-33, 4), new(-448, 1), new(25, 8)), marker.Position);
        Assert.Equal("a", marker.InstanceId);
        Assert.Equal(0.25f, marker.YawRadians);
        Assert.Equal(MapMembershipStatus.Resolved, membership.Status);
        Assert.Equal("a/room", membership.SpaceId);
        Assert.Equal(new[] { "exterior" }, membership.ParentSpaceIds);
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(1.2f)]
    public void LocalCaveMarker_FreeYawAndScale_UseIndependentlyQuantizedPoints(float scale)
    {
        var frame = new MapTransform(new(-12.25f, -450, 7.125f), 0.371f, scale);
        MapEffectiveResources resources = MarkerFixtures.Effective(MarkerFixtures.LocalCave(frame));
        MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "a/room-marker");

        // Local (2, 1, -2) through a binary64 yaw and uniform scale, then the 2^-20 metre freeze.
        var (sin, cos) = Math.SinCos((double)0.371f);
        double x = -12.25 + scale * (cos * 2 + sin * -2), z = 7.125 + scale * (-sin * 2 + cos * -2);
        Assert.Equal(new MapExactPoint(Quantized(x), MapExactValue.FromSingle((float)(Math.Round((-450d + scale * 1d) * 1048576, MidpointRounding.ToEven) / 1048576)),
            Quantized(z)), marker.Position);
        Assert.Equal((float)(0.25f + (double)0.371f), marker.YawRadians);
        MapMembershipResult membership = MapMarkerSpaces.Query(resources.CompleteView(), marker);
        Assert.Equal((MapMembershipStatus.Resolved, "a/room"), (membership.Status, membership.SpaceId));
        Assert.Equal(new[] { "exterior" }, membership.ParentSpaceIds);
    }

    [Fact]
    public void SpacelessPoint_IsOutsideAndRefusalsKeepTheirStatus()
    {
        MarkerFixture stacked = MarkerFixtures.Stacked();
        stacked.Document.BuildingIntent = new(1, [], [], [], stacked.Document.BuildingIntent!.Markers.Append(
            MapMarkerHeights.Apply(new(1, "between", "between-name", new Vector3(0.5f, 0, 0.5f), 0, "guard-role", true, Array.Empty<string>()), new(7.5f, null))).ToArray());
        MapEffectiveResources resources = MarkerFixtures.Effective(stacked);
        MapEffectiveMarker between = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "between");
        MapMembershipResult outside = MapMarkerSpaces.Query(resources.CompleteView(), between);
        Assert.Equal(MapMembershipStatus.Outside, outside.Status);
        Assert.Null(outside.SpaceId);
        Assert.Empty(outside.ParentSpaceIds);

        // Missing geometry: the view does not hold the marker's column.
        MapEffectiveResources cave = MarkerFixtures.Effective(MarkerFixtures.LocalCave());
        MapEffectiveMarker local = MapEffectiveAnnotations.Markers(cave).Single(m => m.Id == "a/room-marker");
        MapSurfaceScope whole = PrefabFixtures.Scope(cave);
        var elsewhere = cave.Acquire(whole with { LocalMin = whole.LocalMax - new Vector2(1, 1) });
        Assert.Equal(MapMembershipStatus.MissingGeometry, MapMarkerSpaces.Query(elsewhere, local).Status);

        // Capacity: the acquisition refuses its candidate budget.
        var tight = cave.Acquire(whole with { Limits = new MapQueryLimits(MaxCandidatePatches: 1) });
        Assert.Equal(MapMembershipStatus.CapacityExceeded, MapMarkerSpaces.Query(tight, local).Status);

        // Ambiguity: two authored peer rooms hold the point.
        PrefabFixture peers = PrefabFixtures.Cave("peer-overlap");
        peers.Document.BuildingIntent = new(1, [], [], [], new[]
        { MapMarkerHeights.Apply(new(1, "peer", "peer-name", new Vector3(2, 0, -2), 0, "guard-role", true, Array.Empty<string>()), new(1f, null)) })
        { Storeys = peers.Document.BuildingIntent?.Storeys ?? [] };
        MapEffectiveResources peerResources = PrefabFixtures.Effective(peers);
        MapEffectiveMarker peer = Assert.Single(MapEffectiveAnnotations.Markers(peerResources), m => m.Id == "peer");
        Assert.Equal(MapMembershipStatus.Ambiguous, MapMarkerSpaces.Query(peerResources.CompleteView(), peer).Status);

        // Nonrepresentability: the released final-denominator point over a sloped parent at the origin.
        PrefabFixture sloped = PrefabFixtures.Sloped(false, 1.2f);
        var origin = sloped.Document.Prefabs!.Instances.Single() with { Transform = new(Vector3.Zero, 0.371f, 1.2f) };
        sloped.Document.Prefabs = new(1, 1, new[] { origin }, []);
        PrefabFixtures.AuthorWorldParent(sloped, origin.Transform, (x, _) => -2 + x * 0.25);
        var extreme = local with { Position = new(new(1, long.MaxValue), new(-1, 1), new(-1, long.MaxValue)) };
        Assert.Equal(MapMembershipStatus.NotRepresentable, MapMarkerSpaces.Query(PrefabFixtures.Effective(sloped).CompleteView(), extreme).Status);
    }

    static MapExactValue Quantized(double metres) => new(checked((long)Math.Round(metres * 1048576, MidpointRounding.ToEven)), 1048576);
}
