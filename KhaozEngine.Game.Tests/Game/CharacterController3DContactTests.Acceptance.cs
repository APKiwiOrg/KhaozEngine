using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Windowing;
using Xunit;
using LifecycleWorld = KhaozEngine.Tests.NetWorld.Fixtures.ObservedPhysicsWorld;

namespace KhaozEngine.Tests.Game;

public partial class CharacterController3DContactTests
{
    [Theory]
    [InlineData(false, 0f, 0f)]
    [InlineData(true, 0f, 0f)]
    [InlineData(false, 256f, 3.3f)]
    [InlineData(true, 256f, 3.3f)]
    public void CharacterControllerWaterEntryAndExitMatchEveryCarriedOrdinarySignal(bool exit, float originX, float originY)
    {
        using var physics = new LifecycleWorld();
        var origin = new Vector3(originX, originY, 0);
        physics.Rebase(origin);
        var tuning = Tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        Func<float, float, float> ground = (x, _) => -0.9f - 0.1f * (x - originX);
        Func<float, float, Vector3> normal = (_, _) => Vector3.Normalize(new(0.1f, 1, 0));
        var boundary = MovementBoundary.Rectangle(originX - 20, -20, originX + 20, 20);
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", physics,
                new(new(originX - 20, -4, -20), new(originX + 20, 4, 20), -2.9f, 1.1f)).Build(read);
        var field = MovementWaterField.Level("world", -0.9f, -4);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, registration, ground, normal, boundary, field);
        using var controller = new CharacterController3D { Controller = tuning.Controller, Water = tuning.Water };
        float startX = originX + (exit ? 1 : -1);
        controller.SetXZ(startX, 0);
        var expected = new MoveState { Position = new(startX, 0, 0) };
        Step(Keys(false), Vector2.Zero);
        Assert.Equal(exit, Wet(expected));
        bool crossed = false;
        for (int tick = 0; tick < 6; tick++)
        {
            bool before = Wet(expected);
            Step(Keys(false, exit ? Key.A : Key.D), exit ? -Vector2.UnitX : Vector2.UnitX);
            Assert.True(controller.Grounded);
            Assert.False(controller.Swimming);
            crossed |= before != Wet(expected);
        }
        Assert.True(crossed);
        Assert.Equal(!exit, Wet(expected));

        void Step(InputState input, Vector2 direction)
        {
            var frame = new MovementFrameDescriptor(default, origin, 1);
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
            using (queries)
            {
                var local = expected with { Position = expected.Position - origin };
                var result = binding.Step(new(local, frame, null), new(direction, false, 0, false), Dt, queries!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                expected = result.State.State with { Position = result.State.State.Position + origin };
            }
            controller.Update(input, Dt, 0, ground, normal, physics, null, field, registration, boundary);
            Assert.Equal(expected, controller.MovementState);
        }
        bool Wet(MoveState body) => field.SampleCentreWater(new(body.Position - origin, tuning.CapsuleRadius,
            tuning.CapsuleHalfHeight, new("world", "ordinary"), null), origin).InWater;
    }

    [Fact]
    public void CharacterControllerCarriesTriggeredStairSignalsEqualToOrdinaryAndDirectDry()
    {
        using var physics = new LifecycleWorld();
        var tuning = Tuning with
        {
            WalkSpeed = 4,
            RunSpeed = 6,
            CapsuleHalfHeight = 0.75f,
            CapsuleRadius = 0.25f,
            MaxSlopeRadians = 20f * MathF.PI / 180f,
            TractionHysteresisRadians = 0
        };
        Func<float, float, float> ground = (_, _) => -0.75f;
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics,
            new(new(-20, -4, -20), new(20, 4, 20), -0.75f, -0.75f));
        for (int i = 0; i < 4; i++)
        {
            Vector3 min = new(0.4f + i * 0.25f, -0.75f, -4), max = new(4, -0.75f + (i + 1) * 0.15f, 4);
            var handle = physics.Backend.AddStatic(new BoxShape((max - min) * 0.5f), Pose.At((max + min) * 0.5f));
            builder.Add(handle, 0, int.MaxValue, new("world", $"stair-{i}"), min, max);
        }
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, registration, ground, null, Boundary);
        using var controller = new CharacterController3D
        {
            Controller = tuning.Controller,
            Water = tuning.Water,
            WalkSpeed = tuning.WalkSpeed,
            RunSpeed = tuning.RunSpeed,
            CapsuleHalfHeight = tuning.CapsuleHalfHeight,
            CapsuleRadius = tuning.CapsuleRadius,
            MaxSlopeRadians = tuning.MaxSlopeRadians,
            TractionHysteresisRadians = tuning.TractionHysteresisRadians
        };
        var expected = new MoveState();
        var frame = new MovementFrameDescriptor(default, Vector3.Zero, 1);
        bool triggered = false;
        for (int tick = 0; tick < 8; tick++)
        {
            var command = new MoveCommand(Vector2.UnitX, false, 0, false);
            var dry = CharacterMovement.Step(expected, command, Dt, ground, tuning, world: physics);
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
            using (queries)
            {
                var result = binding.Step(new(expected, frame, null), command, Dt, queries!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                expected = result.State.State;
            }
            controller.Update(Keys(false, Key.D), Dt, 0, ground, null, physics, null, null, registration, Boundary);
            Assert.Equal(dry, expected);
            Assert.Equal(expected, controller.MovementState);
            Assert.Equal(expected.ClimbRate, controller.ClimbRate);
            Assert.Equal(expected.StepDeltaY, controller.StepDeltaY);
            triggered |= controller.ClimbRate > 0 && controller.MovementState.ClimbRateEwma > 0;
        }
        Assert.True(triggered);
    }
}
