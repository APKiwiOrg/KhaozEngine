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
    [Fact]
    public void FieldDerivedMediumPreservesAbsoluteFeetAtTheStrictSurface()
    {
        var field = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene();
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        context.ValidateTuning(scene.Tuning);
        Assert.True(context.Medium!(0, 0, -float.Epsilon).InWater);
        Assert.False(context.Medium!(0, 0, 0).InWater);
    }

    [Fact]
    public void ApproachPacingHonoursSelectedWaterPolicy()
    {
        var field = MovementWaterField.Level("world", 0, -4);
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: field);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, (_, _, _) => MovementMedium.Dry,
            null, field, scene.Registration, Boundary);
        context.ValidateTuning(tuning);
        var sample = context.Medium!(0, 0, -1);
        Assert.True(sample.InWater);
        Assert.Equal(0, sample.WaterSurfaceY);
        var swimmer = new MoveState
        {
            Position = new(0, -0.15f, 0), Swimming = true,
            WaterExcursion = WaterExcursionState.Surface
        };
        Assert.True(context.IsSwimmingForPacing(swimmer, tuning, sample));
        Assert.False(context.IsSwimmingForPacing(swimmer, tuning with { Water = new(WaterTraversalMode.WadeOnly) }, sample));
        Assert.False(context.IsSwimmingForPacing(swimmer, tuning with { Water = new(WaterTraversalMode.DryOnly) }, sample));
        Assert.Equal(tuning.SwimSpeed * Dt, SwimPace.Bound(swimmer, tuning, false, Dt, context));
        var direct = new DirectMoveToRange(new(20, 0.01f, 20, 0.01f));
        var target = ReachTarget.Point(new(4, -0.15f, 0));
        Assert.Equal(RangeMoveStatus.Suspended, direct.Tick(swimmer, tuning, target, 0, false, false, Dt, context).Status);
        // An authored aquatic route is supplied directly, with no contact navigation bake.
        var aquatic = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(4, 0))),
            Space, (_, _) => true, null, new() { SteerWhileSwimming = true });
        var steering = aquatic.Tick(swimmer, tuning, target, 0, false, Dt, context);
        Assert.Equal(Vector2.UnitX, steering.WorldDirection);
        var driven = NpcGroundMovement.Step(swimmer, steering, false, Dt, tuning, context);
        using var lease = scene.Acquire();
        var reference = scene.Binding.StepTowards(new(swimmer, scene.Frame, null), Vector2.UnitX, false, Dt, lease);
        Assert.Equal(MovementStepOutcome.Advanced, reference.Outcome);
        Assert.Equal(reference.State.State, driven);
        Assert.Equal(tuning.SwimSpeed * Dt, driven.Position.X);
    }

    [Fact]
    public void WadeOnlyDirectApproachSlidesAtTheDeepShoreInsteadOfBecomingASwimmer()
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.WadeOnly) };
        float high = -2 * tuning.CapsuleHalfHeight * tuning.SwimEnterDepthFraction + 0.001f;
        var field = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(tuning, (x, _) => high - 0.1f * x,
            (_, _) => Vector3.Normalize(new(0.1f, 1, 0)),
            new(new(-20, -4, -20), new(20, 4, 20), -4, 4), water: field);
        using var context = new GroundMoveContext(scene.Height, scene.Normal, scene.Physics, null, null, null,
            field, scene.Registration, Boundary);
        var initial = new MoveState { Position = new(0, high + 0.75f, 0), Grounded = true };
        var direct = new DirectMoveToRange(new(20, 0.01f, 20, 0.01f));
        var steering = direct.Tick(initial, tuning, ReachTarget.Point(new(4, initial.Position.Y, 4)), 0, false, false, Dt, context);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.NotEqual(Vector2.Zero, steering.WorldDirection);
        var driven = NpcGroundMovement.Step(initial, steering, false, Dt, tuning, context);
        using var lease = scene.Acquire();
        var reference = scene.Binding.StepTowards(new(initial, scene.Frame, null), steering.WorldDirection, false, Dt, lease);
        Assert.Equal(reference.State.State, driven);
        Assert.False(driven.Swimming);
        Assert.True(driven.Position.Z > initial.Position.Z);
        Assert.Equal(initial.Position.X, driven.Position.X);
    }
}
