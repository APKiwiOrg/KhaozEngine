using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Candidate vertical motion only. The caller must prove the complete path before publishing it.
public class SurfaceWaterMotionTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementDomainKey Lake = new("world", "lake");
    static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleHalfHeight = 0.75f,
        CapsuleRadius = 0.25f,
        SwimSurfaceSubmersionFraction = 0.5f,
        SwimBuoyancyStiffness = 8f,
        Gravity = 20f,
        JumpSpeed = 30f
    };

    static MovementWaterPoint Water(float bed = -100, float surface = 0, bool free = true) =>
        new(MovementAvailability.Known, Room, Lake, true, 1,
            new MovementWaterInterval(bed, surface, free ? surface : surface + 10, free, "bed", "top"));

    [Theory]
    [InlineData(-2f, 0f, 1f, 30)]
    [InlineData(-100f, 0f, 1f, 30)]
    [InlineData(-100f, 0f, 0.5f, 30)]
    [InlineData(-640f, -600f, 1f, 30)]
    [InlineData(-100f, 0f, 1f, 60)]
    public void SurfaceJumpUsesIndependentApexAtAnyBedDepth(float bed, float surface, float apex, int hz)
    {
        float dt = 1f / hz;
        float launch = MoveTuning.JumpSpeedForApex(apex, 20, dt);
        MoveState state = new() { Position = new(0, surface, 0), Swimming = true };
        MovementWaterPoint point = Water(bed, surface);
        var first = Propose(state, false, true, point, dt, Tuning, launch);
        Assert.Equal(MovementAvailability.Known, first.Status);
        Assert.True(first.Launched);
        Assert.True(first.Vertical.X > surface);
        Assert.True(first.Vertical.Y > 0);
        float highest = first.Vertical.X;
        state.Position.Y = first.Vertical.X;
        state.VerticalVelocity = first.Vertical.Y;
        // One finite trajectory, not repeated test execution or an artificial load fixture.
        for (int tick = 1; tick < hz; tick++)
        {
            var next = Propose(state, true, tick == 2, point, dt, Tuning, launch);
            Assert.Equal(MovementAvailability.Known, next.Status);
            Assert.False(next.Launched);
            state.Position.Y = next.Vertical.X;
            state.VerticalVelocity = next.Vertical.Y;
            highest = Math.Max(highest, state.Position.Y);
        }
        Assert.InRange(highest - surface, apex - 0.004f, apex + 0.0002f);
    }

    [Theory]
    [InlineData(-0.03f, true)]
    [InlineData(0.03f, true)]
    [InlineData(0f, true)]
    public void SurfaceContactBandIncludesBothBoundaries(float y, bool eligible)
    {
        var result = Propose(new() { Position = new(0, y, 0) }, false, true, Water(), 1f / 30, Tuning, 7);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.Equal(eligible, result.Launched);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void NextFloatOutsideContactBandCannotLaunch(int sign)
    {
        float y = sign < 0 ? MathF.BitDecrement(-0.03f) : MathF.BitIncrement(0.03f);
        var result = Propose(new() { Position = new(0, y, 0) }, false, true, Water(), 1f / 30, Tuning, 7);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.False(result.Launched);
        Assert.InRange(Math.Abs(result.Vertical.Y), 0, 0.06f);
    }

    [Fact]
    public void BelowBandPressDoesNotChangeBuoyancyOrBufferALaterLaunch()
    {
        MoveState state = new() { Position = new(0, -0.2f, 0), JumpBufferRemaining = 1 };
        var pressed = Propose(state, false, true, Water(), 1f / 30, Tuning, 7);
        var idle = Propose(state, false, false, Water(), 1f / 30, Tuning, 7);
        Assert.Equal(idle, pressed);
        state.Position.Y = 0;
        var later = Propose(state, false, false, Water(), 1f / 30, Tuning, 7);
        Assert.False(later.Launched);
        Assert.Equal(Vector2.Zero, later.Vertical);
    }

    [Fact]
    public void AnAirbornePressUsesGravityAndCannotRecaptureOrRelaunch()
    {
        MoveState state = new() { Position = Vector3.Zero, VerticalVelocity = 5, Swimming = true };
        var result = Propose(state, true, true, Water(), 0.1f, Tuning, 50);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.False(result.Launched);
        Assert.InRange(result.Vertical.X, 0.299999f, 0.300001f);
        Assert.InRange(result.Vertical.Y, 2.999999f, 3.000001f);
    }

    [Fact]
    public void AirborneMotionCanCrossKnownDrySpaceWithoutInventingAWaterSurface()
    {
        var dry = new MovementWaterPoint(MovementAvailability.Known, Room, null, false, 1, null);
        var result = Propose(new() { Position = new(0, 4, 0), VerticalVelocity = -2 }, true, false,
            dry, 0.1f, Tuning, 7);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.InRange(result.Vertical.X, 3.599999f, 3.600001f);
        Assert.InRange(result.Vertical.Y, -4.000001f, -3.999999f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloodedLocalCeilingNeverBecomesASurfaceTarget(bool jump)
    {
        var result = Propose(new() { Position = Vector3.Zero }, false, jump, Water(free: false),
            1f / 30, Tuning, 7);
        Assert.Equal(MovementAvailability.Unresolved, result.Status);
        Assert.Equal(Vector2.Zero, result.Vertical);
        Assert.False(result.Launched);
    }

    [Fact]
    public void BuoyancyIsTheSameAnalyticSettleAcrossASubdividedTick()
    {
        MoveState state = new() { Position = new(0, -0.3f, 0), VerticalVelocity = -1 };
        var whole = Propose(state, false, false, Water(), 0.1f, Tuning, 7);
        var half = Propose(state, false, false, Water(), 0.05f, Tuning, 7);
        state.Position.Y = half.Vertical.X;
        state.VerticalVelocity = half.Vertical.Y;
        var remainder = Propose(state, false, false, Water(), 0.05f, Tuning, 7);
        Assert.Equal(MovementAvailability.Known, whole.Status);
        Assert.InRange(whole.Vertical.X, -0.288f, -0.287f);
        Assert.InRange(whole.Vertical.Y, 0.77f, 0.78f);
        Assert.InRange(Vector2.Distance(whole.Vertical, remainder.Vertical), 0, 0.000001f);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved)]
    [InlineData(MovementAvailability.Stale)]
    [InlineData(MovementAvailability.Invalid)]
    [InlineData(MovementAvailability.CapacityExceeded)]
    public void MissingFactsProduceNoCandidate(MovementAvailability status)
    {
        var point = new MovementWaterPoint(status, default, null, false, 0, null);
        var result = Propose(new() { Position = new(0, 3, 0) }, true, true, point, 0.1f, Tuning, 7);
        Assert.Equal(status, result.Status);
        Assert.Equal(Vector2.Zero, result.Vertical);
        Assert.False(result.Launched);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidTimeCannotPublishCandidate(float dt)
    {
        var result = Propose(default, false, true, Water(), dt, Tuning, 7);
        Assert.Equal(MovementAvailability.Invalid, result.Status);
        Assert.Equal(Vector2.Zero, result.Vertical);
        Assert.False(result.Launched);
    }

    delegate MovementAvailability ProposeDelegate(in MoveState state, bool airborneFromWater, bool jump,
        in MovementWaterPoint point, float dt, in MoveTuning tuning, float surfaceJumpSpeed,
        out Vector2 vertical, out bool launched);

    static (MovementAvailability Status, Vector2 Vertical, bool Launched) Propose(MoveState state,
        bool airborne, bool jump, MovementWaterPoint point, float dt, MoveTuning tuning, float speed)
    {
        Type? type = typeof(MoveState).Assembly.GetType("KhaozEngine.Locomotion.SurfaceWaterMotion");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod("TryPropose", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invoke = method.CreateDelegate<ProposeDelegate>();
        var status = invoke(state, airborne, jump, point, dt, tuning, speed, out var vertical, out bool launched);
        return (status, vertical, launched);
    }
}
