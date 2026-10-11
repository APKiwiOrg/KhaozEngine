using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactMediumRouteTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default with
    { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f, StepHeight = 0.3f };
    static readonly GroundCoreSettings Settings = new();
    static readonly MovementSpaceKey Room = new("world", "room");

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void P1LiftedSideCannotCrossForbiddenWaterBetweenDryEndpoints(int hz)
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 0.35f;
        scene.WaterMaxX = 0.4f;
        scene.WaterBottom = 1.6f;
        var queries = scene.Queries();
        ShellSweep lifted = ShellMotion.SweepWithQueries(queries, Vector3.Zero, 0.3f, new(0.8f, 0),
            Tuning, MathF.Cos(Tuning.MaxSlopeRadians));
        ShellSweep standing = ShellMotion.SweepWithQueries(queries, Vector3.Zero, 0, new(0.8f, 0),
            Tuning, MathF.Cos(Tuning.MaxSlopeRadians));

        Assert.True(lifted.Blocked);
        Assert.InRange(lifted.Achieved.X, 0, 0.11f);
        Assert.Equal(new Vector2(0.8f, 0), standing.Achieved);
        GroundStepResult step = GroundCore.Step(Vector3.Zero, new(0.8f, 0), 1f / hz,
            Tuning, Settings, queries);
        Assert.Equal(new Vector3(0.8f, 0, 0), step.Feet);
        Assert.Equal(0f, step.StepRise);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void P2RecoveryCannotPushTheFullBodyAcrossShore(int hz)
    {
        using var scene = new RouteScene(configure: world =>
            world.AddStatic(new BoxShape(new(0.2f, 2, 2)), Pose.At(new(0.5f, 1, 0))));
        scene.WaterMinX = 1;
        Vector3 input = new(0.7f, 0, 0);
        Vector3 recovered = ShellMotion.RecoverWithQueries(scene.Queries(), input, Tuning, out bool cleared);

        Assert.False(cleared);
        Assert.Equal(input, recovered);
        GroundStepResult step = GroundCore.Step(input, Vector2.Zero, 1f / hz, Tuning, Settings, scene.Queries());
        Assert.Equal(input, step.Feet);
        Assert.Equal(0f, step.StepRise);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void P3ShoreTangentRetracesTheShellAndStopsAtARealWall(int hz)
    {
        using var scene = new RouteScene(configure: world =>
            world.AddStatic(new BoxShape(new(4, 2, 0.05f)), Pose.At(new(0, 1, 1))));
        scene.WaterMinX = 1;
        ShellSweep sweep = ShellMotion.SweepWithQueries(scene.Queries(), Vector3.Zero, 0, new(2, 2),
            Tuning, MathF.Cos(Tuning.MaxSlopeRadians));

        Assert.True(sweep.Blocked);
        Assert.InRange(sweep.Achieved.X, 0.74f, 0.75f);
        Assert.InRange(sweep.Achieved.Y, 0.69f, 0.70f);
        Assert.True(scene.Environment.CoverageCalls > 1);
        GroundStepResult step = GroundCore.Step(Vector3.Zero, new(2, 2), 1f / hz,
            Tuning, Settings, scene.Queries());
        Assert.InRange(step.Feet.X, 0.74f, 0.75f);
        Assert.InRange(step.Feet.Z, 0.69f, 0.70f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiedMediumBoundariesKeepEveryActiveNormal(bool reverse)
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 1;
        scene.SecondShore = true;
        scene.ReverseContacts = reverse;
        ShellSweep sweep = ShellMotion.SweepWithQueries(scene.Queries(), Vector3.Zero, 0, new(2, 2),
            Tuning, MathF.Cos(Tuning.MaxSlopeRadians));

        Assert.True(sweep.Blocked);
        Assert.InRange(sweep.Achieved.X, 0.74f, 0.75f);
        Assert.InRange(sweep.Achieved.Y, 0.74f, 0.75f);
        Assert.Equal(sweep.Achieved.X, sweep.Achieved.Y);
    }

    [Fact]
    public void LagPaymentCannotPublishThroughForbiddenMedium()
    {
        using var scene = new RouteScene(0.25f);
        scene.WaterMinX = float.NegativeInfinity;
        scene.WaterBottom = 1.6f;
        GroundStepResult step = GroundCore.Step(Vector3.Zero, Vector2.Zero, 0.1f, Tuning, Settings, scene.Queries(),
            stepInProgress: true);

        Assert.Equal(Vector3.Zero, step.Feet);
        Assert.Equal(0f, step.StepRise);
        Assert.Equal(0f, step.MoveRise);
        Assert.True(step.Blocked);
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("binding")]
    [InlineData("stale")]
    public void P4LateEvidenceFailureRollsBackTheWholeTickAndConsumesThePress(string fault)
    {
        using var scene = new RouteScene();
        scene.LateFault = fault;
        var input = new MoveState
        {
            Position = new(0, 0.751f, 0),
            Grounded = true,
            JumpBufferRemaining = 1,
            StepDeltaY = 0.2f,
            ClimbRate = 1,
            ClimbRateEwma = 1,
            LandingImpactSpeed = 3,
            SupportGranted = true
        };
        MoveState result = ContactController.StepWithQueries(input, Vector2.UnitX, 1, false, true, 0.5f,
            Tuning, scene.Queries());

        Assert.Equal(input.Position, result.Position);
        Assert.Equal(input.VerticalVelocity, result.VerticalVelocity);
        Assert.Equal(input.Grounded, result.Grounded);
        Assert.Equal(0f, result.JumpBufferRemaining);
        Assert.Equal(0f, result.StepDeltaY);
        Assert.Equal(0f, result.ClimbRate);
        Assert.Equal(0f, result.ClimbRateEwma);
        Assert.Equal(0f, result.LandingImpactSpeed);
        Assert.False(result.SupportGranted);
        Assert.True(scene.ProgressSeen);
    }
}
