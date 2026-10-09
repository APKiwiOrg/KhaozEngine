using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using BepuUtilities;
using KhaozEngine.Physics;

using BepuStaticHandle = BepuPhysics.StaticHandle;
using SeamHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Shared query implementations for the complete world and its restricted views.</summary>
public sealed partial class BepuPhysicsWorld
{
    // Broad-phase candidates for one penetration query. Cleared per call and kept for its capacity. Query views
    // reach the same core, so they share it. The world is single-threaded, so one list serves every caller.
    private readonly List<CollidableReference> _overlapScratch = new();

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)
        => RaycastCore(origin, direction, maxDistance, out hit, filter, exclusions: null);

    private bool RaycastCore(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
        QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        using QueryOperation scope = EnterQuery();
        var handler = new RayHitHandler(filter.Mobility, exclusions);
        _sim.RayCast(origin, direction, maxDistance, ref handler);

        if (!handler.DidHit)
        {
            hit = default;
            return false;
        }

        var point = origin + direction * handler.HitT;
        // RayHit.Body is a nullable static handle. A dynamic hit (only possible with QueryMobility.All/Dynamics)
        // has no static seam handle, so Body is null rather than reverse-looking-up a non-static hit.
        var seamHandle = handler.HitWasStatic ? ResolveSeamHandle(handler.HitStatic) : null;
        hit = new RayHit(handler.HitT, point, handler.HitNormal, seamHandle);
        return true;
    }

    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default)
        => SweepCapsuleCore(capsule, pose, direction, maxDistance, out hit, filter, exclusions: null);

    private bool SweepCapsuleCore(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
        out SweepHit hit, QueryFilter filter, StaticQueryExclusions? exclusions)
    {
        using QueryOperation scope = EnterQuery();
        var bepuCapsule = new Capsule(capsule.Radius, capsule.Length);
        var rigidPose = new RigidPose(pose.Position, pose.Orientation);
        // Bepu 2.4 sweeps a mesh or compound target by searching its child tree along velocity * maximumT
        // over a ray bounded by maximumT again (ConvexCompoundSweepOverlapFinder.FindOverlaps feeding
        // Mesh.FindLocalOverlaps, Compound.FindLocalOverlaps and BigCompound.FindLocalOverlaps). That search
        // reaches only maximumT squared along the motion, so below 1 it drops children short of the requested
        // distance. The whole motion is therefore carried in the velocity with maximumT exactly 1, where the
        // square is the identity. SweepHitHandler lowers maximumT only for a hit at t 0, which nothing can
        // beat. Bepu t then spans [0, 1] and scales back to distance.
        float scale = maxDistance > 0f && float.IsFinite(maxDistance) ? maxDistance : 1f;
        var velocity = new BodyVelocity(direction * scale);
        var handler = new SweepHitHandler(filter.Mobility, exclusions);

        _sim.Sweep(bepuCapsule, rigidPose, velocity, maxDistance / scale, _pool, ref handler);

        if (!handler.DidHit)
        {
            hit = default;
            return false;
        }

        // SweepHit.Body is a nullable static handle. A dynamic hit (only possible with QueryMobility.All/Dynamics)
        // has no static seam handle, so Body is null rather than reverse-looking-up a non-static hit.
        var seamHandle = handler.HitWasStatic ? ResolveSeamHandle(handler.HitStatic) : null;
        hit = new SweepHit(handler.HitT * scale, handler.HitLocation, handler.HitNormal, seamHandle);
        return true;
    }

    public unsafe bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
        => ComputePenetrationCore(capsule, pose, out mtv, exclusions: null);

    private unsafe bool ComputePenetrationCore(CapsuleShape capsule, Pose pose, out Vector3 mtv,
        StaticQueryExclusions? exclusions)
    {
        using QueryOperation scope = EnterQuery();
        // General capsule-vs-static depenetration over EVERY shape type (box, sphere, cylinder, convex
        // hull, triangle mesh, compound) via one BepuPhysics CollisionBatcher manifold query. This
        // replaced the per-shape analytic switch (which only handled box/sphere and reported no
        // penetration for hulls/meshes, trapping the capsule inside rocks). The deepest single contact
        // across all candidate pairs is the MTV.
        //
        // Mesh statics are ONE-SIDED (only front/CW-wound faces generate contacts). That is fine here:
        // the swept collide-and-slide always precedes this depenetration from a known-outside position,
        // so the capsule never begins a tick already through a wall.
        var bepuCapsule = new Capsule(capsule.Radius, capsule.Length);
        bepuCapsule.ComputeBounds(pose.Orientation, out var bMin, out var bMax);

        _overlapScratch.Clear();
        var collector = new OverlapCollector(_overlapScratch);
        _sim.BroadPhase.GetOverlaps(pose.Position + bMin, pose.Position + bMax, ref collector);
        if (collector.Found.Count == 0) { mtv = default; return false; }

        var callbacks = new PenetrationCallbacks();
        // Reuse the live collision-task registry off NarrowPhase. dt = 0 so there is no velocity-bound
        // expansion. speculativeMargin = 0 below so only real penetration (depth >= 0) reaches the callback.
        var batcher = new CollisionBatcher<PenetrationCallbacks>(
            _pool, _sim.Shapes, _sim.NarrowPhase.CollisionTaskRegistry, 0f, callbacks);
        try
        {
            int capsuleType = bepuCapsule.TypeId;
            int capsuleSize = Unsafe.SizeOf<Capsule>();
            foreach (var collidable in collector.Found)
            {
                if (collidable.Mobility != CollidableMobility.Static) continue;
                if (exclusions is not null && !exclusions.Allows(collidable)) continue;
                _sim.Statics.GetDescription(collidable.StaticHandle, out var desc);
                _sim.Shapes[desc.Shape.Type].GetShapeData(desc.Shape.Index, out var staticData, out _);

                // A = static, B = capsule. The capsule is an ephemeral stack value. CacheShapeB copies
                // it into the batcher's pool so no transient shape registration is needed in _sim.Shapes.
                batcher.CacheShapeB(desc.Shape.Type, capsuleType,
                    Unsafe.AsPointer(ref bepuCapsule), capsuleSize, out var cachedCapsule);
                var offsetB = pose.Position - desc.Pose.Position; // capsule relative to static (A)
                var staticOrientation = desc.Pose.Orientation;
                var capsuleOrientation = pose.Orientation;
                var continuation = new PairContinuation(0);
                batcher.AddDirectly(desc.Shape.Type, capsuleType, staticData, cachedCapsule,
                    in offsetB, in staticOrientation, in capsuleOrientation, 0f, in continuation);
            }
        }
        finally
        {
            // Flush executes all collision testers synchronously AND returns every pool buffer the
            // batcher took. It must run on every path, so it lives in finally.
            batcher.Flush();
        }

        callbacks = batcher.Callbacks; // read accumulated result AFTER flush
        if (callbacks.DeepestDepth <= 0f) { mtv = default; return false; }
        // With A = static, B = capsule, the BepuPhysics 2.4.0 contact normal points from the CAPSULE
        // toward the STATIC (the "into the surface" direction, verified empirically and locked by the
        // hull-penetration sign test). The minimum-translation vector that pushes the capsule OUT is the
        // negation, applied by the caller as capsulePos += mtv.
        mtv = -callbacks.DeepestNormal * callbacks.DeepestDepth;
        return true;
    }

    private SeamHandle? ResolveSeamHandle(BepuStaticHandle bepuHandle)
    {
        // O(1) reverse-lookup via _reverseHandles (see its field comment). This used to be a linear scan of
        // _handles run on every static ray/sweep hit.
        if (_reverseHandles.TryGetValue(bepuHandle.Value, out int id))
            return new SeamHandle(id);
        System.Diagnostics.Debug.Assert(false, "BepuPhysicsWorld: ray/sweep hit a static that cannot be resolved by seam handle - this is a bug");
        return null;
    }
}

