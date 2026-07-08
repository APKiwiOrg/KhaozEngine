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
using BepuBodyHandle = BepuPhysics.BodyHandle;
using SeamHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>BepuPhysics v2 backend for <see cref="IPhysicsWorld"/>. Single-threaded, deterministic
/// (null dispatcher, fixed SolveDescription, per-step gravity applied uniformly). The only assembly in the
/// engine that references BepuPhysics; consumers depend on the dependency-free <c>KhaozEngine.Physics</c> seam
/// and add this backend explicitly, matching the Netcode.LiteNetLib / WorldStore.Sqlite pattern.</summary>
public sealed class BepuPhysicsWorld : IPhysicsWorld
{
    /// <summary>Standard Earth gravity (m/s^2, -Y) used when no gravity is supplied to the constructor.</summary>
    public static readonly Vector3 DefaultGravity = new(0f, -9.81f, 0f);

    private readonly BufferPool _pool;
    private readonly BepuSim _sim;

    // Seam int id -> (Bepu StaticHandle, shape TypedIndex) mapping.
    // The TypedIndex is stored so we can remove the shape from _sim.Shapes on RemoveStatic;
    // Statics.Remove only removes the body entry, not the shape, so without this the shape
    // pool grows unbounded across streaming load/unload cycles.
    private readonly Dictionary<int, (BepuStaticHandle Handle, TypedIndex Shape)> _handles = new();
    // Seam int id -> (Bepu BodyHandle, shape TypedIndex) for dynamic bodies. Same shape-pool discipline:
    // Bodies.Remove frees the body entry but NOT the shape, so RecursivelyRemoveAndDispose on RemoveDynamic
    // keeps the shape pool from growing across body add/remove cycles.
    private readonly Dictionary<int, (BepuBodyHandle Handle, TypedIndex Shape)> _dynamics = new();
    // Restitution bookkeeping for the explicit bounce pass in Step (see Step): the set of dynamic-body ids with a
    // non-zero restitution, each id's coefficient, and a scratch snapshot of pre-step velocities (reused to avoid
    // per-step allocation). A body with zero restitution is never in these, so the common case adds no overhead.
    private readonly HashSet<int> _restitutiveDynamics = new();
    private readonly Dictionary<int, float> _restitutionOf = new();
    private readonly Dictionary<int, Vector3> _preStepVel = new();
    private int _nextId;

    /// <summary>A world with standard Earth gravity (<see cref="DefaultGravity"/>).</summary>
    public BepuPhysicsWorld() : this(DefaultGravity) { }

    /// <summary>A world with the given <paramref name="gravity"/> (m/s^2). Pass <c>Vector3.Zero</c> for the
    /// static-only, non-falling behaviour of the pre-dynamics backend.</summary>
    public BepuPhysicsWorld(Vector3 gravity)
    {
        _pool = new BufferPool();
        _sim = BepuSim.Create(
            _pool,
            new PhysicsNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new PhysicsPoseIntegratorCallbacks(gravity),
            // Substepping (4) gives dynamic-body contacts stable, deterministic resolution and lets the stiff
            // restitution spring rebound within a step. Static-only queries (raycast/sweep/penetration) are
            // unaffected by the substep count.
            new SolveDescription(velocityIterationCount: 8, substepCount: 4));
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

    public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null)
    {
        var rigidPose = new RigidPose(pose.Position, pose.Orientation);
        var velocity = new BodyVelocity(body.LinearVelocity, body.AngularVelocity);
        // Sleep threshold: negative keeps the Bepu default; otherwise honour the caller (0 disables sleep).
        float sleep = body.SleepThreshold < 0f ? 0.01f : body.SleepThreshold;
        var activity = new BodyActivityDescription(sleep);

        int id = _nextId++;
        BepuBodyHandle bepuHandle;
        TypedIndex shapeIndex;

        if (body.Mass <= 0f)
        {
            // Infinite-mass (kinematic) body: no inertia, unaffected by gravity/impacts but still moved by its
            // velocity. Reuse the static/kinematic shape path (base-alignment wrappers, no inertia needed).
            shapeIndex = ShapeFactory.Add(_sim, _pool, shape);
            var desc = BodyDescription.CreateKinematic(rigidPose, velocity, new CollidableDescription(shapeIndex), activity);
            bepuHandle = _sim.Bodies.Add(desc);
        }
        else
        {
            shapeIndex = ShapeFactory.AddDynamic(_sim, _pool, shape, body.Mass, out BodyInertia inertia);
            var desc = BodyDescription.CreateDynamic(rigidPose, velocity, inertia, new CollidableDescription(shapeIndex), activity);
            bepuHandle = _sim.Bodies.Add(desc);
        }

        _dynamics[id] = (bepuHandle, shapeIndex);
        float restitution = material?.Restitution ?? 0f;
        if (restitution > 0f)
        {
            _restitutiveDynamics.Add(id);
            _restitutionOf[id] = restitution;
        }
        return new DynamicBodyHandle(id);
    }

