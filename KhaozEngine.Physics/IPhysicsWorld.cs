using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The physics world the character controller and netcode resolve against: a set of static
/// bodies plus stepping and raycast/sweep/overlap queries. Headless and backend-agnostic. Authoritative
/// on the server and re-run identically in client prediction. Sub-project 1 has static bodies only;
/// dynamic bodies arrive in sub-project 2 behind the same interface.</summary>
public interface IPhysicsWorld : IDisposable
{
    /// <summary>Add a static body. Returns a handle for later removal.</summary>
    StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null);

    /// <summary>Remove a static body previously added.</summary>
    void RemoveStatic(StaticHandle handle);

    /// <summary>Advance the simulation by <paramref name="dt"/> seconds. Near-trivial while there are no
    /// dynamic bodies, but present so sub-project 2 drops in without an interface change.</summary>
    void Step(float dt);

    /// <summary>Cast a ray; returns the nearest hit. Used for ledge detection, jump targeting,
    /// line-of-sight, and downward ground probes.</summary>
    bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default);

    /// <summary>Sweep a capsule along <paramref name="direction"/>; returns the nearest time of impact.</summary>
    bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default);

    /// <summary>If the capsule at <paramref name="pose"/> overlaps any static body, output the minimum
    /// translation (direction * depth) that separates it; returns false when clear.</summary>
    bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv);
}
