using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class EffectiveMovementBindingProofTests
{
    static MoveTuning Tuning => MoveTuning.Default with
    {
        Controller = MovementControllerKind.Contact,
        Water = new(WaterTraversalMode.DryOnly),
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        StepHeight = 0.4f,
        GroundedEpsilon = 0.05f,
    };

    static EffectiveMovementBinding Binding(IPhysicsWorld world, MoveTuning tuning, float height,
        IMovementWaterField? water = null)
    {
        OrdinaryMovementRegistration registration;
        using (var read = ((IPhysicsQueryLeaseSource)world).AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", world,
                new(new(-20), new(20), height, height)).Build(read);
        return EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => height, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20), water);
    }

    static FramedMovementState State(MovementFrameDescriptor frame, float feet) =>
        new(new MoveState { Position = new(0, feet + 0.75f, 0), Grounded = true }, frame, null);

    [Fact]
    public void ProofBindingSharesEnvironmentAndNormalizesSpeeds()
    {
        using var world = new ObservedPhysicsWorld();
        var tuning = Tuning with { WalkSpeed = 0, RunSpeed = 7, AirMomentum = true };
        using var source = Binding(world, tuning, 0);
        using var proof = source.CreateProofBinding();
        Assert.Equal(EffectiveMovementBindingKind.OrdinaryContact, proof.Kind);
        Assert.Equal(tuning with { WalkSpeed = 1, RunSpeed = 1, AirMomentum = false }, proof.Tuning);
        Assert.Equal(tuning, source.Tuning);
        Assert.Equal(source.Water, proof.Water);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        var scope = source.CreateOrdinaryScope(frame);
        Assert.Equal(scope, proof.CreateOrdinaryScope(frame));
        Assert.Same(source.CaptureOrdinaryBoundary(frame), proof.CaptureOrdinaryBoundary(frame));
        Assert.Equal(1, world.ViewCreations);
        Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var read));
        using (read)
        {
            var step = proof.StepTowards(State(frame, 0), Vector2.UnitX, false, 0.1f, read!);
            Assert.Equal(MovementStepOutcome.Advanced, step.Outcome);
            Assert.Equal(0.75f, step.State.State.Position.Y);
            Assert.InRange(step.State.State.Position.X, 0.099999f, 0.100001f);
            proof.Dispose();
            read!.AssertCurrent();
            Assert.Equal(0, world.ViewDisposals);
            Assert.Equal(MovementStepOutcome.Advanced, source.ValidatePlacement(State(frame, 0), read).Outcome);
        }
        Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var after));
        using (after) Assert.Equal(MovementStepOutcome.Advanced,
            source.StepTowards(State(frame, 0), Vector2.UnitX, false, 0.1f, after!).Outcome);
        source.Dispose();
        Assert.Equal(1, world.ViewDisposals);
        Assert.Equal(0, world.SourceDisposals);
    }

    [Fact]
    public void RestingAnchorIgnoresWaterAtRawPoint()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var tuning = Tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        using var binding = Binding(world, tuning, -0.8f, MovementWaterField.Level("world", 0, -4));
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var read));
        using (read)
        {
            var state = State(frame, -1);
            Assert.Equal(MovementStepOutcome.PlacementRefused, binding.ValidatePlacement(state, read!).Outcome);
            Assert.Equal(MovementAvailability.Known, binding.TryFindRestingAnchor(state, read!, out var anchor));
            Assert.Equal(SupportStatus.Walkable, anchor.Support.Status);
            Assert.Equal(new Vector3(0, -0.8f, 0), anchor.Feet);
            Assert.Equal(-0.8f, anchor.Support.Height);
        }
    }

    [Fact]
    public void RestingAnchorRefusesOutOfBandSupport()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var binding = Binding(world, Tuning, 0.4001f);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var read));
        using (read)
        {
            Assert.Equal(MovementAvailability.Known, binding.TryFindRestingAnchor(State(frame, 0), read!, out var anchor));
            Assert.Equal(default, anchor);
            Assert.NotEqual(SupportStatus.Walkable, anchor.Support.Status);
        }
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(-0.05f)]
    public void RestingAnchorIncludesBothBandEndpoints(float support)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var binding = Binding(world, Tuning, support);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var read));
        using (read)
        {
            Assert.Equal(MovementAvailability.Known, binding.TryFindRestingAnchor(State(frame, 0), read!, out var anchor));
            Assert.Equal(SupportStatus.Walkable, anchor.Support.Status);
            Assert.Equal(new Vector3(0, support, 0), anchor.Feet);
        }
    }
}
