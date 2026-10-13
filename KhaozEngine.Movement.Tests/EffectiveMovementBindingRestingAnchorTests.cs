using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class EffectiveMovementBindingRestingAnchorTests
{
    [Fact]
    public void RestingAnchorFindsLipAboveRawPoint()
    {
        using var world = LowLipTraversalTests.LipWorld();
        var tuning = GroundTraversalProbeTests.Tuning with { CapsuleRadius = 0.3f, CapsuleHalfHeight = 0.75f };
        // The rounded capsule end meets the lip 0.025 - (0.3 - sqrt(0.3^2 - 0.075^2)) above the bank.
        var raw = new MoveState { Position = new(-0.075f, 0.75f, 0.125f), Grounded = true };
        var legacy = NpcGroundMovement.Hold(raw, 1f / 30, tuning, new((_, _) => 0, physics: world));
        Assert.InRange(legacy.Position.Y - raw.Position.Y, 0.014f, 0.016f);
        Assert.True(world.Raycast(new(-1, 1, 0), -Vector3.UnitY, 2, out var floor, QueryFilter.StaticsOnly));
        Assert.True(world.Raycast(new(1, 1, 0), -Vector3.UnitY, 2, out var lip, QueryFilter.StaticsOnly));
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", world,
            new(new(-20, -4, -20), new(20, 4, 20), 0, 0));
        builder.Add(floor.Body!.Value, 0, int.MaxValue, new("world", "floor"), new(-8, -0.2f, -8), new(8, 0, 8));
        builder.Add(lip.Body!.Value, 0, int.MaxValue, new("world", "lip"), new(0, 0, -2), new(2, 0.025f, 2));
        OrdinaryMovementRegistration registration;
        using (var read = world.AcquireQueryReadLease()) registration = builder.Build(read);
        tuning = tuning with { Controller = MovementControllerKind.Contact, Water = new(WaterTraversalMode.DryOnly) };
        using var binding = EffectiveMovementBinding.Ordinary(tuning, world, registration, (_, _) => 0, null,
            MovementBoundary.Rectangle(-20, -20, 20, 20));
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var state = new FramedMovementState(raw, frame, null);
            Assert.Equal(MovementAvailability.Known, binding.TryFindRestingAnchor(state, queries!, out var anchor));
            Assert.Equal(SupportStatus.Walkable, anchor.Support.Status);
            Assert.Equal(lip.Body, anchor.Support.Static);
            Assert.Equal(new Vector3(-0.075f, 0.025f, 0.125f), anchor.Feet);
            Assert.Equal(anchor.Support.Height, anchor.Feet.Y);
            Assert.InRange(anchor.Feet.Y, -tuning.GroundedEpsilon, tuning.StepHeight);
        }
    }
}
