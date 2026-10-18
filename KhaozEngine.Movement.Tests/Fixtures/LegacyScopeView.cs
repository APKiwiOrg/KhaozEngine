using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal sealed class LegacyScopeView(IPhysicsWorldQueryView inner) : PhysicsForwarder(inner),
    IPhysicsWorldQueryView, IPhysicsQueryLeaseSource
{
    internal IPhysicsWorld Owner { get; set; } = inner.SourceWorld;
    internal Vector3? ReportedOrigin { get; set; }
    internal Func<IPhysicsQueryLease>? Acquire { get; set; }
    public IPhysicsWorld SourceWorld => Owner;
    public new Vector3 Origin => ReportedOrigin ?? inner.Origin;
    public IPhysicsQueryLease AcquireQueryReadLease() => Acquire?.Invoke() ??
        ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease();
}

internal sealed class LegacyViewWithoutLease(IPhysicsWorldQueryView inner) : PhysicsForwarder(inner), IPhysicsWorldQueryView
{
    public IPhysicsWorld SourceWorld => inner.SourceWorld;
}

internal sealed class LegacyWorldWithoutLease(IPhysicsWorld inner) : PhysicsForwarder(inner)
{
    public override void Dispose() { }
}

internal sealed class LegacyLeaseMetadata(IPhysicsQueryLease inner, IPhysicsWorld owner, Vector3 origin)
    : IPhysicsQueryLease
{
    public IPhysicsWorld SourceWorld => owner;
    public Vector3 Origin => origin;
    public long GeometryGeneration => inner.GeometryGeneration;
    public void AssertCurrent() => inner.AssertCurrent();
    public void Dispose() => inner.Dispose();
}
