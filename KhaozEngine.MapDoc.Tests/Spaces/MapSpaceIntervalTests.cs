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
                if (at.SpaceId is null) continue;
                Assert.Contains(column, c => c.SpaceId == at.SpaceId && c.Lower.CompareTo(p.ExactY()) <= 0 && (c.Upper is null || p.ExactY().CompareTo(c.Upper.Value) < 0));
            }
        }
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
