using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The physics world the character controller and netcode resolve against: static bodies, dynamic
/// rigid bodies, stepping, and raycast/sweep/overlap queries. Headless and backend-agnostic. Authoritative
/// on the server and re-run identically in client prediction. Dynamic-body stepping is deterministic under a
/// fixed timestep on a given platform (no wall clock, no unseeded randomness), so two worlds built and stepped
/// identically stay bit-identical.</summary>
public interface IPhysicsWorld : IDisposable
{
    /// <summary>Add a static body. Returns a handle for later removal.</summary>
    StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null);

    /// <summary>Remove a static body previously added.</summary>
    void RemoveStatic(StaticHandle handle);

    /// <summary>Add a dynamic rigid body: it falls under the world's gravity, collides with statics and
    /// other dynamic bodies, and is advanced by <see cref="Step"/>. The <paramref name="shape"/> reuses the
    /// same <see cref="PhysicsShape"/> descriptors as the static path (box/sphere/capsule/cylinder/hull and
    /// their compounds); base-aligned shapes (cylinder/hull) stay base-aligned to <paramref name="pose"/>
    /// exactly as statics do. Returns a handle for pose/velocity queries and removal.</summary>
    DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null);

    /// <summary>Remove a dynamic body previously added. Safe to call at any time, including mid-flight.</summary>
    void RemoveDynamic(DynamicBodyHandle handle);

    /// <summary>The current world pose (position + orientation) of a dynamic body.</summary>
    Pose GetDynamicPose(DynamicBodyHandle handle);

    /// <summary>The current linear (m/s) and angular (rad/s) velocity of a dynamic body, world space.</summary>
    void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular);

    /// <summary>Set the linear (m/s) and angular (rad/s) velocity of a dynamic body, world space. Wakes the
    /// body if it was asleep.</summary>
    void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular);

    /// <summary>Whether a dynamic body is currently awake (being actively simulated). A body that has come to
    /// rest sleeps and reports false until disturbed. Cheap; used to gate replication and rest assertions.</summary>
    bool IsAwake(DynamicBodyHandle handle);

    /// <summary>Add a joint constraint connecting two dynamic bodies, or one dynamic body to a fixed world-space
    /// anchor. The <see cref="ConstraintDescription"/> is a discriminated struct: its <see cref="ConstraintKind"/>
    /// selects the joint (ball-socket, hinge, slider, distance, weld) and only that kind's fields are read.
    /// Anchors and axes are body-local. A world-space anchor end (<see cref="ConstraintAttachment.AtWorld(Pose)"/>)
    /// is pinned by the backend as an infinite-mass kinematic body. Returns a handle for removal. The constraint
    /// is stepped by <see cref="Step"/> from the next step on. Throws <see cref="System.ArgumentException"/> if a
    /// referenced dynamic body is not live, or if both ends are world anchors (a constraint needs at least one
    /// dynamic body to move).</summary>
    ConstraintHandle AddConstraint(in ConstraintDescription description);

    /// <summary>Remove a joint constraint previously added. Safe to call at any time, including mid-step. A
    /// double-remove, or removing a constraint whose body was already removed (which cleans up its constraints),
    /// is a safe no-op.</summary>
    void RemoveConstraint(ConstraintHandle handle);

    /// <summary>Advance the simulation by <paramref name="dt"/> seconds. Integrates dynamic bodies under
    /// gravity, resolves contacts, and solves active constraints. Deterministic under a fixed
    /// <paramref name="dt"/>.</summary>
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
