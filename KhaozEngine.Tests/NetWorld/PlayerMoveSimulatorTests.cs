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
}
