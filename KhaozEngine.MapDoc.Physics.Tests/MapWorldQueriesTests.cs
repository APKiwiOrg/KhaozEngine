using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Pick, reach and physical distance over a built world's interaction envelopes and colliders.</summary>
public class MapWorldQueriesTests
{
    [Fact]
    public void NativeDoorway_StaysOpenAfterYawAndScale()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var q = new MapWorldQueries(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()));
        var t = f.Resolved.Placements.Single(p => p.PlacementId == "doorway").Transform;
        var through = Vector3.Transform(Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.371f));
        Assert.Null(q.Pick(new MapPickRay(t.TransformPoint(new Vector3(0, 1, -2)), through, 4f)));
        Assert.Equal("doorway", q.Pick(new MapPickRay(t.TransformPoint(new Vector3(-0.75f, 1, -2)), through, 4f))!.PlacementId);
        Assert.True(q.PhysicalDistance(new MovementBody(t.TransformPoint(new Vector3(0, 1, 0)), 0.2f, 0.8f), "doorway") > 0);
    }

    [Fact]
    public void TreeBand_HitsAtBasePlusOneAndRefusesAtBasePlusThree()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildTree());
        var band = new MapInteractionBand(0f, 2f);
        Assert.NotNull(q.Pick(new MapPickRay(new Vector3(0, 1, -2), Vector3.UnitZ, 4f), band));
        Assert.Null(q.Pick(new MapPickRay(new Vector3(0, 3, -2), Vector3.UnitZ, 4f), band));
    }

    [Fact]
    public void LowObject_ReachUsesTheOneMetreEnvelope()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.3f);
        Assert.True(q.Within(head, "crate", 0.5f));
        Assert.Equal(0.44f, q.Distance(head, "crate"), 3);
        Assert.True(q.PhysicalDistance(head, "crate") > 1f);
    }

    [Fact]
    public void EqualDistancePicks_BreakTiesByOrdinalPlacementId()
    {
        // The ray starts in the cell b-crate's rear post reaches, so b-crate is tested first and a-crate must win the
        // tie on its id rather than on order.
        var hit = new MapWorldQueries(NativeWorldFixtures.BuildTwinCrates()).Pick(new MapPickRay(new Vector3(0, 0.1f, -20), Vector3.UnitZ, 30f))!;
        Assert.Equal("a-crate", hit.PlacementId);
        Assert.Equal(19.7f, hit.Distance, 1e-5f);
    }

    [Fact]
    public void Pick_InspectsABoundedNumberOfEnvelopes()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrateField(100, 100, spacingMetres: 4f));
        Assert.Equal("crate-000-000", q.Pick(new MapPickRay(new Vector3(0f, 0.1f, -2f), Vector3.UnitZ, 30f), null, out int inspected)!.PlacementId);
        Assert.InRange(inspected, 1, 64);
    }

    [Fact]
    public void FarRegion_PickAndReachHoldTheirTolerance()
    {
        var near = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var far = new MapWorldQueries(NativeWorldFixtures.BuildCrateAt(30_000f, 0f, -30_000f));
        var offset = new Vector3(30_000f, 0f, -30_000f);
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.3f);
        Assert.Equal(near.Distance(head, "crate"), far.Distance(new MovementBody(head.Centre + offset, 0.2f, 0.3f), "crate"), 0.004f);
    }

    // Expected values below are hand derived, then checked against a dense brute-force sampling of each clipped shape.

    [Fact]
    public void TallMesh_PicksAndMeasuresTheMeshItself()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildMeshWall());
        var hit = q.Pick(new MapPickRay(new Vector3(0, 1, -2), Vector3.UnitZ, 4f))!;
        Assert.Equal("mesh-wall", hit.PlacementId);
        Assert.Equal(2f, hit.Distance, 1e-5f);
        Assert.Equal(-1f, hit.Normal.Z, 1e-5f);
        // The core's lowest point (1.5, 2.3, -0.5) to the wall corner (1, 2, 0): sqrt(0.59) - 0.2.
        Assert.Equal(0.568115f, q.Distance(new MovementBody(new Vector3(1.5f, 2.5f, -0.5f), 0.2f, 0.4f), "mesh-wall"), 1e-5f);
    }

    [Fact]
    public void LeaningPost_MeasuresTheTiltedCylinderWithAndWithoutABand()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildLeaningPost());
        var body = new MovementBody(new Vector3(1.5f, 0.6f, 0f), 0.2f, 0.4f);
        // The core's top (1.5, 0.8, 0) is 0.7 sqrt(0.5) from the axis along (1, 1, 0): 0.7 sqrt(0.5) - 0.1 - 0.2.
        Assert.Equal(0.194975f, q.Distance(body, "leaning-post"), 1e-5f);
        Assert.Equal(0.194975f, q.PhysicalDistance(body, "leaning-post"), 1e-5f);
        // Cut at y 0.5, the post's slice is an ellipse centred at x 0.5 reaching x 0.5 + 0.1 sqrt(2): 1.5 less that
        // less 0.2.
        Assert.Equal(0.658579f, q.Distance(body, "leaning-post", new MapInteractionBand(0f, 0.5f)), 1e-5f);
    }

    [Fact]
    public void TreeBand_ClipsTheUprightCylinderForReach()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildTree());
        var body = new MovementBody(new Vector3(0, 3f, -1f), 0.2f, 0.5f);
        var band = new MapInteractionBand(0f, 2f);
        // The core spans y 2.7 to 3.3 at 1 m from the axis: radial gap 0.7, then 0.7 less 0.2 unbanded.
        Assert.Equal(0.5f, q.Distance(body, "tree"), 1e-5f);
        Assert.True(q.Within(body, "tree", 0.6f));
        // Clipped to y 2 the vertical gap is 0.7 too: sqrt(0.98) - 0.2.
        Assert.Equal(0.789949f, q.Distance(body, "tree", band), 1e-5f);
        Assert.False(q.Within(body, "tree", 0.7f, band: band));
        Assert.True(q.Within(body, "tree", 0.8f, band: band));
    }

    [Fact]
    public void LowHullBand_ClipsTheSweptEnvelope()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var head = new MovementBody(new Vector3(0, 1.6f, -0.7f), 0.2f, 0.3f);
        // The swept hull spans y 0 to 1. Clipped to y 0.5, the core's bottom (y 1.5, z -0.7) reaches the edge at
        // (y 0.5, z -0.3): sqrt(1.16) - 0.2.
        Assert.Equal(0.877033f, q.Distance(head, "crate", new MapInteractionBand(0.2f, 0.5f)), 1e-5f);
    }

    [Fact]
    public void YawedBoxBand_ClipsReach()
    {
        var f = NativeWorldFixtures.Placed("parapet-1m", yaw: 0.6f);
        var q = new MapWorldQueries(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()));
        var t = f.Resolved.Placements.Single().Transform;
        var body = new MovementBody(t.TransformPoint(new Vector3(0, 1.3f, -0.5f)), 0.2f, 0.3f);
        var band = new MapInteractionBand(0f, 0.4f);
        // Unbanded: vertical gap 0.2 and depth gap 0.4, sqrt(0.2) - 0.2 = 0.247. Clipped to y 0.4 the vertical gap is
        // 0.8, sqrt(0.8) - 0.2 = 0.694.
        Assert.True(q.Within(body, "parapet-1m", 0.25f));
        Assert.False(q.Within(body, "parapet-1m", 0.5f, band: band));
        Assert.True(q.Within(body, "parapet-1m", 0.7f, band: band));
    }
}
