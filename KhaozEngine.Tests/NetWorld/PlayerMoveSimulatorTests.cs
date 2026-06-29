using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerMoveSimulatorTests
{
    static readonly Func<float, float, float> Ground = (x, z) => 2f;

    [Fact]
    public void Step_advances_and_ground_clamps()
    {
        var sim = new PlayerMoveSimulator(Ground, MoveTuning.Default);
        var s0 = new PlayerMoveState { Position = new Vector3(0f, 0f, 0f) };
        var s1 = sim.Step(s0, new MoveCommand(new Vector2(0f, 1f), false, 0f), 1f);
        Assert.True(s1.Position.Z < 0f);
        Assert.Equal(2f + MoveTuning.Default.CapsuleHalfHeight, s1.Position.Y, 4);
    }

    [Fact]
    public void Step_is_pure_does_not_mutate_input()
    {
        var sim = new PlayerMoveSimulator(Ground, MoveTuning.Default);
        var s0 = new PlayerMoveState { Position = Vector3.Zero };
        sim.Step(s0, new MoveCommand(new Vector2(1f, 0f), false, 0f), 0.5f);
        Assert.Equal(Vector3.Zero, s0.Position);
    }

    [Fact]
    public void Multi_tick_accumulates()
    {
        var sim = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        var cmd = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) s = sim.Step(s, cmd, 1f / 30f);
        Assert.Equal(-MoveTuning.Default.WalkSpeed * 3f / 30f, s.Position.Z, 4);
    }

    [Theory]
    [InlineData(float.NaN, float.NaN, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    [InlineData(0f, float.NegativeInfinity, 0f)]
    [InlineData(1f, 0f, float.NaN)]
    [InlineData(float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity)]
    public void Pathological_command_never_produces_a_non_finite_position(float moveX, float moveY, float yaw)
    {
        // A pathological command (constructed directly, as if it had bypassed the wire decode) must never drive
        // the authoritative state to a NaN/Inf position - that would replicate a poisoned ReplicatedPosition to
        // every client in range. Run several ticks so any accumulation surfaces.
        var sim = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default);
        var s = new PlayerMoveState { Position = new Vector3(3f, 0f, -2f) };
        var cmd = new MoveCommand(new Vector2(moveX, moveY), run: true, cameraYaw: yaw, jump: true);
        for (int i = 0; i < 5; i++)
        {
            s = sim.Step(s, cmd, 1f / 30f);
            Assert.True(float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z),
                $"tick {i}: non-finite position {s.Position}");
            Assert.True(float.IsFinite(s.VerticalVelocity), $"tick {i}: non-finite vVel {s.VerticalVelocity}");
        }
    }
}
