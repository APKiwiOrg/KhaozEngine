using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class MapSpaceIntervalTests
{
    [Fact]
    public void Interval_ReportsExactBoundsAndBoundaryIds()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapSpaceInterval i = MapSpaceIntervals.At(CaveFixtures.View(doc), WaterFixtures.Point(2, -38, -2));
        Assert.Equal(("lake-cave", "lake-floor", "lake-roof"), (i.SpaceId, i.LowerBoundaryId, i.UpperBoundaryId));
        Assert.Equal(new MapExactValue(-40, 1), i.Lower);
        Assert.Equal(new MapExactValue(-34, 1), i.Upper);
        Assert.Null(MapSpaceIntervals.At(CaveFixtures.View(doc), WaterFixtures.Point(11, 3, -2)).Upper);
    }

    [Fact]
    public void Column_ListsEverySpaceAtXzInAscendingOrder()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        var column = MapSpaceIntervals.Column(CaveFixtures.View(doc), WaterFixtures.Point(3, 0, -2));
        Assert.Equal(new[] { "dry-cave", "lake-cave", "sea" }, column.Select(c => c.SpaceId));
    }

    [Fact]
    public void Column_NormalizesAliasesAndRefusesOverlappingOwners()
    {
        MapDocument peers = CaveFixtures.WithPeerChamber(null), alias = CaveFixtures.WithPeerChamber("chamber");
        Assert.Equal(MapMembershipStatus.Ambiguous, Assert.Single(MapSpaceIntervals.Column(CaveFixtures.View(peers), WaterFixtures.PeerPoint())).Status);
        Assert.Equal(new[] { "chamber", "outside" }, MapSpaceIntervals.Column(CaveFixtures.View(alias), WaterFixtures.PeerPoint()).Select(c => c.SpaceId));
    }

    [Fact]
    public void Column_AgreesWithPointMembership_IncludingPortalSeams()
    {
        foreach (MapDocument doc in new[] { WaterFixtures.OceanOverCaves(), WaterFixtures.SeaConnectedCave(portalBottomY: -5f) })
        {
            MapScopedSurfaces view = CaveFixtures.View(doc);
            foreach (MapFramePoint p in WaterFixtures.ProbeGrid(doc))
            {
                IReadOnlyList<MapSpaceInterval> column = MapSpaceIntervals.Column(view, p);
                MapSpaceInterval at = MapSpaceIntervals.At(view, p);
                // Every height inside a column interval resolves through point membership to that interval's space.
                foreach (MapSpaceInterval c in column.Where(c => Holds(c, p)))
                    Assert.Equal((c.SpaceId, MapMembershipStatus.Resolved), (at.SpaceId, at.Status));
                if (at.SpaceId is null) continue;
                Assert.Contains(column, c => c.SpaceId == at.SpaceId && Holds(c, p));
            }
        }
    }

    static bool Holds(MapSpaceInterval c, MapFramePoint p) =>
        c.Status == MapMembershipStatus.Resolved && c.Lower.CompareTo(p.ExactY()) <= 0 && (c.Upper is null || p.ExactY().CompareTo(c.Upper.Value) < 0);

    [Fact]
    public void Column_ThroughAPortal_TakesTheBandAndItsChainIds()
    {
        // The grotto mouth on lattice x 8 owns [-5, 3) for the sea, so the grotto keeps the pieces below and above it,
        // each cut end named by the band chain that cut it, and the sea resumes over the cliff top at +5.
        MapScopedSurfaces view = CaveFixtures.View(WaterFixtures.SeaConnectedCave(portalBottomY: -5f));
        MapSpaceInterval[] column = MapSpaceIntervals.Column(view, WaterFixtures.Point(8, 0, -2)).ToArray();
        Assert.Equal(new (string?, MapExactValue, MapExactValue?, string?, string?)[]
        {
            ("grotto", new(-10, 1), new MapExactValue(-5, 1), "grotto-floor", "grotto-mouth-bottom"),
            ("sea", new(-5, 1), new MapExactValue(3, 1), "grotto-mouth-bottom", "grotto-mouth-top"),
            ("grotto", new(3, 1), new MapExactValue(5, 1), "grotto-mouth-top", "grotto-roof"),
            ("sea", new(5, 1), null, "cliff-top", MapSpaceInterval.OpenTop),
        }, column.Select(c => (c.SpaceId, c.Lower, c.Upper, c.LowerBoundaryId, c.UpperBoundaryId)));
        MapSpaceInterval band = MapSpaceIntervals.At(view, WaterFixtures.Point(8, 0, -2));
        Assert.Equal(("sea", new MapExactValue(-5, 1), (MapExactValue?)new MapExactValue(3, 1), "grotto-mouth-bottom", "grotto-mouth-top"),
            (band.SpaceId, band.Lower, band.Upper, band.LowerBoundaryId, band.UpperBoundaryId));
    }

    [Fact]
    public void Column_MergesOverlappingIntervalsOfOneOwner()
    {
        // The aliased peer chamber overlaps the chamber over [5, 9), so the two footprints merge into one interval.
        MapScopedSurfaces view = CaveFixtures.View(CaveFixtures.WithPeerChamber("chamber"));
        Assert.Equal(new (string?, MapExactValue, MapExactValue?)[] { ("chamber", new(5, 1), new MapExactValue(9, 1)), ("outside", new(10, 1), null) },
            MapSpaceIntervals.Column(view, WaterFixtures.PeerPoint()).Select(c => (c.SpaceId, c.Lower, c.Upper)));
    }

    [Fact]
    public void Column_RefusesAVerticallyBoundedAcquisition()
    {
        // A source may leave out every patch outside the acquired heights, so a column, which covers every height,
        // cannot answer from such a view whatever this source returned. A point inside those heights still resolves.
        MapScopedSurfaces view = WaterFixtures.ViewBetween(WaterFixtures.OceanOverCaves(), -45f, 10f);
        MapSpaceInterval refused = Assert.Single(MapSpaceIntervals.Column(view, WaterFixtures.Point(3, 0, -2)));
        Assert.Equal((MapMembershipStatus.MissingGeometry, "column outside acquired vertical scope", (string?)null),
            (refused.Status, refused.Detail, refused.SpaceId));
        Assert.Equal("lake-cave", MapSpaceIntervals.At(view, WaterFixtures.Point(3, -38, -2)).SpaceId);
    }

    [Fact]
    public void Interval_AgreesWithMembershipEverywhere()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapScopedSurfaces view = CaveFixtures.View(doc);
        foreach (MapFramePoint p in WaterFixtures.ProbeGrid(doc))
            Assert.Equal(new MapSpaceMembership(view).Query(p).SpaceId, MapSpaceIntervals.At(view, p).SpaceId);
    }
}
