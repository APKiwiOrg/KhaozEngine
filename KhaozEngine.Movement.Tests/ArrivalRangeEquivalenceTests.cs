using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ArrivalRangeEquivalenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("flat")]
    [InlineData("dry-field")]
    [InlineData("rebase")]
    [InlineData("clamp")]
    [InlineData("clamp-rebase")]
    [InlineData("clamp-rim")]
    [InlineData("unproven")]
    [InlineData("bank")]
    [InlineData("slope")]
    [InlineData("certified-slope")]
    [InlineData("certified-slope-rebase")]
    [InlineData("seam")]
    [InlineData("wall")]
    [InlineData("tangent")]
    [InlineData("water")]
    [InlineData("shore")]
    [InlineData("water-tangent")]
    [InlineData("precision")]
    [InlineData("precision-edge")]
    [InlineData("borrowed")]
    [InlineData("borrowed-dry")]
    public void SeededArrivalCommandsAndStatesMatchOriginalSolver(string scenario)
    {
        // #1729, #1737. No tolerance is permitted in the command or any carried/event state field.
        using FlatArrivalScene scene = Scene(scenario);
        var random = new Random(1729);
        int referenceSteps = 0, fullSteps = 0, certified = 0;
        const int samples = 128;
        for (int sample = 0; sample < samples; sample++)
        {
            MoveTuning tuning = scene.Tuning with { FacingTurnSpeed = sample % 2 == 0 ? 3f : float.PositiveInfinity };
            float x = scenario == "precision" ? 1048576 : (random.NextSingle() - 0.5f) * 0.125f;
            if (scenario == "clamp-rim") x = 19.99f;
            if (scenario == "precision-edge") x += MathF.ScaleB(1, 8 + sample % 7);
            float z = (random.NextSingle() - 0.5f) * 0.125f;
            float height = scene.Context.GroundHeight(x, z);
            var body = new MoveState
            {
                Position = new(x, height + tuning.CapsuleHalfHeight, z),
                Grounded = true,
                SpeedScale = 0.5f + random.NextSingle() * 1.5f,
                FacingYaw = random.NextSingle() * 6 - 3,
                HorizontalVelocity = new(random.NextSingle(), -random.NextSingle()),
            };
            float angle = random.NextSingle() * MathF.Tau;
            Vector2 command = new(MathF.Cos(angle), MathF.Sin(angle));
            command *= sample % 4 == 0 ? 0.25f : 1;
            bool run = sample % 2 == 0;
            float dt = sample % 3 == 0 ? 1f / 30f : 0.1f;
            MoveState predicted = scene.Context.Step(body, command, run, dt, tuning);
            Vector3 centre = predicted.Position + new Vector3(0, sample % 7 == 0 ? 0.05f : 0, 0);
            ReachTarget target = (sample % 3) switch
            {
                0 => ReachTarget.Point(centre),
                1 => ReachTarget.Capsule(new(centre, 0.05f, 0.1f)),
                _ => ReachTarget.Box(centre, new(0.03f, 0.1f, 0.07f), angle),
            };
            float range = sample % 5 == 0 ? 0 : random.NextSingle() * 0.1f;
            StepAdmission admits = sample % 8 == 0 ? RejectMiddle : ArrivalRangeCrossingTests.Admit;
            var expectedTrials = new List<MoveState>();
            var actualTrials = new List<MoveState>();
            bool ObserveActual(in MoveState from, in MoveState to, in MoveTuning shape)
            {
                actualTrials.Add(to);
                return admits(from, to, shape);
            }
            Vector2 expected = ArrivalReference.Stop(body, tuning, target, range, run, dt, scene.Context,
                command, predicted, admits, out int count, expectedTrials.Add);
            referenceSteps += count;
            Vector2 actual;
            using (var work = new ArrivalPreviewDiagnostics())
            {
                actual = RangeApproachCore.StopAtRange(body, tuning, target, range, run, dt, scene.Context,
                    command, predicted, ObserveActual);
                fullSteps += work.FullSteps;
                certified += work.CertifiedTrials;
            }
            Same(expected, actual);
            Assert.Equal(count, expectedTrials.Count);
            Assert.Equal(count, actualTrials.Count);
            for (int trialIndex = 0; trialIndex < count; trialIndex++) Same(expectedTrials[trialIndex], actualTrials[trialIndex]);
            Same(scene.Context.Step(body, expected, run, dt, tuning), scene.Context.Step(body, actual, run, dt, tuning));
            MoveState trial = scene.Context.Step(body, command * 0.5f, run, dt, tuning);
            using var preview = scene.Context.OpenGroundRangePreview(body, predicted, tuning, run, dt);
            if (preview is not null) Same(trial, preview.Step(command * 0.5f));
        }
        Assert.True(referenceSteps > 0);
        Assert.Equal(referenceSteps, fullSteps + certified);
        output.WriteLine($"{scenario}: samples={samples}, reference={referenceSteps}, full={fullSteps}, certified={certified}");
        if (scenario is "flat" or "dry-field" or "rebase" or "borrowed-dry" or "clamp" or "clamp-rebase" or
            "certified-slope" or "certified-slope-rebase")
        {
            Assert.Equal(0, fullSteps);
            Assert.True(certified > 0);
        }
        else if (scenario != "precision-edge")
        {
            Assert.Equal(referenceSteps, fullSteps);
            Assert.Equal(0, certified);
        }
    }

    [Fact]
    public void UnenclosedOptionalSignalQueriesRetireTheSupportedTrial()
    {
        // #1754. Ground signals may swallow an optional query refusal. Missing capture evidence must still
        // retire the preview, since the live producer can answer the look-ahead outside the captured interval.
        using var scene = new FlatArrivalScene(height: (x, _) => MathF.Floor((x + 0.01f) / 0.25f) * 0.125f,
            normal: (_, _) => Vector3.UnitY, slope: 0);
        MoveTuning tuning = scene.Tuning with { MaxStepClimbSpeed = 0 };
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        Assert.True(predicted.ClimbRate > 0);
        var target = ReachTarget.Point(predicted.Position);
        static bool Admit(in MoveState from, in MoveState to, in MoveTuning tuning) => to.Grounded && to.ClimbRate > 0;
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Admit);
        Same(expected, actual);
        Assert.Equal(count - 1, work.FullSteps);
        Assert.Equal(1, work.CertifiedTrials);
    }

    static bool RejectMiddle(in MoveState from, in MoveState to, in MoveTuning tuning)
        => ArrivalRangeCrossingTests.Admit(from, to, tuning) &&
            (Vector3.DistanceSquared(from.Position, to.Position) < 0.0004f || to.Position.X >= from.Position.X);

    static FlatArrivalScene Scene(string scenario) => scenario switch
    {
        "dry-field" => new(water: MovementWaterField.Level("world", -3, -4)),
        "rebase" => new(origin: new(2, 0, -3)),
        "certified-slope" => new(slope: 0.125f),
        "certified-slope-rebase" => new(slope: 0.125f, origin: new(2, 0.125f, -3)),
        "clamp" or "clamp-rim" => new(clampXz: FlatArrivalScene.ThrowingClamp),
        "clamp-rebase" => new(origin: new(2, 0, -3), clampXz: FlatArrivalScene.ThrowingClamp),
        "unproven" => new(provenance: false),
        "bank" => new((x, _) => x < 0 ? 0 : 0.125f, provenance: false),
        "slope" => new((x, _) => x * 0.125f, (_, _) => Vector3.Normalize(new(-0.125f, 1, 0)), provenance: false),
        "seam" => new(provenance: false, install: (world, builder) =>
        {
            ContactNavigationFixture.Box(world, builder, "left", new(-8, -0.2f, -8), new(0, 0, 8));
            ContactNavigationFixture.Box(world, builder, "right", new(0, -0.2f, -8), new(8, 0, 8));
        }),
        "wall" => new(install: (world, builder) => ContactNavigationFixture.Box(world, builder,
            "wall", new(0.3f, 0, -2), new(0.4f, 3, 2))),
        "tangent" => new(install: (world, builder) => ContactNavigationFixture.Box(world, builder,
            "roof", new(-2, 1.9f, -2), new(2, 2, 2))),
        "water" => new(water: MovementWaterField.Level("world", 0.1f, -4)),
        "shore" => new((x, _) => x < 0 ? 0 : 0.125f, water: MovementWaterField.Level("world", 0.1f, -4), provenance: false),
        "water-tangent" => new(water: MovementWaterField.Level("world", 0, -4)),
        "borrowed" => new(install: (world, builder) =>
        {
            var min = new Vector3(0.3f, 0, -2);
            var max = new Vector3(0.4f, 3, 2);
            var handle = world.AddStatic(new BoxShape((max - min) / 2), Pose.At((min + max) / 2));
            builder.Add(handle, 0, int.MaxValue, new("world", "excluded-wall"), min, max).Exclude(handle);
        }, movementQueries: world => world.CreateQueryViewExcludingStatics([])),
        "borrowed-dry" => new(movementQueries: world => world.CreateQueryViewExcludingStatics([])),
        "precision" or "precision-edge" => new(extent: new(new(-2000000, -4, -20), new(2000000, 4, 20), -2, 2),
            boundary: MovementBoundary.Rectangle(-2000000, -20, 2000000, 20)),
        _ => new(),
    };

    internal static void Same<T>(T expected, T actual) =>
        LocomotionStateBits.AssertEqual(expected, actual, LocomotionStateBits.Policy.IncludeContinuityHandle);
}
