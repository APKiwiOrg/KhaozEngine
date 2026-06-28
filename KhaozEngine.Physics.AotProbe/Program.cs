// BepuPhysics 2.4.0 NativeAOT gate probe.
// Builds a tiny scene (one static box at Z=5), steps once single-threaded,
// casts a ray and a capsule sweep toward +Z, and prints the hit distances.
// The gate test is that this links, runs, and prints a hit near 4.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;

var pool = new BufferPool();

var sim = Simulation.Create(
    pool,
    new ProbeNarrowPhaseCallbacks(new SpringSettings(30, 1)),
    new ProbePoseIntegratorCallbacks(),
    new SolveDescription(velocityIterationCount: 8, substepCount: 1));

TypedIndex box = sim.Shapes.Add(new Box(2f, 2f, 2f));
sim.Statics.Add(new StaticDescription(new Vector3(0f, 0f, 5f), box));

// Single-threaded timestep (null dispatcher).
sim.Timestep(1f / 60f, null);

// Ray from origin toward +Z; front face of the box is at Z = 4.
var rayHandler = new ProbeRayHandler();
sim.RayCast(Vector3.Zero, Vector3.UnitZ, 100f, ref rayHandler);

// Capsule sweep toward +Z.
var capsuleShape = new Capsule(0.4f, 1.0f);
var sweepHandler = new ProbeSweepHandler();
sim.Sweep(capsuleShape, new RigidPose(Vector3.Zero), new BodyVelocity(Vector3.UnitZ), 100f, pool, ref sweepHandler);

Console.WriteLine($"AOT PROBE: ray hit t={rayHandler.HitT:F4}  sweep hit t={sweepHandler.HitT:F4}");

sim.Dispose();
pool.Clear();

// ---- callback / handler structs (mirrors BepuDeterminismGateTests) ----

struct ProbePoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt) { }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity) { }
}

struct ProbeNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

    public ProbeNarrowPhaseCallbacks(SpringSettings contactSpringiness, float maximumRecoveryVelocity = 2f, float frictionCoefficient = 1f)
    {
        ContactSpringiness = contactSpringiness;
        MaximumRecoveryVelocity = maximumRecoveryVelocity;
        FrictionCoefficient = frictionCoefficient;
    }

    public void Initialize(Simulation simulation)
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
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

struct ProbeRayHandler : IRayHitHandler
{
    public float HitT;
    public ProbeRayHandler() { HitT = float.MaxValue; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < HitT) { HitT = t; maximumT = t; }
    }
}

struct ProbeSweepHandler : ISweepHitHandler
{
    public float HitT;
    public ProbeSweepHandler() { HitT = float.MaxValue; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
    {
        if (t < HitT) { HitT = t; maximumT = t; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) { HitT = 0f; maximumT = 0f; }
}
