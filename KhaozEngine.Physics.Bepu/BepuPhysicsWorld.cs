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

    public unsafe bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
    {
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

        var collector = new OverlapCollector();
        _sim.BroadPhase.GetOverlaps(pose.Position + bMin, pose.Position + bMax, ref collector);
        if (collector.Found.Count == 0) { mtv = default; return false; }

        var callbacks = new PenetrationCallbacks();
        // Reuse the live collision-task registry off NarrowPhase. dt = 0 so there is no velocity-bound
        // expansion; speculativeMargin = 0 below so only real penetration (depth >= 0) reaches the callback.
        var batcher = new CollisionBatcher<PenetrationCallbacks>(
            _pool, _sim.Shapes, _sim.NarrowPhase.CollisionTaskRegistry, 0f, callbacks);
        try
        {
            int capsuleType = bepuCapsule.TypeId;
            int capsuleSize = Unsafe.SizeOf<Capsule>();
            foreach (var collidable in collector.Found)
            {
                if (collidable.Mobility != CollidableMobility.Static) continue;
                _sim.Statics.GetDescription(collidable.StaticHandle, out var desc);
                _sim.Shapes[desc.Shape.Type].GetShapeData(desc.Shape.Index, out var staticData, out _);

                // A = static, B = capsule. The capsule is an ephemeral stack value; CacheShapeB copies
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
        // toward the STATIC (the "into the surface" direction; verified empirically and locked by the
        // hull-penetration sign test). The minimum-translation vector that pushes the capsule OUT is the
        // negation, applied by the caller as capsulePos += mtv.
        mtv = -callbacks.DeepestNormal * callbacks.DeepestDepth;
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

// CollisionBatcher callbacks for capsule-vs-static depenetration: keep the single deepest contact
// across all pairs. With A = static, B = capsule, the contact normal points capsule -> static; the
// caller negates it to get the push-OUT MTV. We take the deepest single contact, NOT a sum (summing
// two touching surfaces pushes diagonally into neither; the slide loop resolves any residual next
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
    // NonconvexContactManifold; the generic IContactManifold<T> handles convex and non-convex alike.
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
