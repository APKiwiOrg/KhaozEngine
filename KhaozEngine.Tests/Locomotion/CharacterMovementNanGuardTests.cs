using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// Defense-in-depth against a non-finite position. The contract: given a FINITE input position/state,
/// <see cref="CharacterMovement"/>'s Step overloads never return a non-finite result, no matter what flows in from
/// a command or a misbehaving delegate. A poisoned (NaN/Inf) position would slip past every clamp (NaN comparisons
/// are false) and replicate to every client in range, corrupting their render of that entity. Two layers are
/// exercised here: (1) a pathological MoveCommand is already neutralized by the move gate (the camera-relative
/// basis is purely horizontal, so any non-finite axis poisons the move vector's Y and its squared length, failing
/// the apply gate); (2) the explicit finite guard holds the last good position when a misbehaving ground/bounds
/// delegate would otherwise inject a NaN the gate cannot catch.
/// </summary>
public class CharacterMovementNanGuardTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly Func<float, float, float> NanGround = (x, z) => float.NaN;
    static readonly MoveTuning T = MoveTuning.Default;

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // --- Explicit finite guard: a non-finite delegate result must not become the position. ---

    [Fact]
    public void Horizontal_step_holds_position_when_the_ground_sample_is_non_finite()
    {
        var start = new Vector3(5f, 0.9f, -3f);
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
        Vector3 r = CharacterMovement.Step(start, cmd, 1f / 30f, NanGround, T);
        Assert.True(Finite(r), $"non-finite position {r}");
        Assert.Equal(start, r);   // a poisoned ground reading holds the last good position
    }

    [Fact]
    public void Vertical_step_holds_state_when_a_clamp_returns_non_finite()
    {
        var start = new MoveState { Position = new Vector3(5f, 0.9f, -3f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
        // A misbehaving play-area bound (the clampXz delegate) returns NaN; the step must not propagate it.
        MoveState r = CharacterMovement.Step(start, cmd, 1f / 30f, Flat, T,
            clampXz: (x, z) => new Vector2(float.NaN, float.NaN));
        Assert.True(Finite(r.Position), $"non-finite position {r.Position}");
        Assert.True(float.IsFinite(r.VerticalVelocity), $"non-finite vVel {r.VerticalVelocity}");
    }

    // --- The move gate already neutralizes a pathological command (regression lock). ---

    [Theory]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(1f, 0f, float.NaN)]
    [InlineData(float.NaN, float.NaN, float.NaN)]
    public void Horizontal_step_neutralizes_a_pathological_command(float moveX, float moveY, float yaw)
    {
        var start = new Vector3(5f, 0.9f, -3f);
        var cmd = new MoveCommand(new Vector2(moveX, moveY), run: true, cameraYaw: yaw);
        Vector3 r = CharacterMovement.Step(start, cmd, 1f / 30f, Flat, T);
        Assert.True(Finite(r), $"non-finite position {r}");
    }

    [Theory]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(1f, 0f, float.NaN)]
    [InlineData(float.NaN, float.NaN, float.NaN)]
    public void Vertical_step_neutralizes_a_pathological_command(float moveX, float moveY, float yaw)
    {
        var start = new MoveState { Position = new Vector3(5f, 0.9f, -3f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(moveX, moveY), run: true, cameraYaw: yaw, jump: true);
        MoveState r = CharacterMovement.Step(start, cmd, 1f / 30f, Flat, T);
        Assert.True(Finite(r.Position), $"non-finite position {r.Position}");
        Assert.True(float.IsFinite(r.VerticalVelocity), $"non-finite vVel {r.VerticalVelocity}");
    }
}