    public void RemoveDynamic(DynamicBodyHandle handle)
    {
        if (_dynamics.TryGetValue(handle.Value, out var entry))
        {
            _sim.Bodies.Remove(entry.Handle);
            _sim.Shapes.RecursivelyRemoveAndDispose(entry.Shape, _pool);
            _dynamics.Remove(handle.Value);
            _restitutiveDynamics.Remove(handle.Value);
            _restitutionOf.Remove(handle.Value);
        }
    }

    public Pose GetDynamicPose(DynamicBodyHandle handle)
    {
        var body = _sim.Bodies.GetBodyReference(RequireDynamic(handle));
        RigidPose p = body.Pose;
        return new Pose(p.Position, p.Orientation);
    }

    public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
    {
        var body = _sim.Bodies.GetBodyReference(RequireDynamic(handle));
        BodyVelocity v = body.Velocity;
        linear = v.Linear;
        angular = v.Angular;
    }

    public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
    {
        var body = _sim.Bodies.GetBodyReference(RequireDynamic(handle));
        body.Velocity = new BodyVelocity(linear, angular);
        body.Awake = true; // a velocity change must wake a sleeping body or it will not move
    }

    public bool IsAwake(DynamicBodyHandle handle)
        => _sim.Bodies.GetBodyReference(RequireDynamic(handle)).Awake;

    private BepuBodyHandle RequireDynamic(DynamicBodyHandle handle)
    {
        if (!_dynamics.TryGetValue(handle.Value, out var entry))
            throw new ArgumentException($"DynamicBodyHandle {handle.Value} is not a live dynamic body.", nameof(handle));
        return entry.Handle;
    }

    public void Step(float dt)
    {
        // Explicit, deterministic restitution. Bepu 2.4 has no restitution coefficient and its contact
        // MaximumRecoveryVelocity only acts on penetration depth (which gives a constant-height limit cycle, not a
        // real coefficient of restitution). So for every awake restitutive dynamic body we snapshot the velocity,
        // let the solver arrest the impact this step, and if the body's speed along the pre-step motion direction
        // was reduced by a contact (the solver removed approach velocity), we return restitution x the removed
        // speed along that direction. This reproduces a true coefficient of restitution (each bounce apex decays
        // geometrically) and stays fully deterministic under fixed dt. Non-restitutive bodies are untouched.
        if (_restitutiveDynamics.Count > 0)
        {
            _preStepVel.Clear();
            foreach (int id in _restitutiveDynamics)
            {
                var body = _sim.Bodies.GetBodyReference(_dynamics[id].Handle);
                if (body.Awake)
                    _preStepVel[id] = body.Velocity.Linear;
            }

            _sim.Timestep(dt, null);

            foreach (var kv in _preStepVel)
            {
                Vector3 pre = kv.Value;
                float preSpeed = pre.Length();
                if (preSpeed < 1e-4f) continue;
                var body = _sim.Bodies.GetBodyReference(_dynamics[kv.Key].Handle);
                if (!body.Exists) continue;
                Vector3 dir = pre / preSpeed;                       // pre-step motion direction (unit)
                Vector3 post = body.Velocity.Linear;
                // Speed along the pre-step direction that a contact removed this step (>0 means arrested).
                float removed = preSpeed - Vector3.Dot(post, dir);
                if (removed <= 0.05f) continue;                     // ignore ordinary damping/friction, only real impacts
                float restitution = _restitutionOf[kv.Key];
                // Reflect: add restitution x the removed approach speed back, but in the OPPOSITE direction, so the
                // body rebounds at restitution x its arrested approach speed.
                body.Velocity.Linear = post - dir * (restitution * removed);
                body.Awake = true;
            }
        }
        else
        {
            _sim.Timestep(dt, null);
        }
    }

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

// Pose integrator that applies a uniform gravity to every dynamic body each substep. Deterministic: the
// gravity vector is fixed at construction and the per-step gravity*dt delta is computed once in
// PrepareForIntegration (no per-body branching, no wall clock, no randomness). Kinematic bodies are not
// velocity-integrated (IntegrateVelocityForKinematics = false), so infinite-mass bodies ignore gravity.
internal struct PhysicsPoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private readonly Vector3 _gravity;
    private Vector3Wide _gravityDt;

    public PhysicsPoseIntegratorCallbacks(Vector3 gravity)
    {
        _gravity = gravity;
        _gravityDt = default;
    }

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(BepuSim simulation) { }

    public void PrepareForIntegration(float dt)
    {
        // Precompute gravity * dt once per (sub)step and broadcast to the SIMD-wide form used per bundle.
        Vector3Wide.Broadcast(_gravity * dt, out _gravityDt);
    }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity)
    {
        Vector3Wide.Add(velocity.Linear, _gravityDt, out velocity.Linear);
    }
}

// Narrow-phase callbacks. Contacts are inelastic here (no restitution in the solver): restitution is applied
// deterministically by BepuPhysicsWorld.Step as an explicit post-solve velocity reflection (Bepu 2.4 has no
// restitution coefficient and its contact recovery velocity only acts on penetration, giving a constant-height
// limit cycle rather than a real coefficient of restitution). Contacts are only generated when at least one
// body is dynamic (a purely static pair never collides).
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