// Broad-phase overlap enumerator - collects CollidableReferences from the static tree into a caller-owned list.
internal struct OverlapCollector : IBreakableForEach<CollidableReference>
{
    public readonly List<CollidableReference> Found;
    public OverlapCollector(List<CollidableReference> found) { Found = found; }
    public bool LoopBody(CollidableReference item) { Found.Add(item); return true; }
}

// CollisionBatcher callbacks for capsule-vs-static depenetration: keep the single deepest contact
// across all pairs. With A = static, B = capsule, the contact normal points capsule -> static. The
// caller negates it to get the push-OUT MTV. We take the deepest single contact, NOT a sum (summing
// two touching surfaces pushes diagonally into neither. The slide loop resolves any residual next
// iteration).
internal struct PenetrationCallbacks : ICollisionCallbacks
{
    public Vector3 DeepestNormal;   // unit, points capsule -> static (caller negates for push-out)
    public float DeepestDepth;      // > 0 = penetrating

    public bool AllowCollisionTesting(int pairId, int childA, int childB) => true;

    public void OnChildPairCompleted(int pairId, int childA, int childB, ref ConvexContactManifold m)
        => Accumulate(ref m);

    public void OnPairCompleted<TManifold>(int pairId, ref TManifold m)
        where TManifold : unmanaged, IContactManifold<TManifold>
        => Accumulate(ref m);

    // Accumulating in BOTH OnChildPairCompleted and OnPairCompleted is safe (deepest-wins is idempotent).
    // A non-convex (mesh/compound) result fans out per overlapping triangle/child into a
    // NonconvexContactManifold. The generic IContactManifold<T> handles convex and non-convex alike.
    private void Accumulate<TManifold>(ref TManifold m)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        for (int i = 0; i < m.Count; i++)
        {
            m.GetContact(i, out _, out var normal, out float depth, out _);
            if (depth > DeepestDepth)
            {
                DeepestDepth = depth;
                DeepestNormal = normal;
            }
        }
    }
}
