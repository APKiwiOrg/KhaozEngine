using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using Xunit;

// Alias to disambiguate BepuPhysics.Simulation from the KhaozEngine.Simulation namespace.
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// Gate: BepuPhysics must step headlessly and be run-to-run deterministic on one binary.
// Single-threaded (null dispatcher), fixed SolveDescription so results are fully reproducible.
public class BepuDeterminismGateTests
{
    private static (float rayT, float sweepT) RunOnce()
    {
        using var pool = new BufferPool();

        var sim = BepuSim.Create(
            pool,
            new GateNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new GatePoseIntegratorCallbacks(),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));

        // One static box centred at Z=5 (half-extent 1 along each axis).
        TypedIndex box = sim.Shapes.Add(new Box(2f, 2f, 2f));
        sim.Statics.Add(new StaticDescription(new Vector3(0f, 0f, 5f), box));

        // Single-threaded step (null dispatcher).
        sim.Timestep(1f / 60f, null);

        // Ray from origin along +Z: front face of the box is at Z = 5 - 1 = 4.
        // Note: Simulation.RayCast in BepuPhysics 2.4.0 does NOT take a BufferPool parameter.
        var rayHandler = new GateRayHandler();
        sim.RayCast(Vector3.Zero, Vector3.UnitZ, 100f, ref rayHandler);

        // Capsule sweep toward +Z; records the time of impact.
        var capsuleShape = new Capsule(0.4f, 1.0f);
        var sweepHandler = new GateSweepHandler();
        sim.Sweep(
            capsuleShape,
            new RigidPose(Vector3.Zero),
            new BodyVelocity(Vector3.UnitZ),
            100f,
            pool,
            ref sweepHandler);

        float rt = rayHandler.HitT;
        float st = sweepHandler.HitT;

        sim.Dispose();
        pool.Clear();
        return (rt, st);
    }

    [Fact]
    public void Bepu_StepsHeadlessly_AndIsRunToRunDeterministic()
    {
        var a = RunOnce();
        var b = RunOnce();

        // Headless-step + query check.
        Assert.True(a.rayT > 0f, $"Ray should hit the static box (got {a.rayT})");
        Assert.True(a.sweepT >= 0f, $"Capsule sweep should hit the static box (got {a.sweepT})");

        // Determinism: exact bit-identical on the same binary.
        Assert.Equal(a.rayT, b.rayT);
        Assert.Equal(a.sweepT, b.sweepT);
    }
}

// Minimal no-gravity pose integrator (no-op since we have no dynamic bodies in the gate scene).
struct GatePoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(BepuSim simulation) { }

    public void PrepareForIntegration(float dt) { }

    public void IntegrateVelocity(
        Vector<int> bodyIndices,
        Vector3Wide position,
        QuaternionWide orientation,
        BodyInertiaWide localInertia,
        Vector<int> integrationMask,
        int workerIndex,
        Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        // No gravity; no dynamic bodies in the gate scene anyway.
    }
}

// Minimal narrow-phase callbacks: allow all pairs, use standard spring settings.
struct GateNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

    public GateNarrowPhaseCallbacks(SpringSettings contactSpringiness, float maximumRecoveryVelocity = 2f, float frictionCoefficient = 1f)
    {
        ContactSpringiness = contactSpringiness;
        MaximumRecoveryVelocity = maximumRecoveryVelocity;
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
    {
        return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
    {
        return true;
    }

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
    {
        return true;
    }

    public void Dispose() { }
}

// Ray hit handler: records the nearest hit t.
struct GateRayHandler : IRayHitHandler
{
    public float HitT;

    public GateRayHandler() { HitT = float.MaxValue; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < HitT)
        {
            HitT = t;
            maximumT = t; // cull further candidates
        }
    }
}

// Sweep hit handler: records the first (nearest) time of impact.
struct GateSweepHandler : ISweepHitHandler
{
    public float HitT;

    public GateSweepHandler() { HitT = float.MaxValue; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
    {
        if (t < HitT)
        {
            HitT = t;
            maximumT = t; // cull further candidates
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
    {
        HitT = 0f;
        maximumT = 0f;
    }
}
