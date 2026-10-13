using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Windowing;
using Xunit;
using LifecycleWorld = KhaozEngine.Tests.NetWorld.Fixtures.ObservedPhysicsWorld;

namespace KhaozEngine.Tests.Game;

public class CharacterController3DContactTests
{
    const float Dt = 0.1f;
    static readonly MovementBoundary Boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
    static readonly Func<float, float, float> Ground = (_, _) => -0.9f;
    static MoveTuning Tuning => MoveTuning.Default with
    { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.SurfaceSwimmer, 7) };

    [Theory]
    [InlineData(false, 0f, 0f)]
    [InlineData(true, 0f, 0f)]
    [InlineData(false, 256f, 3.3f)]
    [InlineData(true, 256f, 3.3f)]
    public void CharacterControllerConsumesInputOnItsLeasedBranch(bool wet, float originX, float originY)
    {
        using var physics = new LifecycleWorld();
        physics.Rebase(new(originX, originY, 0));
        Func<float, float, float> ground = wet ? (_, _) => -4 : Ground;
        var boundary = MovementBoundary.Rectangle(originX - 20, -20, originX + 20, 20);
        var registration = Registration(physics, originX, wet ? -4 : -0.9f);
        var field = wet ? MovementWaterField.Level("world", 1, -4) : null;
        using var reference = EffectiveMovementBinding.Ordinary(Tuning, physics, registration, ground, null, boundary, field);
        using var controller = new CharacterController3D { Controller = Tuning.Controller, Water = Tuning.Water };
        controller.SetXZ(originX, 0);
        var expected = new MoveState { Position = new(originX, 0, 0) };
        for (int tick = 0; tick < 4; tick++)
        {
            bool jump = tick == 2;
            var input = Keys(jump, Key.D, Key.LeftShift);
            var command = new MoveCommand(Vector2.UnitX, true, 0.3f, jump);
            var frame = new MovementFrameDescriptor(default, physics.Origin, 1);
            Assert.Equal(MovementAvailability.Known, reference.TryAcquire(reference.CreateOrdinaryScope(frame), out var queries));
            using (queries)
            {
                var local = expected with { Position = expected.Position - physics.Origin };
                var result = reference.Step(new(local, frame, null), command, Dt, queries!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                expected = result.State.State with { Position = result.State.State.Position + physics.Origin };
            }
            controller.Update(input, Dt, 0.3f, ground, null, physics, null, field, registration, boundary);
            Assert.Equal(expected.Position, controller.Position);
            Assert.Equal(expected.FacingYaw, controller.FacingYaw);
            Assert.Equal(expected.Swimming, controller.Swimming);
            Assert.Equal(expected.ClimbRate, controller.ClimbRate);
            Assert.Equal(expected.StepDeltaY, controller.StepDeltaY);
            Assert.Equal(expected.Grounded, controller.Grounded);
            Assert.Equal(expected.VerticalVelocity, controller.VerticalVelocity);
            Assert.Equal(expected.LandingImpactSpeed, controller.LandingImpactSpeed);
            Assert.Equal(expected, controller.MovementState);
        }
        Assert.True(controller.Position.X > originX);
        Assert.Equal(2, physics.ViewCreations);
    }

    [Fact]
    public void MutableControllerRefusesInvalidFootWithoutChangingStateAndDisposesOwnedViewOnce()
    {
        using var physics = new LifecycleWorld();
        var registration = Registration(physics);
        var controller = new CharacterController3D { Controller = Tuning.Controller, Water = Tuning.Water };
        controller.Update(Keys(false, Key.D), Dt, 0, Ground, null, physics, null, null, registration, Boundary);
        var before = controller.MovementState;
        controller.FootRadiusFraction = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.Update(Keys(false, Key.D), Dt, 0,
            Ground, null, physics, null, null, registration, Boundary));
        Assert.Equal(before, controller.MovementState);
        Assert.Equal(1, physics.ViewCreations);
        Assert.Equal(0, physics.ViewDisposals);
        controller.FootRadiusFraction = 0.5f;
        controller.Update(Keys(false, Key.D), Dt, 0, Ground, null, physics, null, null, registration, Boundary);
        Assert.Equal(1, physics.ViewCreations);
        controller.Dispose();
        controller.Dispose();
        Assert.Equal(1, physics.ViewDisposals);
        Assert.Equal(0, physics.SourceDisposals);
        Assert.Throws<ObjectDisposedException>(() => controller.Update(Keys(false), Dt, 0, Ground));
    }

    [Theory]
    [InlineData("tuning")]
    [InlineData("world")]
    [InlineData("field")]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("medium")]
    [InlineData("height")]
    [InlineData("normal")]
    public void ReconfigurationReplacesTheOwnedBindingForEachCacheKey(string key)
    {
        using var physics = new LifecycleWorld();
        using var other = new LifecycleWorld();
        var registration = Registration(physics);
        var field = MovementWaterField.Level("world", 0, -4);
        Func<float, float, float, MovementMedium> medium = (_, _, _) => MovementMedium.Dry;
        Func<float, float, Vector3> normal = (_, _) => Vector3.UnitY;
        using var controller = new CharacterController3D { Controller = Tuning.Controller, Water = Tuning.Water };
        controller.Update(Keys(false), Dt, 0, Ground, normal, physics, medium, field, registration, Boundary);
        controller.Update(Keys(false), Dt, 0, Ground, normal, physics, medium, field, registration, Boundary);
        Assert.Equal(1, physics.ViewCreations);
        Assert.Equal(0, physics.ViewDisposals);
        if (key == "tuning") controller.FootRadiusFraction = 0.6f;
        var nextWorld = key == "world" ? other : physics;
        var nextRegistration = key is "world" or "registration" ? Registration(nextWorld) : registration;
        var nextField = key == "field" ? MovementWaterField.Level("world", 0, -4) : field;
        var nextBoundary = key == "boundary" ? MovementBoundary.Rectangle(-19, -19, 19, 19) : Boundary;
        Func<float, float, float> nextHeight = key == "height" ? (_, _) => -0.9f : Ground;
        Func<float, float, Vector3> nextNormal = key == "normal" ? (_, _) => Vector3.UnitY : normal;
        Func<float, float, float, MovementMedium> nextMedium = key == "medium" ? (_, _, _) => MovementMedium.Dry : medium;
        controller.Update(Keys(false, Key.D), Dt, 0, nextHeight, nextNormal, nextWorld, nextMedium,
            nextField, nextRegistration, nextBoundary);
        Assert.Equal(1, physics.ViewDisposals);
        Assert.Equal(key == "world" ? 1 : 2, physics.ViewCreations);
        Assert.Equal(key == "world" ? 1 : 0, other.ViewCreations);
        Assert.True(controller.Position.X > 0);
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("field")]
    [InlineData("sweep")]
    [InlineData("selection")]
    public void ConfigurationRefusalPreservesTheUnsteppedState(string refusal)
    {
        using var physics = new LifecycleWorld { CertifiedSweep = refusal != "sweep", Selectable = refusal != "selection" };
        var registration = Registration(physics);
        using var controller = new CharacterController3D { Controller = Tuning.Controller, Water = Tuning.Water };
        if (refusal == "gravity") controller.Gravity = 0;
        if (refusal == "fall") controller.MaxFallSpeed = 0;
        var before = controller.MovementState;
        Action update = () => controller.Update(Keys(false, Key.D), Dt, 0, Ground, null, physics,
            refusal == "field" ? (_, _, _) => MovementMedium.Dry : null, null,
            refusal == "registration" ? null : registration, refusal == "boundary" ? null : Boundary);
        if (refusal is "sweep" or "selection") Assert.Throws<NotSupportedException>(update);
        else Assert.Throws<ArgumentException>(update);
        Assert.Equal(before, controller.MovementState);
        Assert.Equal(physics.ViewCreations, physics.ViewDisposals);
    }

    static OrdinaryMovementRegistration Registration(IPhysicsWorld world, float originX = 0, float ground = -0.9f)
    {
        using var read = ((IPhysicsQueryLeaseSource)world).AcquireQueryReadLease();
        return OrdinaryMovementRegistration.CreateBuilder("world", world,
            new(new(originX - 20, -4, -20), new(originX + 20, 4, 20), ground, ground)).Build(read);
    }

    static InputState Keys(bool jump, params Key[] down)
    {
        var held = new HashSet<Key>(down);
        if (jump) held.Add(Key.Space);
        return new(held, jump ? new HashSet<Key> { Key.Space } : new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 0, 800, 600);
    }
}
