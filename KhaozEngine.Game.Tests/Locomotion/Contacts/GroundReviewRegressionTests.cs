using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class GroundReviewRegressionTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new();
    const float Dt = 1f / 30f;

    static GroundStepResult Step(FootSupportScene? scene, Vector3 feet, Vector2 move,
        Func<float, float, float>? height = null, Func<float, float, Vector3>? normal = null) =>
        GroundCore.Step(feet, move, Dt, Tuning, Settings, height, normal, scene?.World, scene?.Lease);

    [Theory]
    [InlineData(false, 0f, false)]
    [InlineData(true, 0f, false)]
    [InlineData(false, 0.1f, true)]
    [InlineData(true, 0.1f, true)]
    public void PacedClimbKeepsSupportToSupportDropLimit(bool oneTick, float lower, bool seats)
    {
        float Height(float x, float z) => x < 0 ? 0.1f : x <= 1 ? 0.5f : lower;
        var feet = new Vector3(-0.1f, 0.1f, 0);
        if (!oneTick)
        {
            GroundStepResult climb = Step(null, feet, Vector2.UnitX, Height);
            Assert.Equal(GroundFooting.Walkable, climb.Footing);
            Assert.Equal(0.5f, climb.Support.Height);
            Assert.True(climb.Feet.Y < climb.Support.Height, $"{climb}");
            feet = climb.Feet;
        }

        GroundStepResult result = Step(null, feet, new Vector2(oneTick ? 1.2f : 0.2f, 0), Height);

        Assert.Equal(seats ? GroundFooting.Walkable : GroundFooting.None, result.Footing);
        Assert.False(result.Blocked, $"{result}");
        if (seats)
            Assert.True(Math.Abs(result.Feet.Y - lower) <= result.Support.HeightError, $"{result}");
    }

    [Theory]
    [InlineData(1.9f, false)]
    [InlineData(1.5f, true)]
    public void AheadOnlyCeilingUsesTheStandingRoute(float underside, bool blocks)
    {
        using FootSupportScene scene = Floor(SceneVariant.Box)
            .Slab("ceiling", new Vector3(2.5f, underside + 0.2f, 0), 0, 1.5f, 3);
        var move = new Vector2(2, 0);

        GroundStepResult result = Step(scene, Vector3.Zero, move);

        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.Equal(blocks, result.Blocked);
        if (blocks)
        {
            Assert.True(result.Feet.X > 0 && result.Feet.X < 1, $"{result}");
            Assert.False(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning),
                Pose.At(ShellGeometry.Centre(result.Feet, Tuning)), out _), $"{result}");
        }
        else Assert.Equal(move, result.Achieved);
        Assert.True(Math.Abs(result.Feet.Y) <= result.Support.HeightError, $"{result}");
    }

    [Fact]
    public void SeatPushOutsideFootprintCannotHoverAboveSlope()
    {
        using var scene = new FootSupportScene(SceneVariant.Box);
        scene.World.AddStatic(new BoxShape(new Vector3(0.5f, 0.35f, 3)),
            Pose.At(new Vector3(1.5f, 0.35f, 0)));
        static float Height(float x, float z) => 0.2f * x;
        Vector3 normal = Vector3.Normalize(new Vector3(-0.2f, 1, 0));
        var start = new Vector3(0.65f, Height(0.65f, 0), 0);

        GroundStepResult moved = Step(scene, start, new Vector2(0.1f, 0), Height, (_, _) => normal);
        GroundStepResult idle = Step(scene, moved.Feet, Vector2.Zero, Height, (_, _) => normal);

        Assert.True(moved.Blocked, $"{moved}");
        Assert.True(moved.Achieved.X > 0 && moved.Achieved.X < 0.1f, $"{moved}");
        Assert.Equal(0f, moved.Achieved.Y);
        Assert.Equal(GroundFooting.Walkable, moved.Footing);
        Assert.True(Math.Abs(moved.Feet.Y - Height(moved.Feet.X, moved.Feet.Z)) <= moved.Support.HeightError,
            $"{moved}");
        Assert.False(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning),
            Pose.At(ShellGeometry.Centre(moved.Feet, Tuning)), out _), $"{moved}");
        Assert.Equal(GroundFooting.Walkable, idle.Footing);
        Assert.True(Math.Abs(idle.Feet.Y - Height(idle.Feet.X, idle.Feet.Z)) <= idle.Support.HeightError,
            $"{idle}");
        Assert.Equal(moved.Feet, idle.Feet);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShellRouteUsesOnlyStatics(bool isStatic, bool overhead)
    {
        using FootSupportScene scene = Floor(SceneVariant.Box);
        var shape = new BoxShape(overhead ? new Vector3(1.5f, 0.1f, 3) : new Vector3(0.1f, 1.5f, 3));
        Pose pose = Pose.At(overhead ? new Vector3(2.5f, 1.6f, 0) : new Vector3(1.1f, 1.5f, 0));
        if (isStatic) scene.World.AddStatic(shape, pose);
        else scene.World.AddDynamic(shape, pose, DynamicBodyDescription.WithMass(1));
        var move = new Vector2(2, 0);

        GroundStepResult result = Step(scene, Vector3.Zero, move);

        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.Equal(isStatic, result.Blocked);
        if (isStatic) Assert.True(result.Achieved.X < 1, $"{result}");
        else Assert.Equal(move, result.Achieved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwedClimbLiftUsesOnlyStatics(bool isStatic)
    {
        using var scene = new FootSupportScene(SceneVariant.Box);
        var shape = new BoxShape(new Vector3(3, 0.1f, 3));
        Pose pose = Pose.At(new Vector3(0, 1.95f, 0));
        if (isStatic) scene.World.AddStatic(shape, pose);
        else scene.World.AddDynamic(shape, pose, DynamicBodyDescription.WithMass(1));

        GroundStepResult result = Step(scene, Vector3.Zero, Vector2.Zero, (_, _) => 0.3f);

        Assert.Equal(GroundFooting.Walkable, result.Footing);
        if (isStatic) Assert.True(result.Rise < 0.05f, $"{result}");
        else Assert.Equal((float)((double)Tuning.MaxStepClimbSpeed * Dt), result.Rise);
    }

    [Fact]
    public void ShellCannotMeetItsSteepestWalkablePlane()
    {
        MoveTuning tuning = Tuning with { StepHeight = 0.1f };

        ArgumentException error = Assert.Throws<ArgumentException>(() => ShellGeometry.Validate(tuning));

        Assert.Contains("StepHeight", error.Message);
        Assert.Contains("CapsuleRadius", error.Message);
        Assert.Contains("MaxSlope", error.Message);
        ShellGeometry.Validate(Tuning);
    }

    [Fact]
    public void TinyFiniteMoveIsExactWithARealWorld()
    {
        using FootSupportScene scene = Floor(SceneVariant.Box);
        var move = new Vector2(1e-25f, 0);

        GroundStepResult result = Step(scene, Vector3.Zero, move);

        Assert.False(result.Blocked, $"{result}");
        Assert.Equal(move, result.Achieved);
        Assert.Equal(GroundFooting.Walkable, result.Footing);
    }
}
