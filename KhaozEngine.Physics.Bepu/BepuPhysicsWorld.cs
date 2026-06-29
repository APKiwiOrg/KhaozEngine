using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using KhaozEngine.Physics;

using BepuSim = BepuPhysics.Simulation;
using BepuStaticHandle = BepuPhysics.StaticHandle;
using SeamHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>BepuPhysics v2 backend for <see cref="IPhysicsWorld"/>. Single-threaded, deterministic
/// (null dispatcher, fixed SolveDescription). The only assembly in the engine that references BepuPhysics;
/// consumers depend on the dependency-free <c>KhaozEngine.Physics</c> seam and add this backend
/// explicitly, matching the Netcode.LiteNetLib / WorldStore.Sqlite pattern.</summary>
public sealed class BepuPhysicsWorld : IPhysicsWorld
{
    private readonly BufferPool _pool;
    private readonly BepuSim _sim;

    // Seam int id -> (Bepu StaticHandle, shape TypedIndex) mapping.
    // The TypedIndex is stored so we can remove the shape from _sim.Shapes on RemoveStatic;
    // Statics.Remove only removes the body entry, not the shape, so without this the shape
    // pool grows unbounded across streaming load/unload cycles.
    private readonly Dictionary<int, (BepuStaticHandle Handle, TypedIndex Shape)> _handles = new();
    private int _nextId;

    public BepuPhysicsWorld()
    {
        _pool = new BufferPool();
        _sim = BepuSim.Create(
            _pool,
            new PhysicsNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new PhysicsPoseIntegratorCallbacks(),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
    }

    public SeamHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
    {
        var shapeIndex = ShapeFactory.Add(_sim, _pool, shape);
        var desc = new StaticDescription(pose.Position, pose.Orientation, shapeIndex);
        var bepuHandle = _sim.Statics.Add(desc);

        int id = _nextId++;
        _handles[id] = (bepuHandle, shapeIndex);
        return new SeamHandle(id);
    }

    public void RemoveStatic(SeamHandle handle)
    {
        if (_handles.TryGetValue(handle.Value, out var entry))
        {
            _sim.Statics.Remove(entry.Handle);
            // Remove the shape from the shape pool. Compound shapes need recursive removal
            // to free child shapes; simple convex shapes (Box, Sphere, Capsule, etc.) use Remove.
            // RecursivelyRemoveAndDispose handles both cases safely.
            _sim.Shapes.RecursivelyRemoveAndDispose(entry.Shape, _pool);
            _handles.Remove(handle.Value);
        }
    }

    public void Step(float dt) => _sim.Timestep(dt, null);

    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)
    {
        var handler = new RayHitHandler();
        _sim.RayCast(origin, direction, maxDistance, ref handler);

        if (!handler.DidHit)
        {
            hit = default;
            return false;
        }

        var point = origin + direction * handler.HitT;
        var seamHandle = ResolveSeamHandle(handler.HitStatic);
        hit = new RayHit(handler.HitT, point, handler.HitNormal, seamHandle);
        return true;
    }

