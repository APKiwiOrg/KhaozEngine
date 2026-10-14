using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public class OrdinaryLevelWaterGeometryTests
{
    const float Dt = 1f / 30f;
    static MoveTuning Tuning(WaterTraversalMode mode) => new(8, 10, 0.75f, 20f * MathF.PI / 180f,
        CapsuleRadius: 0.25f, TractionHysteresisRadians: 0)
    { Controller = MovementControllerKind.Contact, Water = new(mode, 7) };

    [Theory]
    [InlineData("entry", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("exit", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("buoyancy", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("hop", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("shore", WaterTraversalMode.WadeOnly)]
    [InlineData("bed", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("down-seat", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("dry-band", WaterTraversalMode.SurfaceSwimmer)]
    [InlineData("corner", WaterTraversalMode.WadeOnly)]
    [InlineData("forbidden", WaterTraversalMode.DryOnly)]
    [InlineData("lag", WaterTraversalMode.WadeOnly)]
    public void LevelRowsMatchTheExistingIndependentBoxWaterFixtureAtZeroOffset(string row, WaterTraversalMode mode)
    {
        var tuning = Tuning(mode);
        float deepLine = -2 * tuning.CapsuleHalfHeight * tuning.SwimEnterDepthFraction;
        // A start within the established 0.002 m vertical retreat holds its axis while sliding.
        float high = deepLine + (row == "shore" ? 0.001f : 0.003f);
        Func<float, float, float> height = row switch
        {
            "shore" or "corner" => (x, _) => high - 0.1f * x,
            "down-seat" => (x, _) => x < 0.05f ? high : high - 0.2f,
            "dry-band" => (x, _) => x >= 0.04f && x <= 0.08f ? 0.1f : -4,
            "lag" => (_, _) => -0.2f,
            "exit" => (_, _) => 0.1f,
            _ => (_, _) => -4,
        };
        Func<float, float, Vector3> normal = row is "shore" or "corner"
            ? (_, _) => Vector3.Normalize(new(0.1f, 1, 0)) : (_, _) => Vector3.UnitY;
        var initial = new MoveState { Position = new(0, -1, 0) };
        Vector2 direction = Vector2.Zero;
        bool jump = false;
        switch (row)
        {
            case "exit": initial = new() { Position = new(0, 0.85f, 0), Grounded = true, Swimming = true }; break;
            case "hop":
                initial = new()
                {
                    Position = new(0, -0.15f, 0),
                    Swimming = true,
                    WaterExcursion = WaterExcursionState.Surface
                }; jump = true; break;
            case "buoyancy": initial = initial with { Swimming = true, WaterExcursion = WaterExcursionState.Surface }; break;
            case "shore":
            case "corner": initial = new() { Position = new(0, high + 0.75f, 0), Grounded = true }; direction = Vector2.One; break;
            case "bed":
                initial = new()
                {
                    Position = new(0, -3.2f, 0),
                    Swimming = true,
                    WaterExcursion = WaterExcursionState.Surface,
                    VerticalVelocity = -100
                }; break;
            case "down-seat": initial = new() { Position = new(0, high + 0.75f, 0), Grounded = true }; direction = Vector2.UnitX; break;
            case "dry-band":
                initial = new()
                {
                    Position = new(0, -0.15f, 0),
                    Swimming = true,
                    WaterExcursion = WaterExcursionState.Surface
                }; direction = Vector2.UnitX; break;
            case "lag": initial = new() { Position = new(0, 0.45f, 0), Grounded = true }; direction = Vector2.UnitX; break;
        }
        using var expected = new AnalyticMovementEnvironment([new("room", new(new(-32), new(32)))],
            [new("level", "room", new(new(-20, -4, -20), new(20, 0, 20)), 0)]);
        expected.RegisterAnalyticGround("room", "analytic-ground", -4);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", expected.Physics,
            new(new(-20, -4, -20), new(20, 4, 20), -4, 4));
        if (row == "corner")
        {
            var shape = new BoxShape(new(0.1f, 2, 4));
            var handle = expected.Physics.AddStatic(shape, Pose.At(new(0.38f, 0, 0)));
            expected.RegisterBoxSupport(handle, "wall", "room");
            builder.Add(handle, 0, int.MaxValue, new("world", "wall"), new(0.28f, -2, -4), new(0.48f, 2, 4));
        }
        OrdinaryMovementRegistration registration;
        using (var read = expected.Physics.AcquireQueryReadLease()) registration = builder.Build(read);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, expected.Physics, registration, height, normal,
            MovementBoundary.Rectangle(-20, -20, 20, 20), MovementWaterField.Level("world", 0, -4));
        MoveState oracle;
        MovementStepOutcome expectedOutcome;
        using (var reference = expected.Acquire("room", 8, 8))
        {
            expected.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
            {
                var axis = new Vector2(request.Body.Centre.X, request.Body.Centre.Z);
                ground = new(axis, height(axis.X, axis.Y), normal(axis.X, axis.Y), new("world", "analytic-ground"),
                    Space("room"), null, Identity);
                return MovementAvailability.Known;
            };
            expected.Acquisition.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding owner) =>
            {
                owner = new(new("world", request.Static is null ? "analytic-ground" : "wall"), Space("room"), null, Identity);
                return MovementAvailability.Known;
            };
            var result = ExplicitCharacterMovement.Step(new(initial, reference.Frame, new(Space("room"), null, Identity)),
                new(direction.X == 0 && direction.Y == 0 ? Vector2.Zero : new(direction.X, -direction.Y), false, 0, jump),
                Dt, tuning, tuning.Water, reference, ContactComposition.Ordinary(tuning));
            oracle = result.State.State;
            expectedOutcome = result.Outcome;
        }
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var result = binding.Step(new(initial, frame, null), new(new(direction.X, -direction.Y), false, 0, jump), Dt, queries!);
            Assert.Equal(expectedOutcome, result.Outcome);
            Assert.Equal(oracle.Position, result.State.State.Position);
            Assert.Equal(oracle, result.State.State);
            if (row != "forbidden")
                Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
            switch (row)
            {
                case "entry": Assert.True(result.State.State.Swimming); break;
                case "exit": Assert.False(result.State.State.Swimming); break;
                case "buoyancy": Assert.True(result.State.State.Position.Y > initial.Position.Y); break;
                case "hop":
                    Assert.Equal(WaterExcursionState.AirborneFromWater, result.State.State.WaterExcursion);
                    Assert.Equal((float)(7d - (double)tuning.Gravity * Dt), result.State.State.VerticalVelocity); break;
                case "shore":
                    Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
                    Assert.False(result.State.State.Swimming);
                    Assert.Equal(initial.Position.X, result.State.State.Position.X);
                    Assert.True(result.State.State.Position.Z > initial.Position.Z);
                    Assert.True(result.State.State.Position.Y - 0.75f > deepLine); break;
                case "bed": Assert.True(result.State.State.Position.Y - 0.75f >= -4); break;
                case "down-seat": Assert.True(result.State.State.Swimming); break;
                case "corner":
                    Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
                    Assert.True(result.State.State.Position.X + 0.25f <= 0.28f);
                    Assert.True(result.State.State.Position.Y - 0.75f > deepLine); break;
                case "forbidden":
                    Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
                    Assert.Equal(initial.Position, result.State.State.Position); break;
                case "lag": Assert.True(result.State.State.Position.Y > initial.Position.Y); break;
            }
        }
    }
}
