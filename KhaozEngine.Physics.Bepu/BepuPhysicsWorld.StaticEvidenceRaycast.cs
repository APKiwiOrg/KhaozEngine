using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsStaticEvidenceRaycast
{
    public PhysicsEvidenceStatus RaycastStaticEvidence(IPhysicsQueryLease lease, Vector3 origin, Vector3 direction,
        float maxDistance, out RayHit? hit, out int leaf)
    {
        AuthenticateFeatureLease(lease);
        using QueryOperation operation = EnterQuery();
        var handler = new EvidenceRayHandler();
        _sim.RayCast(origin, direction, maxDistance, ref handler);
        hit = null;
        leaf = -1;
        lease.AssertCurrent();
        if (!handler.Ray.DidHit) return PhysicsEvidenceStatus.Complete;
        var owner = ResolveSeamHandle(handler.Ray.HitStatic);
        if (owner is null || !TryGetInstalledSweepGeometry(new CollidableReference(handler.Ray.HitStatic), out var geometry) ||
            geometry is null) return PhysicsEvidenceStatus.Unavailable;
        if (geometry.Children is { } children)
        {
            bool known = false;
            foreach (var child in children)
                if (child.ChildIndex == handler.Child) { known = true; break; }
            if (!known) return PhysicsEvidenceStatus.Unavailable;
            leaf = handler.Child;
        }
        else leaf = Math.Max(0, geometry.ChildIndex);
        hit = new RayHit(handler.Ray.HitT, origin + direction * handler.Ray.HitT, handler.Ray.HitNormal, owner);
        return PhysicsEvidenceStatus.Complete;
    }

    // Reuse the ordinary nearest-hit rule and arithmetic. Only its selected child address is added.
    struct EvidenceRayHandler : IRayHitHandler
    {
        internal RayHitHandler Ray;
        internal int Child;
        public EvidenceRayHandler() { Ray = new(QueryMobility.Statics); Child = -1; }
        public bool AllowTest(CollidableReference collidable) => Ray.AllowTest(collidable);
        public bool AllowTest(CollidableReference collidable, int childIndex) => Ray.AllowTest(collidable, childIndex);
        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal,
            CollidableReference collidable, int childIndex)
        {
            if (t < Ray.HitT) Child = childIndex;
            Ray.OnRayHit(ray, ref maximumT, t, normal, collidable, childIndex);
        }
    }
}
