using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class CharacterMovementTests
{
    static readonly Func<float, float, float> FlatGround = (x, z) => 0f;
    static readonly MoveTuning Tuning = MoveTuning.Default with { CapsuleHalfHeight = 0f };

    static MoveCommand Cmd(float x, float y, bool run = false, float yaw = 0f) =>
        new(new Vector2(x, y), run, yaw);

    [Fact]
    public void W_at_yaw_zero_moves_toward_negative_z()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, FlatGround, Tuning);
        Assert.True(p.Z < 0f, p.ToString());
        Assert.True(MathF.Abs(p.X) < 1e-4f, p.ToString());
        Assert.Equal(Tuning.WalkSpeed, MathF.Abs(p.Z), 4);
    }

    [Fact]
    public void Diagonal_is_normalized()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f), 1f, FlatGround, Tuning);
        float horiz = new Vector2(p.X, p.Z).Length();
        Assert.Equal(Tuning.WalkSpeed, horiz, 3);
    }

    [Fact]
    public void Run_is_faster_than_walk()
    {
        Vector3 walk = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, FlatGround, Tuning);
        Vector3 run = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f, run: true), 1f, FlatGround, Tuning);
        Assert.True(MathF.Abs(run.Z) > MathF.Abs(walk.Z));
        Assert.Equal(Tuning.RunSpeed, MathF.Abs(run.Z), 3);
    }

    [Fact]
    public void Idle_does_not_move_horizontally()
    {
        Vector3 p = CharacterMovement.Step(new Vector3(5f, 0f, 7f), Cmd(0f, 0f), 1f, FlatGround, Tuning);
        Assert.Equal(5f, p.X, 6);
        Assert.Equal(7f, p.Z, 6);
    }

    [Fact]
    public void Y_clamps_to_ground_plus_half_height()
    {
        Func<float, float, float> bumpy = (x, z) => 5f;
        var t = MoveTuning.Default with { CapsuleHalfHeight = 0.9f };
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 0.5f, bumpy, t);
        Assert.Equal(5f + 0.9f, p.Y, 4);
    }

    [Fact]
    public void Camera_relative_yaw_rotates_movement()
    {
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f, yaw: MathF.PI / 2f), 1f, FlatGround, Tuning);
        Assert.True(p.X < 0f, p.ToString());
        Assert.True(MathF.Abs(p.Z) < 1e-3f, p.ToString());
    }

    [Fact]
    public void Step_onto_too_steep_ground_is_rejected()
    {
        // A near-vertical face rising toward -Z, which is where Cmd(0,1) at yaw 0 travels. The normal and the height
        // describe ONE surface on purpose: the gate is direction-aware, so a steep normal over ground that does not
        // stand above the feet is a DESCENT and would (correctly) not be refused.
        Func<float, float, Vector3> steep = (x, z) => Vector3.Normalize(new Vector3(0f, 0.05f, 1f));
        Func<float, float, float> wall = (x, z) => MathF.Max(0f, -z) * 20f;
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, wall, Tuning, steep);
        Assert.True(MathF.Abs(p.X) < 1e-6f && MathF.Abs(p.Z) < 1e-6f, p.ToString());
    }

    // A ground normal leaning in +Z so the surface slope (angle from +Y) is exactly `degrees`: it rises toward -Z,
    // the direction Cmd(0,1) travels at yaw 0, so walking forward is walking UP it.
    static Func<float, float, Vector3> SlopeNormal(float degrees)
    {
        float a = degrees * MathF.PI / 180f;
        var n = new Vector3(0f, MathF.Cos(a), MathF.Sin(a));
        return (x, z) => n;
    }

    // The height field of that same surface, so the normal and the ground the gate samples cannot disagree.
    static Func<float, float, float> SlopeGround(float degrees)
    {
        float grade = MathF.Tan(degrees * MathF.PI / 180f);
        return (x, z) => -z * grade;
    }

    [Fact]
    public void Default_max_slope_blocks_a_47_degree_wall()
    {
        // The rim mountains read as "too steep to climb": the default budget must reject a 47 deg slope
        // (it did NOT at the old 50 deg default - that let you walk up near-cliffs).
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, SlopeGround(47f), Tuning, SlopeNormal(47f));
        Assert.True(MathF.Abs(p.X) < 1e-6f && MathF.Abs(p.Z) < 1e-6f, $"climbed a 47 deg wall: {p}");
    }

    [Fact]
    public void Default_max_slope_allows_a_gentle_30_degree_slope()
    {
        // A walkable hill (well under the budget) still moves - the gate only blocks the rim, not normal terrain.
        Vector3 p = CharacterMovement.Step(Vector3.Zero, Cmd(0f, 1f), 1f, SlopeGround(30f), Tuning, SlopeNormal(30f));
        Assert.True(MathF.Abs(p.Z) > 0.1f, $"a 30 deg slope should be walkable: {p}");
    }

    [Fact]
    public void Deterministic_same_inputs_same_output()
    {
        Vector3 a = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f, run: true, yaw: 0.7f), 0.123f, FlatGround, Tuning);
        Vector3 b = CharacterMovement.Step(Vector3.Zero, Cmd(1f, 1f, run: true, yaw: 0.7f), 0.123f, FlatGround, Tuning);
        Assert.Equal(a, b);
    }
}