    public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default)
    {
        var bepuCapsule = new Capsule(capsule.Radius, capsule.Length);
        var rigidPose = new RigidPose(pose.Position, pose.Orientation);
        var velocity = new BodyVelocity(direction);
        var handler = new SweepHitHandler();

        _sim.Sweep(bepuCapsule, rigidPose, velocity, maxDistance, _pool, ref handler);

        if (!handler.DidHit)
        {
            hit = default;
            return false;
        }

        var seamHandle = ResolveSeamHandle(handler.HitStatic);
        hit = new SweepHit(handler.HitT, handler.HitLocation, handler.HitNormal, seamHandle);
        return true;
    }

    public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
    {
        // Compute a conservative AABB for the capsule in world space.
        var bepuCapsule = new Capsule(capsule.Radius, capsule.Length);
        bepuCapsule.ComputeBounds(pose.Orientation, out var bepuMin, out var bepuMax);
        var boundsMin = pose.Position + bepuMin;
        var boundsMax = pose.Position + bepuMax;

        // Collect all statics whose AABB overlaps the capsule AABB.
        var collector = new OverlapCollector();
        _sim.BroadPhase.GetOverlaps(boundsMin, boundsMax, ref collector);

        if (collector.Found.Count == 0)
        {
            mtv = default;
            return false;
        }

        // For each candidate, compute the capsule-vs-shape penetration analytically.
        // Accumulate the deepest MTV (largest depth, dominant axis).
        Vector3 deepest = Vector3.Zero;
        float deepestDepth = 0f;

        foreach (var collidable in collector.Found)
        {
            if (collidable.Mobility != CollidableMobility.Static)
                continue;

            _sim.Statics.GetDescription(collidable.StaticHandle, out var desc);
            var staticPose = new Pose(desc.Pose.Position, desc.Pose.Orientation);

            bool overlap = ComputeCapsuleShapeMtv(capsule, pose, desc.Shape, staticPose, out var candidateMtv);
            if (overlap)
            {
                float depth = candidateMtv.Length();
                if (depth > deepestDepth)
                {
                    deepestDepth = depth;
                    deepest = candidateMtv;
                }
            }
        }

        if (deepestDepth <= 0f)
        {
            mtv = default;
            return false;
        }

        mtv = deepest;
        return true;
    }

    // Analytical penetration per shape type. Returns true and an MTV when overlapping.
    private bool ComputeCapsuleShapeMtv(CapsuleShape capsule, Pose capsulePose, TypedIndex shapeIndex, Pose staticPose, out Vector3 mtv)
    {
        // For SP1: only Box statics are tested. Implement capsule-vs-box analytically.
        // For other shapes, fall back to a sweep-based approximation.

        // The capsule's local Y axis is the segment axis.
        // Capsule segment endpoints in world space:
        var localHalfAxis = Vector3.Transform(new Vector3(0, capsule.Length * 0.5f, 0), capsulePose.Orientation);
        var p0 = capsulePose.Position - localHalfAxis; // bottom sphere centre
        var p1 = capsulePose.Position + localHalfAxis; // top sphere centre
        float r = capsule.Radius;

        // Transform capsule segment into static body local space.
        var invOri = Quaternion.Inverse(staticPose.Orientation);
        var relP0 = Vector3.Transform(p0 - staticPose.Position, invOri);
        var relP1 = Vector3.Transform(p1 - staticPose.Position, invOri);

        // Probe the shape type by checking against known type IDs.
        Vector3 halfExtents;
        if (TryGetBoxHalfExtents(shapeIndex, out halfExtents))
        {
            return ComputeCapsuleVsBoxMtv(relP0, relP1, r, halfExtents, staticPose.Orientation, out mtv);
        }
        else if (TryGetSphereRadius(shapeIndex, out float sphereR))
        {
            // Capsule vs sphere: find closest point on segment to sphere centre (relOrigin = zero).
            var segDir = relP1 - relP0;
            float segLenSq = segDir.LengthSquared();
            float t2 = segLenSq > 1e-10f ? Math.Clamp(Vector3.Dot(-relP0, segDir) / segLenSq, 0f, 1f) : 0f;
            var closest = relP0 + segDir * t2;
            float dist = closest.Length();
            float combinedR = r + sphereR;
            if (dist >= combinedR)
            {
                mtv = default;
                return false;
            }
            float depth = combinedR - dist;
            var dirWS = dist > 1e-6f
                ? Vector3.Transform(-closest / dist, staticPose.Orientation)
                : Vector3.UnitY;
            mtv = dirWS * depth;
            return true;
        }
        else
        {
            // Hull/mesh/compound depenetration is deferred to SP2's collision-manifold path.
            // SP1 reports no penetration for these shapes and relies on the swept collide-and-slide
            // to keep the capsule out. Returning true here caused spurious nudges near rocks due to
            // AABB-vs-geometry overlap slack being misread as actual penetration.
            mtv = default;
            return false;
        }
    }

    private bool TryGetBoxHalfExtents(TypedIndex shapeIndex, out Vector3 halfExtents)
    {
        // Check the type id BEFORE calling GetShape: GetShape<Box>(index) reads from
        // the Box batch unconditionally and interprets mismatched-type memory as a Box.
        var boxTypeId = default(Box).TypeId;
        if (shapeIndex.Type != boxTypeId)
        {
            halfExtents = default;
            return false;
        }
        ref var box = ref _sim.Shapes.GetShape<Box>(shapeIndex.Index);
        halfExtents = new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength);
        return true;
    }

    private bool TryGetSphereRadius(TypedIndex shapeIndex, out float radius)
    {
        var sphereTypeId = default(Sphere).TypeId;
        if (shapeIndex.Type != sphereTypeId)
        {
            radius = 0f;
            return false;
        }
        ref var sphere = ref _sim.Shapes.GetShape<Sphere>(shapeIndex.Index);
        radius = sphere.Radius;
        return true;
    }

    // Analytical capsule-vs-AABB MTV in the AABB's local space.
    // capsule segment endpoints (relP0, relP1) and radius r are already in local space.
    // halfExtents are the box half-extents. orientation transforms local -> world.
    private static bool ComputeCapsuleVsBoxMtv(
        Vector3 relP0, Vector3 relP1, float r, Vector3 halfExtents,
        Quaternion boxOrientation, out Vector3 mtv)
    {
        // Find closest point on the segment to the AABB, then compute sphere-vs-AABB for that sphere.
        // We test penetration of the capsule as the union of spheres along the segment.
        // For each sphere endpoint plus the parametric closest point, find the best (deepest) MTV.

        // Find t in [0,1] that minimises distance from segment point to AABB.
        // We use the closest point on segment to the AABB centre (origin in local space).
        var segDir = relP1 - relP0;
        float segLenSq = segDir.LengthSquared();
        // t for closest point on segment to AABB centre (origin):
        float t = segLenSq > 1e-10f ? Math.Clamp(Vector3.Dot(-relP0, segDir) / segLenSq, 0f, 1f) : 0f;
        var sphereCentreLocal = relP0 + segDir * t;

        // Evaluate all three candidate sphere centres and return the deepest MTV.
        // Short-circuit OR would return the FIRST penetrating candidate, not the deepest,
        // giving a wrong push direction when a shallower candidate happens to test first.
        bool anyHit = false;
        Vector3 deepestMtv = default;
        float deepestDepthSq = 0f;

        if (SpherePenetratesBox(sphereCentreLocal, r, halfExtents, boxOrientation, out var mtv0))
        {
            float dsq = mtv0.LengthSquared();
            if (dsq > deepestDepthSq) { deepestDepthSq = dsq; deepestMtv = mtv0; }
            anyHit = true;
        }
        if (SpherePenetratesBox(relP0, r, halfExtents, boxOrientation, out var mtv1))
        {
            float dsq = mtv1.LengthSquared();
            if (dsq > deepestDepthSq) { deepestDepthSq = dsq; deepestMtv = mtv1; }
            anyHit = true;
        }
        if (SpherePenetratesBox(relP1, r, halfExtents, boxOrientation, out var mtv2))
        {
            float dsq = mtv2.LengthSquared();
            if (dsq > deepestDepthSq) { deepestDepthSq = dsq; deepestMtv = mtv2; }
            anyHit = true;
        }

        mtv = deepestMtv;
        return anyHit;
    }

    private static bool SpherePenetratesBox(
        Vector3 sphereCentreLocal, float r, Vector3 halfExtents,
        Quaternion boxOrientation, out Vector3 mtvWorld)
    {
        // Clamp to AABB to get the closest point on the surface.
        var clampedLocal = new Vector3(
            Math.Clamp(sphereCentreLocal.X, -halfExtents.X, halfExtents.X),
            Math.Clamp(sphereCentreLocal.Y, -halfExtents.Y, halfExtents.Y),
            Math.Clamp(sphereCentreLocal.Z, -halfExtents.Z, halfExtents.Z));

        var delta = sphereCentreLocal - clampedLocal;
        float distSq = delta.LengthSquared();

        if (distSq >= r * r && !(distSq < 1e-10f))
        {
            // Not penetrating (or exactly on surface).
            mtvWorld = default;
            return false;
        }

        Vector3 mtvLocal;

        if (distSq < 1e-10f)
        {
            // Sphere centre is inside the AABB: find the minimum penetration axis.
            // Compute distance to each face and pick the shallowest (minimum push).
            float dx = halfExtents.X - MathF.Abs(sphereCentreLocal.X);
            float dy = halfExtents.Y - MathF.Abs(sphereCentreLocal.Y);
            float dz = halfExtents.Z - MathF.Abs(sphereCentreLocal.Z);

            if (dx <= dy && dx <= dz)
                mtvLocal = new Vector3(MathF.Sign(sphereCentreLocal.X) * (dx + r), 0, 0);
            else if (dy <= dz)
                mtvLocal = new Vector3(0, MathF.Sign(sphereCentreLocal.Y) * (dy + r), 0);
            else
                mtvLocal = new Vector3(0, 0, MathF.Sign(sphereCentreLocal.Z) * (dz + r));
        }
        else
        {
            // Sphere centre outside but intersecting: push along the delta direction.
            float dist = MathF.Sqrt(distSq);
            float depth = r - dist;
            mtvLocal = (delta / dist) * depth;
        }

        mtvWorld = Vector3.Transform(mtvLocal, boxOrientation);
        return true;
    }

    private SeamHandle ResolveSeamHandle(BepuStaticHandle bepuHandle)
    {
        // Reverse-lookup seam id from Bepu handle.
        foreach (var kv in _handles)
        {
            if (kv.Value.Handle.Value == bepuHandle.Value)
                return new SeamHandle(kv.Key);
        }
        System.Diagnostics.Debug.Assert(false, "BepuPhysicsWorld: ray/sweep hit a static that cannot be resolved by seam handle - this is a bug");
        return new SeamHandle(-1);
    }

    public void Dispose()
    {
        _sim.Dispose();
        _pool.Clear();
    }
}

