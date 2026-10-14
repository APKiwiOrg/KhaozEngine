using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public partial class OrdinaryContactLocalHeadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebasedAmplifiedWadeLandBoundCoversLeasedTravel(bool swimPace)
    {
        using var scene = AmplifiedWadeScene(out var field);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        var body = AmplifiedWader;
        context.ValidateTuning(scene.Tuning);
        Assert.False(context.Medium!(0, 0, body.Position.Y - scene.Tuning.CapsuleHalfHeight).InWater);
        var sample = context.SampleWaterForPacing(body, scene.Tuning);
        Assert.True(sample.Medium.InWater);
        Assert.False(sample.Swimming);
        float bound = swimPace
            ? SwimPace.Bound(body, scene.Tuning, false, Dt, context)
            : RangeApproachCore.TravelBound(body, scene.Tuning, false, Dt, context);
        using var lease = scene.Acquire();
        var local = body with { Position = body.Position - scene.Physics.Origin };
        var reference = scene.Binding.StepTowards(new(local, scene.Frame, null), Vector2.UnitX, false, Dt, lease);
        Assert.Equal(MovementStepOutcome.Advanced, reference.Outcome);
        Assert.True(reference.State.State.Grounded);
        Assert.False(reference.State.State.Swimming);
        Assert.Equal(0.8f, reference.State.State.Position.X);
        Assert.True(bound >= reference.State.State.Position.X,
            $"Bound {bound:R} must cover leased travel {reference.State.State.Position.X:R}.");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("routed")]
    [InlineData("aquatic-routed")]
    public void RebasedAmplifiedWadeLandApproachStopsAtRangeWithoutCrossing(string consumer)
    {
        using var scene = AmplifiedWadeScene(out var field);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        var body = AmplifiedWader;
        var target = ReachTarget.Point(new(0.45f, 0.55f, 0));
        Assert.False(ReachGeometry.Within(Shape(body, scene.Tuning), target, 0));
        var direct = new DirectMoveToRange(new(20, 0.01f, 20, 0.01f));
        var routed = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(0.45f, 0))),
            Space, (_, _) => true, null, new() { SteerWhileSwimming = consumer == "aquatic-routed" });
        var steering = Tick(body);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        var driven = NpcGroundMovement.Step(body, steering, false, Dt, scene.Tuning, context);
        Assert.True(driven.Grounded);
        Assert.False(driven.Swimming);
        Assert.True(driven.Position.X < target.Centre.X,
            $"Approach crossed target X {target.Centre.X:R} to {driven.Position.X:R}.");
        Assert.True(ReachGeometry.Within(Shape(driven, scene.Tuning), target, 0));
        Assert.InRange(driven.Position.X, 0.45f - 0.25f, 0.2f);
        var arrival = Tick(driven);
        Assert.Equal(RangeMoveStatus.InRange, arrival.Status);
        Assert.Equal(Vector2.Zero, arrival.WorldDirection);

        RangeSteering Tick(MoveState state) => consumer == "direct"
            ? direct.Tick(state, scene.Tuning, target, 0, false, false, Dt, context)
            : routed.Tick(state, scene.Tuning, target, 0, false, Dt, context);
    }

    [Theory]
    [InlineData(false, 0.55f, 0.4f)]
    [InlineData(true, 0.55f, 0.4f)]
    [InlineData(false, 0.54f, 0.8f)]
    [InlineData(true, 0.54f, 0.8f)]
    public void LegacyLandBoundKeepsAbsoluteMediumArithmetic(bool swimPace, float centreY, float expected)
    {
        const float surface = -0.19999998807907104f;
        var tuning = OrdinaryContactScene.DefaultTuning with { Controller = MovementControllerKind.Legacy, Water = default };
        using var context = new GroundMoveContext((_, _) => -0.2f,
            medium: (_, _, feetY) => feetY < surface ? new(surface, true, 2f) : MovementMedium.Dry);
        var body = AmplifiedWader with { Position = new(0, centreY, 0) };
        float bound = swimPace
            ? SwimPace.Bound(body, tuning, false, Dt, context)
            : RangeApproachCore.TravelBound(body, tuning, false, Dt, context);
        Assert.Equal(expected, bound);
    }

    static MoveState AmplifiedWader => new() { Position = new(0, 0.55f, 0), Grounded = true, SpeedScale = 1f };

    static OrdinaryContactScene AmplifiedWadeScene(out IMovementWaterField field)
    {
        field = new AmplifiedWadeField();
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.WadeOnly) };
        return new(tuning, (_, _) => -0.2f,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -0.2f, -0.2f), water: field, origin: new(0, 3.3f, 0));
    }

    sealed class AmplifiedWadeField : IMovementWaterField
    {
        readonly IMovementWaterField _level = MovementWaterField.Level("world", -0.19999998807907104f, -4f);
        public string SemanticIdentity => "amplified-wade-pacing";
        public long Generation => 0;
        public MovementWaterExtent Extent => _level.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            var point = _level.SampleCentreWater(body, origin);
            return point.InWater ? new(point.Availability, point.Space, point.Domain, true, 2f, point.Interval) : point;
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            _level.TraceWater(query, origin, identity, spans, contacts);
    }
}
