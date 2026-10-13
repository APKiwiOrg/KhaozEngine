using System;
using System.Numerics;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryServedDomainTests
{
    [Theory]
    [InlineData(120f, 100f, 1f, 1f, false, 0.75f)]
    [InlineData(4f, 6f, 4f, 1f, true, 0.75f)]
    [InlineData(4f, 6f, 1f, 3f, false, 0.75f)]
    [InlineData(4f, 6f, 1f, 1f, false, 20f)]
    public void ResidentScopeCoversMotionWithoutPlannerRetries(float walk, float run, float strafe,
        float scale, bool faceCamera, float centreY)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { WalkSpeed = walk, RunSpeed = run, StrafeSpeedScale = strafe };
        using var scene = new OrdinaryContactScene(tuning,
            extent: new(new(-100, -4, -100), new(100, 40, 100), 0, 0));
        var state = new MoveState { Position = new(0, centreY, 0), Grounded = centreY == 0.75f, SpeedScale = scale };
        var command = new MoveCommand(Vector2.UnitX, false, 0, faceCamera: faceCamera);
        var dry = CharacterMovement.Step(state, command, 0.1f, scene.Height, tuning, world: scene.Physics);
        Assert.Equal(scene.Scope, scene.Binding.CreateOrdinaryScope(scene.Frame));
        using var lease = scene.Acquire();
        var moved = scene.Binding.Step(new(state, scene.Frame, null), command, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, moved.Outcome);
        Assert.Equal(dry, moved.State.State);
    }

    [Fact]
    public void PlayableEdgeHasExteriorSupportAndOutsideDomainHolds()
    {
        using var scene = new OrdinaryContactScene();
        using var lease = scene.Acquire();
        var edge = scene.State(new(20, 0.75f, 0));
        Assert.Equal(MovementStepOutcome.Advanced, scene.Binding.StepTowards(edge, Vector2.Zero, false, 0.1f, lease).Outcome);
        var exterior = scene.State(new(scene.Scope.Max.X + 1f, 0.75f, 0));
        var refused = scene.Binding.StepTowards(exterior, Vector2.Zero, false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, refused.Outcome);
        Assert.Equal(exterior.State.Position, refused.State.State.Position);
    }

    [Fact]
    public void SixtyDegreeContourCarryAdvancesWithTheDirectDryResult()
    {
        float slope = 60f * MathF.PI / 180f;
        var tuning = OrdinaryContactScene.DefaultTuning with
        { RunSpeed = 100, Gravity = 10, AirMomentum = true, AirControl = 1, MaxSlopeRadians = 20f * MathF.PI / 180f };
        using var scene = new OrdinaryContactScene(tuning, (x, _) => x * MathF.Tan(slope),
            (_, _) => new(-MathF.Sin(slope), MathF.Cos(slope), 0),
            new(new(-100, -200, -100), new(100, 200, 100), -200, 200));
        var start = new MoveState { Position = new(0, 0.75f, 0), HorizontalVelocity = new(0, 100), SpeedScale = 1 };
        const float dt = 0.1f;
        var dry = CharacterMovement.StepTowards(start, Vector2.UnitY, true, dt, scene.Height, tuning, scene.Normal, scene.Physics);
        using var lease = scene.Acquire();
        var moved = scene.Binding.StepTowards(new(start, scene.Frame, null), Vector2.UnitY, true, dt, lease);
        Assert.Equal(dry, moved.State.State);
        Assert.Equal(MovementStepOutcome.Advanced, moved.Outcome);
        Assert.NotEqual(start.Position, moved.State.State.Position);
        Assert.InRange(MathF.Abs(moved.State.State.Position.Z - start.Position.Z), 19.99f, 20.01f);
    }

    [Fact]
    public void DeepLevelBuoyancyUsesResidentVerticalExtent()
    {
        var field = MovementWaterField.Level("world", 1000, -2000);
        var tuning = OrdinaryContactScene.DefaultTuning with
        { CapsuleRadius = 1, CapsuleHalfHeight = 2, SwimSurfaceSubmersionFraction = 0.5f,
            SwimBuoyancyStiffness = 10, Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -2000,
            extent: new(new(-20, -2000, -20), new(20, 1000, 20), -2000, -2000), water: field);
        var swimmer = new MoveState { Position = Vector3.Zero, Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        using var lease = scene.Acquire();
        var rise = scene.Binding.StepTowards(new(swimmer, scene.Frame, null), Vector2.Zero, false, 0.1f, lease);
        Assert.Equal(1000f, field.Extent.Maximum.Y);
        Assert.Equal(-2000f, field.Extent.Minimum.Y);
        Assert.Equal(MovementStepOutcome.Advanced, rise.Outcome);
        Assert.InRange(rise.State.State.Position.Y, 263f, 265f);
        double dt = 0.1f;
        double error = -1000;
        double coefficient = 10 * error;
        float expected = (float)(1000 + (error + coefficient * dt) * Math.Exp(-10 * dt));
        Assert.Equal(expected, rise.State.State.Position.Y);
    }

    [Fact]
    public void ReplayCommitmentAndContinuationUseOneResidentScope()
    {
        using var scene = new OrdinaryContactScene();
        var input = new MoveState { Position = new(0, 0.75f, 0), Grounded = true,
            Commitment = new(1, Vector2.UnitX, 4, 7, scene.Tuning.Gravity, 0.15f, 0, 3) };
        var commands = new[] { new MoveCommand(Vector2.UnitY, false, 0), new MoveCommand(-Vector2.UnitX, true, 1),
            new MoveCommand(Vector2.One, false, 2) };
        var dry = input;
        var scopes = new HashSet<MovementQueryScope>();
        foreach (var command in commands)
            dry = CharacterMovement.Step(dry, command, 0.1f, scene.Height, scene.Tuning, world: scene.Physics);
        using var lease = scene.Acquire();
        var state = new FramedMovementState(input, scene.Frame, null);
        foreach (var command in commands)
        {
            scopes.Add(scene.Binding.CreateOrdinaryScope(scene.Frame));
            var result = scene.Binding.Step(state, command, 0.1f, lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
        }
        scopes.Add(scene.Binding.CreateOrdinaryScope(scene.Frame));
        Assert.Single(scopes);
        Assert.Equal(dry, state.State);
        Assert.Equal(MovementCommitmentPhase.Airborne, state.State.Commitment.Phase);
        Assert.True(state.State.Position.Y > input.Position.Y);
        Assert.Equal(MovementStepOutcome.Advanced, scene.Binding.ReclassifyContinuation(state, lease).Outcome);
    }

    [Fact]
    public void TriggeredStairSignalsAndEdgeFootprintsMatchDirectDry()
    {
        float Height(float x, float z) => Math.Clamp(MathF.Floor(x / 0.28f), 0, 8) * 0.18f;
        using var scene = new OrdinaryContactScene(height: Height,
            extent: new(new(-20, -4, -20), new(20, 4, 20), 0, 1.44f));
        var start = new MoveState { Position = new(0.2f, 0.75f, 0), Grounded = true };
        var dry = CharacterMovement.StepTowards(start, Vector2.UnitX, false, 0.1f, Height, scene.Tuning, world: scene.Physics);
        using var lease = scene.Acquire();
        var result = scene.Binding.StepTowards(new(start, scene.Frame, null), Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(dry, result.State.State);
        Assert.True(result.State.State.ClimbRateEwma > 0);
        var edge = scene.State(new(20 - scene.Tuning.CapsuleRadius * scene.Tuning.FootRadiusFraction, 2.19f, 0));
        Assert.Equal(MovementStepOutcome.Advanced, scene.Binding.StepTowards(edge, Vector2.Zero, false, 0.1f, lease).Outcome);
    }

    [Fact]
    public void WaterFieldSpeedScaleFourAdvancesInsideTheFixedResidentScope()
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.SurfaceSwimmer, 7) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: new FastField());
        var input = new MoveState { Position = new(0, -0.15f, 0), Swimming = true,
            WaterExcursion = WaterExcursionState.Surface };
        using var lease = scene.Acquire();
        var moved = scene.Binding.StepTowards(new(input, scene.Frame, null), Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, moved.Outcome);
        Assert.Equal(scene.Scope, scene.Binding.CreateOrdinaryScope(scene.Frame));
        Assert.InRange(moved.State.State.Position.X, tuning.SwimSpeed * 4 * 0.1f - 0.000001f,
            tuning.SwimSpeed * 4 * 0.1f + 0.000001f);
    }

    sealed class FastField : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", 0, -4);
        public string SemanticIdentity => "level-with-speed-four";
        public long Generation => 0;
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body)
        {
            var point = _inner.SampleCentreWater(body);
            return new(point.Availability, point.Space, point.Domain, point.InWater, 4, point.Interval);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) => _inner.TraceWater(query, identity, spans, contacts);
    }
}