// Broad-phase overlap enumerator - collects CollidableReferences from the static tree.
internal struct OverlapCollector : IBreakableForEach<CollidableReference>
{
    public readonly List<CollidableReference> Found = new();
    public OverlapCollector() { }
    public bool LoopBody(CollidableReference item) { Found.Add(item); return true; }
}

// Minimal no-gravity pose integrator (no dynamic bodies in SP1).
internal struct PhysicsPoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(BepuSim simulation) { }
    public void PrepareForIntegration(float dt) { }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity) { }
}

// Minimal narrow-phase callbacks.
internal struct PhysicsNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

    public PhysicsNarrowPhaseCallbacks(SpringSettings springSettings, float maxRecoveryVelocity = 2f, float frictionCoefficient = 1f)
    {
        ContactSpringiness = springSettings;
        MaximumRecoveryVelocity = maxRecoveryVelocity;
        FrictionCoefficient = frictionCoefficient;
    }

    public void Initialize(BepuSim simulation)
    {
        if (ContactSpringiness.AngularFrequency == 0 && ContactSpringiness.TwiceDampingRatio == 0)
        {
            ContactSpringiness = new SpringSettings(30, 1);
            MaximumRecoveryVelocity = 2f;
            FrictionCoefficient = 1f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = FrictionCoefficient;
        pairMaterial.MaximumRecoveryVelocity = MaximumRecoveryVelocity;
        pairMaterial.SpringSettings = ContactSpringiness;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
        => true;

    public void Dispose() { }
}
