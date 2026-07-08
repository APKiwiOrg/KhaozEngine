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
/// <see cref="Mobility"/> so a query can opt out of statics or dynamics.</summary>
internal struct RayHitHandler : IRayHitHandler
{
    public float HitT;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;
    public bool HitWasStatic; // the recorded nearest hit was a static (HitStatic is meaningful); false = dynamic
    public KhaozEngine.Physics.QueryMobility Mobility;

    public RayHitHandler(KhaozEngine.Physics.QueryMobility mobility)
    {
        HitT = float.MaxValue;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
        HitWasStatic = false;
        Mobility = mobility;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => QueryMobilityGate.Allows(Mobility, collidable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => QueryMobilityGate.Allows(Mobility, collidable);

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
/// <see cref="Mobility"/> so a query can opt out of statics or dynamics.</summary>
internal struct SweepHitHandler : ISweepHitHandler
{
    public float HitT;
    public Vector3 HitLocation;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;
    public bool HitWasStatic; // the recorded nearest hit was a static (HitStatic is meaningful); false = dynamic
    public KhaozEngine.Physics.QueryMobility Mobility;

    public SweepHitHandler(KhaozEngine.Physics.QueryMobility mobility)
    {
        HitT = float.MaxValue;
        HitLocation = default;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
        HitWasStatic = false;
        Mobility = mobility;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable) => QueryMobilityGate.Allows(Mobility, collidable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowTest(CollidableReference collidable, int childIndex) => QueryMobilityGate.Allows(Mobility, collidable);

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
            maximumT = t; // cull further candidates
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
