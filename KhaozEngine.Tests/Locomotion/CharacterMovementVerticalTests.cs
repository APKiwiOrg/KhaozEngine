using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// Vertical character physics: the <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float,
/// Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, KhaozEngine.Physics.IPhysicsWorld?,
/// Func{float, float, Vector2}?)"/> overload (gravity, jump, coyote, jump-buffer, air control). Headless: state is
/// constructed frame-by-frame and the same step runs on server and client.
/// </summary>
public class CharacterMovementVerticalTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly Func<float, float, float> FarBelow = (x, z) => -1000f;   // so a player never lands
    // Half-height 0 makes groundY == groundHeight, so the vertical math reads cleanly.
    static readonly MoveTuning T = MoveTuning.Default with { CapsuleHalfHeight = 0f };

    static MoveCommand Jump => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
    static MoveCommand Idle => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
    static MoveCommand Forward(bool jump = false) => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: jump);

    [Fact]
    public void Gravity_makes_an_airborne_player_fall_and_accelerate()
    {
        var s = new MoveState { Position = new Vector3(0f, 10f, 0f), Grounded = false };
        var s1 = CharacterMovement.Step(s, Idle, 1f / 60f, FarBelow, T);
        var s2 = CharacterMovement.Step(s1, Idle, 1f / 60f, FarBelow, T);

        Assert.True(s1.VerticalVelocity < 0f, $"vVel1 {s1.VerticalVelocity}");
        Assert.True(s2.VerticalVelocity < s1.VerticalVelocity, "fall must accelerate");
        Assert.True(s2.Position.Y < s1.Position.Y && s1.Position.Y < 10f, "must descend");
        Assert.False(s2.Grounded);
    }

    [Fact]
    public void Fall_speed_clamps_to_max_fall_speed()
    {
        var s = new MoveState { Position = new Vector3(0f, 10f, 0f), Grounded = false };
        for (int i = 0; i < 400; i++) s = CharacterMovement.Step(s, Idle, 1f / 60f, FarBelow, T);
        Assert.Equal(-T.MaxFallSpeed, s.VerticalVelocity, 3);
        Assert.True(s.VerticalVelocity >= -T.MaxFallSpeed, "must never exceed terminal");
    }

    [Fact]
    public void Landing_clamps_y_zeroes_vertical_velocity_and_grounds()
    {
        var s = new MoveState { Position = new Vector3(0f, 0.5f, 0f), Grounded = false };
        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, Idle, 1f / 60f, Flat, T);
        Assert.True(s.Grounded, "should have landed");
        Assert.Equal(0f, s.Position.Y, 4);            // groundHeight (0) + halfHeight (0)
        Assert.Equal(0f, s.VerticalVelocity, 4);
    }

    [Fact]
    public void Jump_launches_when_grounded()
    {
        var s = new MoveState { Position = Vector3.Zero, Grounded = true };
        s = CharacterMovement.Step(s, Jump, 1f / 60f, Flat, T);
        Assert.Equal(T.JumpSpeed, s.VerticalVelocity, 3);
        Assert.False(s.Grounded);
    }

    [Fact]
    public void Jump_does_not_launch_when_airborne_beyond_coyote()
    {
        var s = new MoveState { Position = new Vector3(0f, 10f, 0f), Grounded = false, TimeSinceGrounded = 1f };
        s = CharacterMovement.Step(s, Jump, 1f / 60f, FarBelow, T);
        Assert.True(s.VerticalVelocity < 0f, $"airborne jump must be rejected: vVel {s.VerticalVelocity}");
    }

    [Fact]
    public void Coyote_time_allows_a_jump_just_after_leaving_ground()
    {
        // Left the ground 0.05 s ago (within the 0.1 s coyote window); ground far below so we stay airborne.
        var s = new MoveState { Position = new Vector3(0f, 10f, 0f), Grounded = false, TimeSinceGrounded = 0.05f };
        s = CharacterMovement.Step(s, Jump, 1f / 60f, FarBelow, T);
        Assert.Equal(T.JumpSpeed, s.VerticalVelocity, 3);
    }

    [Fact]
    public void No_double_jump_when_pressing_again_at_the_apex()
    {
        // Jump from the ground, coast (no input) up to the apex, then press jump again: it must be rejected
        // (coyote was consumed by the first jump), so vertical velocity stays near zero, not back at JumpSpeed.
        var s = new MoveState { Position = Vector3.Zero, Grounded = true };
        s = CharacterMovement.Step(s, Jump, 1f / 60f, FarBelow, T);   // launch
        Assert.Equal(T.JumpSpeed, s.VerticalVelocity, 3);

        // Coast up until the apex (velocity crosses to non-positive). FarBelow ground so we never land.
        int guard = 0;
        while (s.VerticalVelocity > 0f && guard++ < 200) s = CharacterMovement.Step(s, Idle, 1f / 60f, FarBelow, T);

        float beforePress = s.VerticalVelocity;
        var afterPress = CharacterMovement.Step(s, Jump, 1f / 60f, FarBelow, T);
        Assert.True(afterPress.VerticalVelocity < beforePress + 0.001f,
            $"apex jump should be rejected, got vVel {afterPress.VerticalVelocity}");
        Assert.True(afterPress.VerticalVelocity < 1f, $"no relaunch at apex: {afterPress.VerticalVelocity}");
    }

    [Fact]
    public void Buffered_jump_fires_on_landing()
    {
        // Press jump once while airborne (outside coyote), release, then land within the buffer window.
        // Returns the peak vertical velocity observed (a launch shows up as ~JumpSpeed).
        float PeakVelocity(MoveTuning tuning)
        {
            var s = new MoveState { Position = new Vector3(0f, 0.08f, 0f), Grounded = false, TimeSinceGrounded = 1f };
            float maxV = float.NegativeInfinity;
            for (int i = 0; i < 12; i++)
            {
                MoveCommand cmd = i == 0 ? Jump : Idle;   // pressed only on the first airborne tick
                s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning);
                maxV = MathF.Max(maxV, s.VerticalVelocity);
            }
            return maxV;
        }

        float bufferedPeak = PeakVelocity(T);
        float noBufferPeak = PeakVelocity(T with { JumpBuffer = 0f });

        Assert.True(bufferedPeak >= T.JumpSpeed - 0.5f, $"buffered jump should fire on landing: peak {bufferedPeak}");
        Assert.True(noBufferPeak < 1f, $"with no buffer the stale press must not fire: peak {noBufferPeak}");
    }

    [Fact]
    public void Air_control_scales_airborne_horizontal_movement()
    {
        var tuning = T with { AirControl = 0.5f };
        var grounded = CharacterMovement.Step(
            new MoveState { Position = Vector3.Zero, Grounded = true }, Forward(), 1f, Flat, tuning);
        var airborne = CharacterMovement.Step(
            new MoveState { Position = new Vector3(0f, 100f, 0f), Grounded = false, TimeSinceGrounded = 1f },
            Forward(), 1f, FarBelow, tuning);

        Assert.True(grounded.Position.Z < 0f && airborne.Position.Z < 0f);
        Assert.Equal(0.5f * grounded.Position.Z, airborne.Position.Z, 3);   // half the horizontal travel in air
    }

    [Fact]
    public void Deterministic_same_inputs_same_output()
    {
        var s0 = new MoveState { Position = new Vector3(1f, 3f, 2f), Grounded = false, VerticalVelocity = -2f };
        var a = CharacterMovement.Step(s0, Forward(jump: true), 0.123f, Flat, T);
        var b = CharacterMovement.Step(s0, Forward(jump: true), 0.123f, Flat, T);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
        Assert.Equal(a.Grounded, b.Grounded);
    }

    [Fact]
    public void Legacy_vector3_overload_still_instant_ground_clamps()
    {
        // The original overload (no vertical state) must be unchanged: Y is a pure function of XZ.
        var t = MoveTuning.Default with { CapsuleHalfHeight = 0.9f };
        Func<float, float, float> ground = (x, z) => 5f;
        Vector3 p = CharacterMovement.Step(new Vector3(0f, 100f, 0f), Forward(), 0.5f, ground, t);
        Assert.Equal(5f + 0.9f, p.Y, 4);   // snapped to ground+halfHeight regardless of the high start Y
    }
}
