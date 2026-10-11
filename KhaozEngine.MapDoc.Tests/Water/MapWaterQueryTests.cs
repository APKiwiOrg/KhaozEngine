using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterQueryTests
{
    static readonly MapDocument Doc = WaterFixtures.OceanOverCaves();
    static MapWaterSample At(float x, float y, float z) => WaterFixtures.Query(Doc).Sample(WaterFixtures.Point(x, y, z));

    [Fact]
    public void Ocean_DoesNotWetTheDryCaveBelowIt()
    {
        MapWaterSample s = At(2, -55, -2);
        Assert.Equal((MapWaterStatus.Dry, "dry-cave"), (s.Status, s.SpaceId));
        Assert.Null(s.BodyId);
    }

    [Fact]
    public void CaveLake_IsItsOwnBody_AtItsOwnLevel()
    {
        MapWaterSample s = At(5, -35, -2);
        Assert.Equal((MapWaterStatus.Wet, "cave-lake", "lake-cave"), (s.Status, s.BodyId, s.SpaceId));
        Assert.Equal((-40f, -34f, -34f, true), (s.LowerY, s.UpperY, s.NominalSurfaceY, s.UpperIsFreeSurface));
        Assert.Equal(("lake-floor", "water:cave-lake"), (s.LowerBoundaryId, s.UpperBoundaryId));
    }

    [Fact]
    public void ContourTie_IsClosed()
    {
        MapWaterSample s = At(2, -35, -2);
        Assert.Equal((MapWaterStatus.Wet, -34f, false, "lake-roof"), (s.Status, s.UpperY, s.UpperIsFreeSurface, s.UpperBoundaryId));
    }

    [Fact]
    public void FloodedCave_HasAClosedTop()
    {
        MapWaterSample s = At(7, -30, -2);
        Assert.Equal((MapWaterStatus.Wet, "flooded"), (s.Status, s.BodyId));
        Assert.Equal((-25f, -20f, false, "flood-roof"), (s.UpperY, s.NominalSurfaceY, s.UpperIsFreeSurface, s.UpperBoundaryId));
    }

    [Fact]
    public void FeetAtTheSurface_AreDry_AndJustBelowAreWet()
    {
        Assert.Equal(MapWaterStatus.Dry, At(4, 0f, -2).Status);
        Assert.Equal(MapWaterStatus.Wet, At(4, -0.0001f, -2).Status);
        Assert.Equal(MapWaterStatus.Dry, At(5, -34f, -2).Status);
        Assert.Equal(MapWaterStatus.Wet, At(5, MathF.BitDecrement(-34f), -2).Status);
        Assert.Equal(MapWaterStatus.Dry, At(11, 2.5f, -2).Status);
    }

    [Fact]
    public void BodyEdges_FollowSpaceOwnership()
    {
        // The ocean's domain owns world z -4 and excludes z 0. The sea footprint owns lattice rows 0 to 3, which
        // includes world z 0 and excludes z -4. The wet set is the intersection, so both edges are dry.
        MapWaterSample minEdge = At(4, -1, -4), maxEdge = At(4, -1, 0), inside = At(4, -1, -2);
        Assert.Equal((MapWaterStatus.Dry, null), (minEdge.Status, minEdge.SpaceId));
        Assert.Equal((MapWaterStatus.Dry, "sea"), (maxEdge.Status, maxEdge.SpaceId));
        Assert.Equal(MapWaterStatus.Wet, inside.Status);
        Assert.Equal(new[] { "ocean" }, WaterFixtures.Query(Doc).Column(WaterFixtures.Point(9, 0, -2)).Select(s => s.BodyId));
    }

    [Fact]
    public void OutsideEverySpace_IsDry_AndOutsideTheAcquisitionIsMissing()
    {
        MapWaterSample outside = At(40, -1, -2);
        Assert.Equal((MapWaterStatus.Dry, null), (outside.Status, outside.SpaceId));
        Assert.Equal(MapMembershipStatus.Outside, new MapSpaceMembership(CaveFixtures.View(Doc)).Query(WaterFixtures.Point(40, -1, -2)).Status);
        Assert.Equal(MapWaterStatus.MissingGeometry, At(80, -1, -2).Status);
    }

    [Fact]
    public void AmbiguousGeometry_IsNeverDry()
    {
        MapDocument peers = WaterFixtures.PeerChambersWithWater();
        MapWaterSample s = new MapWaterQuery(CaveFixtures.View(peers), peers.Water!).Sample(WaterFixtures.PeerPoint());
        Assert.Equal(MapWaterStatus.Ambiguous, s.Status);
    }

    [Fact]
    public void Column_ListsStackedWaterInOrder()
    {
        IReadOnlyList<MapWaterSample> column = WaterFixtures.Query(Doc).Column(WaterFixtures.Point(5, 0, -2));
        Assert.Equal(new[] { "cave-lake", "ocean" }, column.Select(s => s.BodyId));
        Assert.All(column, s => Assert.Equal(MapWaterStatus.Wet, s.Status));
    }

    [Fact]
    public void WetIntervals_SatisfyTheMovementIntervalContract()
    {
        MapWaterQuery query = WaterFixtures.Query(Doc);
        MapWaterSample[] wet = WaterFixtures.ProbeGrid(Doc).Select(query.Sample).Where(s => s.Status == MapWaterStatus.Wet).ToArray();
        Assert.Equal(new[] { "cave-lake", "flooded", "ocean" }, wet.Select(s => s.BodyId).Distinct().Order());
        foreach (MapWaterSample s in wet)
        {
            Assert.True(s.LowerY < s.UpperY && s.UpperY <= s.NominalSurfaceY);
            Assert.True(!s.UpperIsFreeSurface || s.UpperY == s.NominalSurfaceY);
            Assert.False(string.IsNullOrWhiteSpace(s.LowerBoundaryId) || string.IsNullOrWhiteSpace(s.UpperBoundaryId));
            Assert.Equal(Doc.Water!.Bodies.Single(b => b.Id == s.BodyId).SurfaceY, s.NominalSurfaceY);
        }
    }

    [Fact]
    public void Query_CarriesItsWitness_AndIgnoresLaterMutation()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapScopedSurfaces view = CaveFixtures.View(doc);
        var query = new MapWaterQuery(view, doc.Water!);
        string digest = doc.Water!.Digest();
        int lake = doc.Water.Bodies.FindIndex(b => b.Id == "cave-lake");
        var rects = (MapWaterRect[])doc.Water.Bodies[lake].Domain.Rects;
        rects[0] = new MapWaterRect(100, 0, 106, 4);
        doc.Water.Bodies[lake] = doc.Water.Bodies[lake] with { SurfaceY = -39f };
        doc.Water.Media[0] = doc.Water.Media[0] with { WadeSpeedScale = 0.25f };
        MapWaterSample s = query.Sample(WaterFixtures.Point(5, -35, -2));
        Assert.Same(view.ReadWitness, s.Witness);
        Assert.Equal((digest, digest), (query.WaterDigest, s.WaterDigest));
        Assert.Equal((MapWaterStatus.Wet, "cave-lake", -34f), (s.Status, s.BodyId, s.NominalSurfaceY));
    }

    [Fact]
    public void CollapsedFloatInterval_IsNotRepresentable()
    {
        MapDocument doc = WaterFixtures.HairlineCave();
        MapWaterQuery query = WaterFixtures.Query(doc);
        Assert.Equal(MapWaterStatus.NotRepresentable, query.Sample(WaterFixtures.Point(1, -1, -1)).Status);
        Assert.Equal(MapWaterStatus.NotRepresentable, Assert.Single(query.Column(WaterFixtures.Point(1, 0, -1))).Status);
    }
}
