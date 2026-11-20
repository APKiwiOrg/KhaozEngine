using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>One ordinary contact lease over a water field at a chosen physics origin, with an empty physics world.
/// The boundary question reads only water, so no statics are installed. The extent is absolute and the lease's
/// envelope follows from it as in production.</summary>
internal sealed class WaterBoundaryParityLease : IDisposable
{
    readonly BepuPhysicsWorld _physics = new(Vector3.Zero);
    readonly EffectiveMovementBinding _binding;
    internal MovementQueryLease Lease { get; }
    internal MovementFrameDescriptor Frame { get; }

    internal WaterBoundaryParityLease(IMovementWaterField field, string worldId, Vector3 origin, Vector3 minimum,
        Vector3 maximum, in MoveTuning tuning, Vector3 select, bool rebuildSelection = true)
    {
        try
        {
            var builder = OrdinaryMovementRegistration.CreateBuilder(worldId, _physics,
                new(minimum, maximum, minimum.Y, minimum.Y));
            if (origin != Vector3.Zero) _physics.Rebase(origin);
            OrdinaryMovementRegistration registration;
            using (var read = _physics.AcquireQueryReadLease()) registration = builder.Build(read);
            float ground = minimum.Y;
            _binding = EffectiveMovementBinding.Ordinary(tuning, _physics, registration, (_, _) => ground, null,
                MovementBoundary.Rectangle(minimum.X, minimum.Z, maximum.X, maximum.Z), field);
            Frame = new(default, _physics.Origin, 1);
            Assert.Equal(MovementAvailability.Known, _binding.TryAcquire(_binding.CreateOrdinaryScope(Frame), out var lease));
            Lease = lease!;
            var state = new FramedMovementState(new MoveState { Position = select - Frame.PhysicsOrigin }, Frame, null);
            if (rebuildSelection) Assert.Equal(MovementAvailability.Known, Lease.RebuildSelection(state, out _));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Lease?.Dispose();
        _binding?.Dispose();
        _physics.Dispose();
    }
}
