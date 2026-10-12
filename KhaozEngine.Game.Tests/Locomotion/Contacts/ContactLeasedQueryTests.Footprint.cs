using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData("zero-cosine")]
    [InlineData("overflow")]
    [InlineData("vertical")]
    [InlineData("horizontal")]
    public void ExpandedScopeRefusesBeforeProducerOrBackend(string fault)
    {
        Vector3 min = fault == "vertical" ? new(-2, -0.12f, -2) : new(-2);
        Vector3 max = fault == "horizontal" ? new(0.50005f, 2, 2) : new(2);
        using var scene = new QueryScene(scopeMin: min, scopeMax: max,
            maxRise: fault == "overflow" ? float.MaxValue : 0.3f,
            maxDrop: fault == "overflow" ? float.MaxValue : 0.3f);
        FootSupportQuery query = Query(Vector3.Zero);
        query = fault switch
        {
            "zero-cosine" => query with { CosMaxSlope = 0 },
            // Every binary32 sum stays finite, but the outward column top exceeds binary32.
            "overflow" => query with { ReachUp = float.MaxValue, ReachDown = 0 },
            "horizontal" => query with { FootRadius = 0.5f },
            _ => query,
        };
        if (fault is "zero-cosine" or "overflow")
            Assert.False(SupportFootprintBounds.TryCreate(FootSupport.DescribeFootprint(query), out _));
        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() => scene.Queries().FindSupport(query));
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        Assert.Equal(0, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(0, scene.Environment.ContactSupportCalls);
        Assert.Equal(0, scene.View.Calls);
    }

    [Fact]
    public void ExpandedScopeAllowsCompleteSupportWithCanonicalBinding()
    {
        using var geometry = new FootSupportScene(SceneVariant.Box).Flat("floor", -2, 2, -2, 2, 0);
        using var scene = new QueryScene(geometry, scopeMin: new(-2, -0.5f, -2), scopeMax: new(2), maxRise: 0.3f, maxDrop: 0.3f);
        SupportSample support = scene.Queries().FindSupport(Query(Vector3.Zero), out var binding);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(new MovementSupportKey("world", "bank"), binding!.Value.Owner);
        Assert.Equal(scene.Lease.Identity, binding.Value.Identity);
        Assert.Equal(1, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(1, scene.Environment.ContactSupportCalls);
    }

    [Fact]
    public void GroundAndEverySignalUseOneFootprintIntervalAndReuseSampleZero()
    {
        using var geometry = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 4, -4, 4, 0);
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        SupportSample start = queries.FindSupport(Query(Vector3.Zero), out var binding);
        Assert.Equal(new MovementSupportKey("world", "bank"), binding!.Value.Owner);
        GroundStepResult step = GroundCore.Step(Vector3.Zero, new(0.1f, 0), 1f / 30, Tuning, Settings, queries);
        Assert.Equal(GroundFooting.Walkable, step.Footing);
        int before = scene.View.FootprintCalls;
        var signals = GroundSignals.Resolve(new MoveState { ClimbRate = 1 }, 0.1f, 0.1f, true,
            false, 0, new(0.1f, 0), step.Feet, step.Support, true, 1f / 30, Tuning, 0.125f,
            MathF.Cos(Tuning.MaxSlopeRadians), queries);
        Assert.Equal(before + GroundSignals.Samples, scene.View.FootprintCalls);
        Assert.Equal(1, scene.View.Acquisitions);
        Assert.NotNull(scene.View.FootprintLease);
        Assert.Equal(scene.Lease.Identity, binding.Value.Identity);
    }

    // The start stands on the over-capacity fan's apex, whose complete footprint holds all 300 triangles, so it
    // is held before the shell moves. The target refusal is injected at the first query after the start, with
    // box geometry the certified shell sweep resolves, so the attempted move is blocked at the start.
    [Fact]
    public void RefusedCompleteStartHoldsAndRefusedTargetBlocks()
    {
        using (var fan = FootSupportScenes.OverCapacityFan())
        using (var scene = new QueryScene(fan))
        {
            GroundStepResult held = GroundCore.Step(Vector3.Zero, new(0.1f, 0), 1f / 30,
                Tuning, Settings, scene.Queries());
            Assert.Equal(GroundFooting.Held, held.Footing);
            Assert.Equal(SupportStatus.Refused, held.Support.Status);
            Assert.Equal(Vector3.Zero, held.Feet);
            Assert.Equal(Vector2.Zero, held.Achieved);
            Assert.Equal(1, scene.View.FootprintCalls);
        }
        using var floor = new FootSupportScene(SceneVariant.Box).Flat("floor", -3, 3, -2, 2, 0);
        using var target = new QueryScene(floor);
        target.View.RefuseFootprintFrom = 2;
        var start = new Vector3(-1.5f, 0, 0);
        GroundStepResult blocked = GroundCore.Step(start, new(0.2f, 0), 1f / 30, Tuning, Settings, target.Queries());
        Assert.True(blocked.Blocked, $"{blocked}");
        Assert.Equal(GroundFooting.Walkable, blocked.Footing);
        Assert.Equal(start, blocked.Feet);
        Assert.Equal(Vector2.Zero, blocked.Achieved);
        Assert.True(target.View.FootprintCalls >= 2);
        Assert.Equal(1, target.View.Acquisitions);
    }

    [Fact]
    public void FootprintFailureAfterInitialSupportHoldsTheWholeTick()
    {
        using var geometry = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 4, -4, 4, 0);
        using var scene = new QueryScene(geometry);
        scene.View.FailFootprintAt = 2;
        var input = new FramedMovementState(new MoveState { Position = new(0, 0.751f, 0), Grounded = true },
            scene.Lease.Frame, new(Room, new MovementSupportKey("world", "bank"), scene.Lease.Identity));
        MovementStepResult result = ContactController.StepLeased(input, Vector2.UnitX, 1, false, false,
            1f / 30, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
        Assert.Equal(2, scene.View.FootprintCalls);
        Assert.Equal(1, scene.View.Acquisitions);
    }
}
