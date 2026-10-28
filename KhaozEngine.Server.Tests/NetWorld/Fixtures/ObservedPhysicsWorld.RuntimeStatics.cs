using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed partial class ObservedPhysicsWorld : IPhysicsStaticMutationSource
{
    public IPhysicsStaticMutationLease AcquireStaticMutationLease() =>
        new StaticMutation(this, Backend.AcquireStaticMutationLease());

    sealed class StaticMutation(ObservedPhysicsWorld owner, IPhysicsStaticMutationLease inner) : IPhysicsStaticMutationLease
    {
        public IPhysicsWorld SourceWorld => owner;
        public Vector3 Origin => inner.Origin;
        public long StaticGeometryGeneration => inner.StaticGeometryGeneration;
        public void AssertCurrent() => inner.AssertCurrent();
        public void Dispose() => inner.Dispose();
    }
}
