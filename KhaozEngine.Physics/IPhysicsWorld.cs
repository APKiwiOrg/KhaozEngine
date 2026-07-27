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
    /// is pinned by the backend as an infinite-mass anchor point that is NOT a collidable, so it is never hit by a
    /// raycast or sweep (a character walks through a world-anchored pivot cleanly). Returns a handle for removal. The constraint
    /// is stepped by <see cref="Step"/> from the next step on. Throws <see cref="System.ArgumentException"/> if a
    /// referenced dynamic body is not live, or if both ends are world anchors (a constraint needs at least one
    /// dynamic body to move).</summary>
    ConstraintHandle AddConstraint(in ConstraintDescription description);

    /// <summary>Remove a joint constraint previously added. Safe to call at any time, including mid-step. A
    /// double-remove, or removing a constraint whose body was already removed (which cleans up its constraints),
    /// is a safe no-op.</summary>
    void RemoveConstraint(ConstraintHandle handle);

    /// <summary>Update the live target of a powered joint's motor or servo, allocation-free and cheap enough to
    /// call every frame. The meaning of <paramref name="target"/> follows the constraint's
    /// <see cref="ConstraintDescription.Motor"/>: a target angular/linear velocity for a motor, or a target
    /// angle/offset/length for a servo (a shrinking winch length reels a body up, a growing hinge servo angle
    /// swings a door). Only the servo/motor target changes; the joint's spring, limits and anchors are untouched.
    /// Throws <see cref="System.ArgumentException"/> if the handle is stale (its body was removed, or it was
    /// removed) or if the constraint has no motor (<see cref="ConstraintMotor.None"/>), matching the mutation
    /// throw-on-stale pattern.</summary>
    void SetConstraintTarget(ConstraintHandle handle, float target);

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

    /// <summary>The world-space point this world's coordinates are expressed against. EVERY pose passed to
    /// <see cref="AddStatic"/>/<see cref="AddDynamic"/>, every query coordinate, and every pose read back out of
    /// <see cref="GetDynamicPose"/> is relative to it. <see cref="Vector3.Zero"/> (the default) means the world
    /// speaks absolute world coordinates, which is what every backend does until something rebases it.
    /// <para>This is deliberately a plain <see cref="Vector3"/> rather than a quantized frame type: the physics
    /// seam has no project references and keeps none. The caller quantizes. A caller that speaks absolute converts
    /// at the call site (<c>AddStatic(shape, new Pose(absolute - world.Origin, rot))</c>) - a site that forgets is
    /// a site that never read <c>Origin</c>, which is greppable.</para></summary>
    Vector3 Origin => Vector3.Zero;

    /// <summary>Whether this backend implements <see cref="Rebase"/>. False (the default, including on any consumer
    /// test double) means the world cannot be re-expressed and therefore cannot serve a world large enough to need
    /// it. Check this before calling <see cref="Rebase"/>.</summary>
    bool CanRebase => false;

    /// <summary>Re-express this world against <paramref name="newOrigin"/>: translate EVERY static, every dynamic
    /// body (awake and sleeping alike) and every world-space constraint anchor by <c>Origin - newOrigin</c>, then
    /// set <see cref="Origin"/> to it. Velocities, sleep state, contacts and constraints are all preserved - this is
    /// a change of coordinate space, not a physical event, and nothing inside the world can observe it.
    /// <para>It takes the TARGET origin rather than a delta on purpose. The contents and <see cref="Origin"/> then
    /// move as one atomic operation and can never be left describing different spaces, which a delta-taking API
    /// makes possible with one dropped call.</para>
    /// <para>Must be called BETWEEN steps, never during one. Anything the caller holds in the old space (its own
    /// collider bookkeeping, cached poses, spatial indices) moves by the same delta or it is left behind.</para></summary>
    void Rebase(Vector3 newOrigin) => throw new NotSupportedException(
        "This IPhysicsWorld backend does not support Rebase. Check CanRebase first.");
}
