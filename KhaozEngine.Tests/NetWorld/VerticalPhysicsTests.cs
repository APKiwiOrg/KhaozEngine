using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Vertical physics across the NetWorld movement stack: the simulator (this file's first tests), the replicated
/// <see cref="MovementState"/> wire round-trip, and the authoritative servers. All headless.
/// </summary>
public class VerticalPhysicsTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Idle = new(Vector2.Zero, run: false, cameraYaw: 0f);

    [Fact]
    public void Simulator_drops_an_airborne_player()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var s = new PlayerMoveState { Position = new Vector3(0f, 20f, 0f) };   // above ground, grounded=false
        var a = sim.Step(s, Idle, 1f / 30f);
        var b = sim.Step(a, Idle, 1f / 30f);
        Assert.True(a.VerticalVelocity < 0f);
        Assert.True(b.Position.Y < a.Position.Y && a.Position.Y < 20f);
        Assert.False(b.Grounded);
    }

    [Fact]
    public void Simulator_jump_launches_then_lands()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        s = sim.Step(s, Idle, 1f / 30f);                              // settle grounded
        Assert.True(s.Grounded);

        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        s = sim.Step(s, jump, 1f / 30f);
        Assert.True(s.VerticalVelocity > 0f && !s.Grounded);          // launched

        for (int i = 0; i < 120; i++) s = sim.Step(s, Idle, 1f / 30f);
        Assert.True(s.Grounded);
        Assert.Equal(MoveTuning.Default.CapsuleHalfHeight, s.Position.Y, 4);   // landed on flat ground
        Assert.Equal(0f, s.VerticalVelocity, 4);
    }

    [Fact]
    public void Simulator_bounds_clamp_keeps_an_airborne_player_airborne()
    {
        // Guards the fix: the play-area clamp must clamp XZ only, NOT re-snap Y to the ground (which would
        // teleport a jumping player down at the wall).
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var s = new PlayerMoveState { Position = new Vector3(4.9f, 0f, 0f) };
        s = sim.Step(s, Idle, 1f / 30f);                              // settle grounded at the edge

        var eastJump = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: true);
        var eastRun = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f);
        s = sim.Step(s, eastJump, 1f / 30f);                          // jump and push into the wall
        for (int i = 0; i < 4; i++) s = sim.Step(s, eastRun, 1f / 30f);

        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped bounds to {s.Position}");
        Assert.True(s.Position.X <= 5f + 1e-3f);
        Assert.False(s.Grounded);
        Assert.True(s.Position.Y > MoveTuning.Default.CapsuleHalfHeight + 0.1f,
            $"airborne Y was snapped to ground at the wall: {s.Position.Y}");
    }
}
