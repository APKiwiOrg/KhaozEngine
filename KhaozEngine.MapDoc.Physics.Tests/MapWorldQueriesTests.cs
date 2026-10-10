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
        => Assert.Equal("a-crate", new MapWorldQueries(NativeWorldFixtures.BuildTwinCrates()).Pick(new MapPickRay(new Vector3(0, 0.1f, -2), Vector3.UnitZ, 4f))!.PlacementId);

    [Fact]
    public void Pick_InspectsABoundedNumberOfEnvelopes()
    {
        var q = new MapWorldQueries(NativeWorldFixtures.BuildCrateField(100, 100, spacingMetres: 4f));
        Assert.Equal("crate-000-000", q.Pick(new MapPickRay(new Vector3(0f, 0.1f, -2f), Vector3.UnitZ, 30f))!.PlacementId);
        Assert.InRange(q.LastPickInspectedEnvelopes, 1, 64);
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
}
