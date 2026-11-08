using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

internal sealed partial class ObservedPhysicsWorld : IPhysicsStaticEvidence, IPhysicsStaticEvidenceRaycast
{
    public PhysicsEvidenceStatus CaptureStaticEvidence(IPhysicsQueryLease lease, Vector3 minimum, Vector3 maximum,
        int maximumFeatures, PhysicsEvidenceVisitor visitor)
    {
        lease.AssertCurrent();
        return Backend.CaptureStaticEvidence(((Read)lease).Inner, minimum, maximum, maximumFeatures, visitor);
    }

    public PhysicsEvidenceStatus RaycastStaticEvidence(IPhysicsQueryLease lease, Vector3 origin, Vector3 direction,
        float maxDistance, out RayHit? hit, out int leaf)
    {
        lease.AssertCurrent();
        return Backend.RaycastStaticEvidence(((Read)lease).Inner, origin, direction, maxDistance, out hit, out leaf);
    }
}
