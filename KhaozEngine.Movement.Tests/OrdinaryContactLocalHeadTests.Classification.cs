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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RebasedSwimPacingAndAquaticApproachUseTheLeasedWaterDecision(bool strictSurface, bool approach)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with
        {
            Water = new(WaterTraversalMode.SurfaceSwimmer),
            SwimSurfaceSubmersionFraction = strictSurface ? 0 : 0.6f
        };
        float surface = strictSurface ? -0.19999998807907104f : 0;
        var field = MovementWaterField.Level("world", surface, -4);
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: field, origin: new(0, 3.3f, 0));
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        var body = new MoveState
        {
            Position = new(0, strictSurface ? 0.55f : -0.0749998539686203f, 0),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface
        };
        using (var lease = scene.Acquire())
        {
            var local = body with { Position = body.Position - scene.Physics.Origin };
            var classified = scene.Binding.ReclassifyContinuation(new(local, scene.Frame, null), lease);
            Assert.Equal(MovementStepOutcome.Advanced, classified.Outcome);
            Assert.True(classified.State.State.Swimming);
            var reference = scene.Binding.StepTowards(new(local, scene.Frame, null), Vector2.UnitX, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.Advanced, reference.Outcome);
            Assert.Equal(tuning.SwimSpeed * Dt, reference.State.State.Position.X);
        }
        context.ValidateTuning(tuning);
        if (!approach)
        {
            Assert.Equal(tuning.SwimSpeed * Dt, SwimPace.Bound(body, tuning, false, Dt, context));
            return;
        }
        var aquatic = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(4, 0))),
            Space, (_, _) => true, null, new() { SteerWhileSwimming = true });
        if (strictSurface)
        {
            // Resolve the arrival before a swim tick can move this exact boundary point above the surface.
            var arrival = aquatic.Tick(body, tuning, ReachTarget.Point(body.Position), 0, false, Dt, context);
            Assert.Equal(RangeMoveStatus.InRange, arrival.Status);
            Assert.Equal(Vector2.Zero, arrival.WorldDirection);
            return;
        }
        var steering = aquatic.Tick(body, tuning, ReachTarget.Point(new(4, body.Position.Y, 0)), 0, false, Dt, context);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.Equal(Vector2.UnitX, steering.WorldDirection);
        var driven = NpcGroundMovement.Step(body, steering, false, Dt, tuning, context);
        using var drivenLease = scene.Acquire();
        var drivenReference = scene.Binding.StepTowards(new(body with { Position = body.Position - scene.Physics.Origin },
            scene.Frame, null), steering.WorldDirection, false, Dt, drivenLease);
        Assert.Equal(drivenReference.State.State with
        { Position = drivenReference.State.State.Position + scene.Physics.Origin }, driven);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedTopWaterNeverGrantsSwimmerPacingAuthority(bool approach)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        var field = new ClosedTopField();
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: field);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        var body = new MoveState { Position = new(0, 0.65f, 0) };
        using (var lease = scene.Acquire())
        {
            var reference = scene.Binding.StepTowards(new(body, scene.Frame, null), Vector2.UnitX, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.PlacementRefused, reference.Outcome);
            Assert.False(reference.State.State.Swimming);
        }
        context.ValidateTuning(tuning);
        Assert.True(context.Medium!(0, 0, -0.1f).InWater);
        if (!approach)
        {
            Assert.Equal(tuning.WalkSpeed * Dt, SwimPace.Bound(body, tuning, false, Dt, context));
            return;
        }
        var aquatic = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(4, 0))),
            Space, (_, _) => true, null, new() { SteerWhileSwimming = true });
        var carriedSwimmer = body with { Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        Assert.Equal(RangeMoveStatus.Suspended,
            aquatic.Tick(carriedSwimmer, tuning, ReachTarget.Point(new(4, 0.65f, 0)), 0, false, Dt, context).Status);
    }

    sealed class ClosedTopField : IMovementWaterField
    {
        readonly IMovementWaterField _level = MovementWaterField.Level("world", 0, -4);
        static readonly MovementWaterInterval Interval = new(-4, 0, 1, false, "bed", "ceiling");
        public string SemanticIdentity => "closed-top-pacing";
        public long Generation => 0;
        public MovementWaterExtent Extent => _level.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            var point = _level.SampleCentreWater(body, origin);
            return point.InWater ? new(point.Availability, point.Space, point.Domain, true, point.SpeedScale, Interval) : point;
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            var result = _level.TraceWater(query, origin, identity, spans, contacts);
            for (int i = 0; i < result.ContactsWritten; i++)
            {
                var contact = contacts[i];
                contacts[i] = new(contact.Domain, contact.Space, Interval, contact.IntervalColumnXZ, contact.Normal,
                    contact.Fraction, contact.BoundaryId, contact.CoverageRegionHandle, contact.Overlap, contact.SpanOverlap);
            }
            return result;
        }
    }
}
