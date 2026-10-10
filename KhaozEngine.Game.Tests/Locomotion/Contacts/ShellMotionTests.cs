// Every expectation below is derived from the installed geometry. The default shell spans feet + 0.4 to
// feet + 1.8 with radius 0.4, so its centre is at feet + 1.1 and its side touches a wall face at x when the
// centre is at x - 0.4.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ShellMotionTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);
    const float Skin = ShellMotion.ContactSkin;

    static FootSupportScene Floor() => new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 6, -5, 5, 0);

    /// <summary>A floor and a 3 m wall whose face is the plane x 1.</summary>
    static FootSupportScene Wall() => Floor().Flat("wall", 1, 2, -5, 5, 3);

    static ShellSweep Sweep(IPhysicsWorld? world, Vector3 feet, Vector2 move, float lift = 0f) =>
        ShellMotion.Sweep(world, feet, lift, move, Tuning, CosMaxSlope);

    static bool Overlaps(IPhysicsWorld world, Vector3 feet) =>
        world.ComputePenetration(ShellGeometry.Shape(Tuning), Pose.At(ShellGeometry.Centre(feet, Tuning)), out _);

    [Fact]
    public void FreeMoveIsExact()
    {
        using FootSupportScene scene = Floor();
        Vector3 feet = Vector3.Zero;
        Vector2 move = new(0.15f, 0.05f);

        float lift = ShellMotion.Lift(scene.World, feet, Tuning);
        ShellSweep sweep = Sweep(scene.World, feet, move, lift);

        Assert.Equal(Tuning.StepHeight, lift);
        Assert.Equal(move, sweep.Achieved);
        Assert.False(sweep.Blocked);
    }

    [Fact]
    public void HeadOnWallStopsAtTheSkin()
    {
        using FootSupportScene scene = Wall();

        ShellSweep sweep = Sweep(scene.World, new Vector3(0.39f, 0, 0), new Vector2(0.3f, 0));

        Assert.Equal(1f - 0.4f - 0.001f - 0.39f, sweep.Achieved.X, 1e-5f);
        Assert.Equal(0f, sweep.Achieved.Y, 1e-6f);
        Assert.True(sweep.Blocked);
    }

    [Theory]
    [InlineData(30f)]
    [InlineData(60f)]
    public void AngledWallKeepsTheTangent(float degrees)
    {
        using FootSupportScene scene = Wall();
        // The shell starts 0.1 from the face and the move's x component exceeds that at both angles.
        float angle = FootSupportScenes.Radians(degrees);
        Vector2 move = new(0.3f * MathF.Cos(angle), 0.3f * MathF.Sin(angle));

        ShellSweep sweep = Sweep(scene.World, new Vector3(0.5f, 0, 0), move);

        Assert.True(sweep.Blocked);
        Assert.InRange(sweep.Achieved.X, 0.1f - 2 * Skin, 0.1f);
        // The tangent is kept up to the float rounding of advance plus the rest of the move.
        Assert.Equal(move.Y, sweep.Achieved.Y, 1e-6f);
    }

    [Fact]
    public void InnerCornerStopsBoth()
    {
        // Wall faces at x 1 and z 1 meet in a right angle. The shell fits the corner with its centre at
        // (0.6, 0.6), 0.1 from each face.
        using FootSupportScene scene = Floor().Flat("wallX", 1, 2, -5, 2, 3).Flat("wallZ", -4, 2, 1, 2, 3);

        ShellSweep sweep = Sweep(scene.World, new Vector3(0.5f, 0, 0.5f), new Vector2(0.3f, 0.3f));

        Assert.True(sweep.Blocked);
        Assert.InRange(sweep.Achieved.X, 0.1f - 2 * Skin, 0.1f);
        Assert.InRange(sweep.Achieved.Y, 0.1f - 2 * Skin, 0.1f);
    }

    // A gap of 0 starts exactly touching, where the sweep reports distance 0 with a zero normal.
    [Theory]
    [InlineData(Skin)]
    [InlineData(0f)]
    public void MovingParallelAlongAWallKeepsFullSpeed(float gap)
    {
        using FootSupportScene scene = Wall();
        Vector2 move = new(0, 0.3f);

        ShellSweep sweep = Sweep(scene.World, new Vector3(1f - 0.4f - gap, 0, -1), move);

        Assert.Equal(move, sweep.Achieved);
        Assert.False(sweep.Blocked);
    }

    [Fact]
    public void LowCrateNeverTouchesTheShell()
    {
        // The crate top at 0.3 is below the shell bottom at 0.4, even without a lift.
        using FootSupportScene scene = Floor().Flat("crate", 0.5f, 1.5f, -1, 1, FootSupportScenes.CrateTop);
        Vector2 move = new(0.3f, 0);

        ShellSweep sweep = Sweep(scene.World, Vector3.Zero, move);

        Assert.Equal(move, sweep.Achieved);
        Assert.False(sweep.Blocked);
    }

    [Fact]
    public void CeilingLimitsTheLift()
    {
        // The ceiling underside is 0.1 above the shell top at 1.8.
        using FootSupportScene scene = Floor().Slab("ceiling", new Vector3(0, 2.1f, 0), 0, 3, 3, 0.2f);

        float lift = ShellMotion.Lift(scene.World, Vector3.Zero, Tuning);

        Assert.Equal(0.1f - Skin, lift, 1e-5f);
    }

    [Fact]
    public void RecoveryPushesTheShellOut()
    {
        // The box face at x 0.35 is 0.05 inside the shell side at x 0.4.
        using FootSupportScene scene = Floor().Flat("box", 0.35f, 1.35f, -1, 1, 3);
        Assert.True(Overlaps(scene.World, Vector3.Zero));

        Vector3 feet = ShellMotion.Recover(scene.World, Vector3.Zero, Tuning, out bool cleared);

        Assert.True(cleared);
        Assert.False(Overlaps(scene.World, feet));
        Assert.InRange(feet.X, -0.05f - 2 * Skin, -0.05f);
        Assert.Equal(0f, feet.Y, 1e-5f);
        Assert.Equal(0f, feet.Z, 1e-5f);
    }

    [Fact]
    public void DeepOverlapReportsNotCleared()
    {
        // Faces at x -0.3 and 0.3 leave a gap narrower than the 0.8 shell, so each push out of one box lands
        // into the other: an overlap of 0.1, then 0.201 on alternating sides for every later pass.
        using FootSupportScene scene = Floor().Flat("left", -2, -0.3f, -1, 1, 3).Flat("right", 0.3f, 2, -1, 1, 3);

        Vector3 feet = ShellMotion.Recover(scene.World, Vector3.Zero, Tuning, out bool cleared);

        Assert.False(cleared, $"Recovered to {feet}");
        Assert.True(float.IsFinite(feet.X) && float.IsFinite(feet.Y) && float.IsFinite(feet.Z), $"{feet}");
    }

    [Fact]
    public void EnclosingBoxClearsInOnePush()
    {
        // The block encloses the whole shell. Its true MTV is the 1.4 m exit through a side face.
        using FootSupportScene scene = Floor().Flat("block", -1, 1, -1, 1, 3);

        Vector3 feet = ShellMotion.Recover(scene.World, Vector3.Zero, Tuning, out bool cleared);

        Assert.True(cleared, $"Recovered to {feet}");
        Assert.True(float.IsFinite(feet.X) && float.IsFinite(feet.Y) && float.IsFinite(feet.Z), $"{feet}");
        Assert.False(Overlaps(scene.World, feet));
        Assert.True(MathF.Max(MathF.Abs(feet.X), MathF.Abs(feet.Z)) >= 1.4f, $"{feet}");
    }

    [Fact]
    public void NullWorldMovesFreely()
    {
        Vector3 feet = new(3, -2, 5);
        Vector2 move = new(0.15f, -0.05f);

        ShellSweep sweep = Sweep(null, feet, move, Tuning.StepHeight);
        Vector3 recovered = ShellMotion.Recover(null, feet, Tuning, out bool cleared);

        Assert.Equal(move, sweep.Achieved);
        Assert.False(sweep.Blocked);
        Assert.Equal(Tuning.StepHeight, ShellMotion.Lift(null, feet, Tuning));
        Assert.Equal(feet, recovered);
        Assert.True(cleared);
    }
}
