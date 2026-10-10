using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Trees;
using KhaozEngine.Physics;

using BepuStaticHandle = BepuPhysics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

// Mobility gate shared by the ray and sweep handlers. Bepu never surfaces a QueryFilter through its
// hit-handler callbacks, so AllowTest silently accepted everything before this: a QueryFilter.Statics
// ground probe still resolved against dynamic bodies (a crate under a character read as ground). The
// gate here is what makes QueryMobility actually take effect. QueryMobility.All returns true for every
// collidable, so the default query keeps hitting everything.
internal static class QueryMobilityGate
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Allows(KhaozEngine.Physics.QueryMobility mobility, CollidableReference collidable)
        => mobility switch
        {
            KhaozEngine.Physics.QueryMobility.Statics => collidable.Mobility == CollidableMobility.Static,
            KhaozEngine.Physics.QueryMobility.Dynamics => collidable.Mobility != CollidableMobility.Static,
            _ => true, // QueryMobility.All
        };
}

/// <summary>Records the nearest ray hit for <see cref="BepuPhysicsWorld.Raycast"/>, gated by
/// query mobility and static selection.</summary>
internal struct RayHitHandler : IRayHitHandler
{
    public float HitT;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;
    public bool HitWasStatic; // the recorded nearest hit was a static (HitStatic is meaningful); false = dynamic
    public KhaozEngine.Physics.QueryMobility Mobility;
    private readonly StaticQueryExclusions? _exclusions;

    public RayHitHandler(KhaozEngine.Physics.QueryMobility mobility, StaticQueryExclusions? exclusions = null)
    {
        HitT = float.MaxValue;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
        HitWasStatic = false;
        Mobility = mobility;
        _exclusions = exclusions;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
        => QueryMobilityGate.Allows(Mobility, collidable) && (_exclusions?.Allows(collidable) ?? true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
    {
        if (t < HitT)
        {
            HitT = t;
            HitNormal = normal;
            HitWasStatic = collidable.Mobility == CollidableMobility.Static;
            HitStatic = HitWasStatic ? collidable.StaticHandle : default;
            maximumT = t; // cull further candidates
            DidHit = true;
        }
    }
}

/// <summary>Records the nearest sweep hit for <see cref="BepuPhysicsWorld.SweepCapsule"/>, gated by
/// query mobility, static selection and, with <see cref="QueryFilter.CullBackFaces"/>, mesh back faces.</summary>
internal struct SweepHitHandler : ISweepHitHandler
{
    public float HitT;
    public Vector3 HitLocation;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;
    public bool HitWasStatic; // the recorded nearest hit was a static (HitStatic is meaningful); false = dynamic
    public KhaozEngine.Physics.QueryMobility Mobility;
    private readonly StaticQueryExclusions? _exclusions;
    // Set only when the filter asks for back-face culling. The sweep direction it is tested against.
    private readonly Simulation? _cullSimulation;
    private readonly Vector3 _cullDirection;

    public SweepHitHandler(KhaozEngine.Physics.QueryMobility mobility, StaticQueryExclusions? exclusions = null,
        Simulation? cullSimulation = null, Vector3 cullDirection = default)
    {
        HitT = float.MaxValue;
        HitLocation = default;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
        HitWasStatic = false;
        Mobility = mobility;
        _exclusions = exclusions;
        _cullSimulation = cullSimulation;
        _cullDirection = cullDirection;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable)
        => QueryMobilityGate.Allows(Mobility, collidable) && (_exclusions?.Allows(collidable) ?? true);

    // Bepu asks per child of a mesh or compound target. For a mesh the child is the triangle index. Meshes are
    // statics only, and compound children are never culled.
    public bool AllowTest(CollidableReference collidable, int childIndex)
        => AllowTest(collidable) && (_cullSimulation is null || !IsMeshBackFace(collidable, childIndex));

    // True when the mesh triangle's world front normal Cross(C - A, B - A), the convention of Bepu 2.4
    // Triangle.RayTest and CapsuleFeatureMesh, points along the sweep. A normal perpendicular to the sweep is kept.
    private readonly bool IsMeshBackFace(CollidableReference collidable, int childIndex)
    {
        if (collidable.Mobility != CollidableMobility.Static) return false;
        ref Static target = ref _cullSimulation!.Statics.GetDirectReference(collidable.StaticHandle);
        if (target.Shape.Type != default(Mesh).TypeId) return false;
        ref Mesh mesh = ref _cullSimulation.Shapes.GetShape<Mesh>(target.Shape.Index);
        ref Triangle triangle = ref mesh.Triangles[childIndex];
        // Mesh.GetLocalChild scales each vertex, so the scaled winding is the one Bepu collides with.
        Vector3 a = triangle.A * mesh.Scale, b = triangle.B * mesh.Scale, c = triangle.C * mesh.Scale;
        Vector3 front = Vector3.Transform(Vector3.Cross(c - a, b - a), target.Pose.Orientation);
        return Vector3.Dot(front, _cullDirection) > 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
    {
        if (t < HitT)
        {
            HitT = t;
            HitLocation = hitLocation;
            HitNormal = hitNormal;
            HitWasStatic = collidable.Mobility == CollidableMobility.Static;
            HitStatic = HitWasStatic ? collidable.StaticHandle : default;
            // maximumT stays put. A lowered bound shrinks a later mesh or compound child search below the
            // hit it has to beat (see SweepCapsuleCore), so a nearer mesh would be culled.
            DidHit = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
    {
        // t=0 means the sweep started already penetrating; Bepu reports a zero normal here,
        // so the caller cannot determine a push-out direction from this hit alone.
        if (0f < HitT)
        {
            HitT = 0f;
            HitLocation = default;
            HitNormal = default;
            HitWasStatic = collidable.Mobility == CollidableMobility.Static;
            HitStatic = HitWasStatic ? collidable.StaticHandle : default;
            maximumT = 0f;
            DidHit = true;
        }
    }
}
