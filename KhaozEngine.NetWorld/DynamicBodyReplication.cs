using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Server-side glue that samples authoritative dynamic rigid bodies out of an <see cref="IPhysicsWorld"/> into the
/// replication components clients interpolate. It keys each body's <see cref="DynamicBodyHandle"/> by the
/// <see cref="Replication.NetId"/> of a server-owned entity (spawn one with <c>WorldServer.SpawnEntity</c> /
/// <c>ShardedWorldServer.SpawnEntity</c>, then <see cref="Track"/> it against the body's handle), and each tick,
/// AFTER <see cref="IPhysicsWorld.Step"/>, writes the body's <see cref="Pose"/> into a <see cref="ReplicatedPosition"/>
/// (position, which drives area-of-interest) plus a <see cref="DynamicBodyState"/> (orientation + velocity). Drive
/// <see cref="Sample"/> from the server's <c>OnBeforeTick</c> (having stepped the physics world yourself first), so the
/// fresh pose reaches the SAME tick's snapshot.
/// </summary>
/// <remarks>
/// <para><b>Sleep gating.</b> A body Bepu has put to sleep (<see cref="IPhysicsWorld.IsAwake"/> false) is not
/// re-sampled: its <see cref="ReplicatedPosition"/> / <see cref="DynamicBodyState"/> keep their last written values, so
/// a resting crate stops generating snapshot churn exactly as a still remote player need not stream. The pose written
/// on the LAST awake tick is the resting pose, so the client's final interpolation converges to the true rest pose and
/// then holds it (no further samples arrive, the fixed-delay buffer clamps at the newest). If a sleeping body is later
/// woken (a collision, <see cref="IPhysicsWorld.SetDynamicVelocity"/>), <see cref="Sample"/> resumes writing it. A body
/// that is asleep on the very first <see cref="Sample"/> is written once regardless, so it is never invisible.</para>
/// <para>This type owns no netId allocation and no physics lifetime: the caller spawns the entity and the body, calls
/// <see cref="Track"/> to pair them, and <see cref="Untrack"/> (or <c>Despawn</c> + <see cref="IPhysicsWorld.RemoveDynamic"/>)
/// when the body is gone. Removing the entity server-side propagates to clients as a normal AoI despawn.</para>
/// </remarks>
public sealed class DynamicBodyReplication
{
    private readonly World world;
    private readonly IPhysicsWorld physics;

    // netId -> (physics handle, the ECS entity carrying the replicated components, whether we've written it once).
    private readonly Dictionary<long, Tracked> tracked = new();

    private struct Tracked
    {
        public DynamicBodyHandle Handle;
        public Entity Entity;
        public bool WrittenOnce;
    }

    /// <summary>Binds the sampler to the authoritative <paramref name="world"/> (the server's ECS world) and the
    /// <paramref name="physics"/> world the caller steps. Neither may be null.</summary>
    public DynamicBodyReplication(World world, IPhysicsWorld physics)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.physics = physics ?? throw new ArgumentNullException(nameof(physics));
    }

    /// <summary>Number of currently tracked bodies.</summary>
    public int Count => tracked.Count;

    /// <summary>True if a body is tracked for <paramref name="netId"/>.</summary>
    public bool IsTracked(long netId) => tracked.ContainsKey(netId);

    /// <summary>
    /// Pairs a physics <paramref name="handle"/> with the server-owned entity <paramref name="entity"/> replicated
    /// under <paramref name="netId"/>, so <see cref="Sample"/> writes that body's pose onto that entity every tick.
    /// The entity must be the one carrying the replicated <see cref="Replication.NetId"/> (the one
    /// <c>SpawnEntity</c> returned). Re-tracking the same netId replaces its handle.
    /// </summary>
    public void Track(long netId, DynamicBodyHandle handle, Entity entity) =>
        tracked[netId] = new Tracked { Handle = handle, Entity = entity, WrittenOnce = false };

    /// <summary>Stops sampling the body for <paramref name="netId"/> (does NOT despawn the entity or remove the
    /// physics body - the caller owns those lifetimes). No-op for an untracked id. Returns the body handle so the
    /// caller can <see cref="IPhysicsWorld.RemoveDynamic"/> it.</summary>
    public bool Untrack(long netId, out DynamicBodyHandle handle)
    {
        if (tracked.TryGetValue(netId, out Tracked t))
        {
            handle = t.Handle;
            tracked.Remove(netId);
            return true;
        }
        handle = default;
        return false;
    }

    /// <summary>
    /// Samples every tracked body out of the physics world and writes its pose into the replication components,
    /// gated on <see cref="IPhysicsWorld.IsAwake"/> (a sleeping body keeps its last written pose - see the class
    /// remarks). Call once per server tick AFTER you have stepped the physics world, from the server's
    /// <c>OnBeforeTick</c>, so the fresh pose lands in the same tick's snapshot pass.
    /// </summary>
    public void Sample()
    {
        // The island frame this world is in, read once per Sample rather than once per body. A pose comes back in the
        // PHYSICS WORLD'S space, and the island's physics world sits at the island frame's anchor, so a pose IS the
        // frame-local position and stamping it as such is both correct and precision-preserving: writing it as an
        // absolute would re-quantize it to the float32 lattice of its own world magnitude, which at 100 km is 7.8 mm,
        // undoing exactly what the frame bought. The residual term is zero in the normal case and covers the one
        // configuration where the two disagree - a consumer driving Rebase itself on an unframed head.
        WorldFrame frame = world.GetIslandFrame();
        Vector3 residual = physics.Origin - frame.Anchor;
        List<long>? firstWrites = null;   // keys to flip WrittenOnce for, applied after enumeration
        foreach (KeyValuePair<long, Tracked> kv in tracked)
        {
            Tracked t = kv.Value;
            if (!world.IsAlive(t.Entity)) continue;

            // Sleep gate: skip an asleep body once it has been written at least once (its last pose is its rest pose).
            // A never-yet-written body is always written, even if it spawned asleep, so it is never missing from the
            // first snapshot in range.
            if (t.WrittenOnce && !physics.IsAwake(t.Handle)) continue;

            Pose pose = physics.GetDynamicPose(t.Handle);
            physics.GetDynamicVelocity(t.Handle, out Vector3 linear, out Vector3 angular);
            // InFrame, never FromWorld: the pose is already expressed in the island's frame. Without this the
            // component would claim the frame-local pose was absolute, and every replicated crate would teleport by
            // the anchor delta the first time the island re-anchored.
            world.Set(t.Entity, ReplicatedPosition.InFrame(frame, pose.Position + residual));
            world.Set(t.Entity, DynamicBodyState.From(pose, linear, angular));

            if (!t.WrittenOnce) (firstWrites ??= new List<long>()).Add(kv.Key);
        }

        if (firstWrites is not null)
            foreach (long netId in firstWrites)
            {
                Tracked t = tracked[netId];
                t.WrittenOnce = true;
                tracked[netId] = t;
            }
    }
}
