using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ArrivalRangeCrossingTests(ITestOutputHelper output)
{
    [Fact]
    public void FlatUnchangedSupportDoesNotRunFullArrivalPreviews()
    {
        // #1729. Work, not elapsed time, is the regression contract.
        using var scene = new FlatArrivalScene();
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position + new Vector3(0.4f, 0, 0));
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, 0.1f,
            scene.Context, Vector2.UnitX, predicted, Admit, out int referencePreviews);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, 0.1f,
            scene.Context, Vector2.UnitX, predicted, Admit);
        Assert.Equal(expected, actual);
        output.WriteLine($"reference previews={referencePreviews}, full previews={work.FullSteps}, certified trials={work.CertifiedTrials}");
        Assert.Equal(0, work.FullSteps);
        Assert.InRange(work.CertifiedTrials, 1, 32);
    }

    internal static bool Admit(in MoveState from, in MoveState to, in MoveTuning tuning)
        => to.Grounded && !to.Swimming && MovementBody.IsFinite(to.Position);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectAndRoutedDriversKeepTheOriginalArrivalCommand(bool routed)
    {
        using var scene = new FlatArrivalScene();
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position + new Vector3(0.4f, 0, 0));
        const float dt = 0.1f, range = 0.1f;
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, dt, tuning);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, range, false, dt, scene.Context,
            Vector2.UnitX, predicted, Admit, out _);
        var direct = new DirectMoveToRange(new(15, 0.1f, 45, 0.1f));
        var route = new MoveToRange(new MoveToRangeTests.ScriptPlanner((_, _) =>
                MoveToRangeTests.Route(NavPathStatus.Complete, new Vector2(0.4f, 0))),
            MoveToRangeTests.Space, (_, _) => true);
        RangeSteering Tick(MoveState state) => routed
            ? route.Tick(state, tuning, target, range, false, dt, scene.Context)
            : direct.Tick(state, tuning, target, range, false, false, dt, scene.Context);
        using var work = new ArrivalPreviewDiagnostics();
        RangeSteering steering = Tick(body);
        ArrivalRangeEquivalenceTests.Same(expected, steering.WorldDirection);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.Equal(0, work.FullSteps);
        Assert.InRange(work.CertifiedTrials, 1, 32);
        MoveState accepted = NpcGroundMovement.Step(body, steering, false, dt, tuning, scene.Context);
        ArrivalRangeEquivalenceTests.Same(scene.Context.Step(body, expected, false, dt, tuning), accepted);
        Assert.Equal(RangeMoveStatus.InRange, Tick(accepted).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCertifiedTrialStateMatchesTheFullGroundCore(bool rebase)
    {
        using var scene = new FlatArrivalScene(origin: rebase ? new Vector3(2, 0, -3) : null);
        MoveTuning tuning = scene.Tuning with { FacingTurnSpeed = 2.5f };
        var body = new MoveState
        {
            Position = new(-0f, tuning.CapsuleHalfHeight, 0.125f), Grounded = true, FacingYaw = 1.5f,
            SpeedScale = 1.25f, VerticalVelocity = -3, TimeSinceGrounded = 0.2f,
            HorizontalVelocity = new(2, -3), LandingImpactSpeed = 4, SupportGranted = false,
        };
        var random = new Random(172913);
        const int count = 260;
        var commands = new Vector2[count];
        var expected = new MoveState[count];
        commands[1] = new(-0f, 0);
        commands[2] = new(float.Epsilon, -float.Epsilon);
        commands[3] = new(1e-20f, -1e-20f);
        for (int sample = 4; sample < count; sample++)
        {
            float angle = random.NextSingle() * MathF.Tau, magnitude = random.NextSingle();
            commands[sample] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * magnitude;
        }
        for (int sample = 0; sample < count; sample++)
            expected[sample] = scene.Context.Step(body, commands[sample], true, 0.1f, tuning);
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, true, 0.1f, tuning);
        using var preview = scene.Context.OpenFlatRangePreview(body, predicted, tuning, true, 0.1f);
        Assert.NotNull(preview);
        Assert.Equal(count, expected.Length);
        for (int sample = 0; sample < count; sample++)
            ArrivalRangeEquivalenceTests.Same(expected[sample], preview.Step(commands[sample]));
    }
}
