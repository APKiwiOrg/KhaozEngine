using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Optional static column-ray evidence under the capture's authenticated interval. A complete miss has
/// a null hit and leaf -1. A hit names its actual installed primitive/compound leaf, not a height-selected leaf.
/// The leaf is callback-local provenance and must be resolved to captured source identity before persistence.</summary>
public interface IPhysicsStaticEvidenceRaycast
{
    PhysicsEvidenceStatus RaycastStaticEvidence(IPhysicsQueryLease lease, Vector3 origin, Vector3 direction,
        float maxDistance, out RayHit? hit, out int leaf);
}
