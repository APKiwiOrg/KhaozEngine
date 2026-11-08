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
        long generation = lease.GeometryGeneration;
        Vector3? origin = null;
        if (generation != GeometryGeneration || (origin = lease.Origin) != Origin)
            throw generation != GeometryGeneration
                ? new NavigationBuildException(NavigationBuildRefusal.CaptureGeometryChanged,
                    "The physics geometry or origin changed after capture began.")
                { ExpectedGeneration = GeometryGeneration, ActualGeneration = generation }
                : new NavigationBuildException(NavigationBuildRefusal.CaptureOriginChanged,
                    "The physics geometry or origin changed after capture began.")
                { ExpectedOrigin = Origin, ActualOrigin = origin };
    }
}
