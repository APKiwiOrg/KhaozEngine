using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

internal readonly record struct PhysicsNavCaptureBasis(
    IPhysicsQueryLeaseSource Source, long GeometryGeneration, Vector3 Origin)
{
    internal static PhysicsNavCaptureBasis? Capture(IPhysicsWorld world)
    {
        if (world is not IPhysicsQueryLeaseSource source) return null;
        using IPhysicsQueryLease lease = source.AcquireQueryReadLease();
        lease.AssertCurrent();
        return new(source, lease.GeometryGeneration, lease.Origin);
    }

    internal void AssertCurrent()
    {
        using IPhysicsQueryLease lease = Source.AcquireQueryReadLease();
        lease.AssertCurrent();
        if (lease.GeometryGeneration != GeometryGeneration || lease.Origin != Origin)
            throw new InvalidOperationException("The physics geometry or origin changed after capture began.");
    }
}
