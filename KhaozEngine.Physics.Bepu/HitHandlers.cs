using System;
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

namespace KhaozEngine.Physics.Bepu;

/// <summary>Records the nearest ray hit for <see cref="BepuPhysicsWorld.Raycast"/>.</summary>
internal struct RayHitHandler : IRayHitHandler
{
    public float HitT;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;

    public RayHitHandler()
    {
        HitT = float.MaxValue;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
    }

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
            HitNormal = normal;
            if (collidable.Mobility == CollidableMobility.Static)
                HitStatic = collidable.StaticHandle;
            maximumT = t; // cull further candidates
            DidHit = true;
        }
    }
}

/// <summary>Records the nearest sweep hit for <see cref="BepuPhysicsWorld.SweepCapsule"/>.</summary>
internal struct SweepHitHandler : ISweepHitHandler
{
    public float HitT;
    public Vector3 HitLocation;
    public Vector3 HitNormal;
    public BepuStaticHandle HitStatic;
    public bool DidHit;

    public SweepHitHandler()
    {
        HitT = float.MaxValue;
        HitLocation = default;
        HitNormal = default;
        HitStatic = default;
        DidHit = false;
    }

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
            HitLocation = hitLocation;
            HitNormal = hitNormal;
            if (collidable.Mobility == CollidableMobility.Static)
                HitStatic = collidable.StaticHandle;
            maximumT = t; // cull further candidates
            DidHit = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
    {
        if (0f < HitT)
        {
            HitT = 0f;
            HitLocation = default;
            HitNormal = default;
            if (collidable.Mobility == CollidableMobility.Static)
                HitStatic = collidable.StaticHandle;
            maximumT = 0f;
            DidHit = true;
        }
    }
}

/// <summary>Broad-phase overlap enumerator that collects static collidable references.</summary>
internal struct StaticOverlapCollector : IOverlapHandler
{
    public readonly List<int> BroadPhaseLeafIndices;

    public StaticOverlapCollector(List<int> list) => BroadPhaseLeafIndices = list;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Handle(int indexA, int indexB)
    {
        // BroadPhase.GetOverlaps reports overlaps of the query against the static tree leaves.
        // indexB is the leaf in the static tree.
        BroadPhaseLeafIndices.Add(indexB);
    }
}
