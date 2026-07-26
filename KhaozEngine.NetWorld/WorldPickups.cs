using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Server-side glue that turns a server-owned entity into a <b>walk-over collectible</b>: it spawns the entity with a
/// replicated <see cref="PickupState"/>, ages it against an optional time-to-live, tests every joined player against
/// every live pickup's radius each tick, and offers a collect to the game, which DECIDES. Drive
/// <see cref="Update"/> from the server's <c>OnBeforeTick</c>, so a despawn on collect reaches the same tick's
/// snapshot. Works identically over <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/> through
/// <see cref="IWorldPickupHost"/>.
/// </summary>
/// <remarks>
/// <para><b>What the engine owns, and what it does not.</b> The engine owns the entity, its replication, the owner
/// tag, the time-to-live, the proximity test and the despawn. It owns NO notion of items, inventories, rarity, loot
/// tables, or what a pickup is worth: <see cref="PickupState.PayloadId"/> is an opaque game-defined value carried
/// verbatim and handed back on collect. It does not own the ownership RULE either, only the tag: every collect is a
/// <see cref="WorldPickupsConfig.OnCollect"/> call that may decline, and a declined pickup stays standing to be
/// offered again. Killer-only, party loot, need-before-greed and free-after-a-delay are all that one predicate plus
/// <see cref="SetOwner"/>.</para>
///
/// <para><b>Offer policy: once per entry, never per tick.</b> A player inside a pickup's radius is offered it exactly
/// once. Standing on a declined pickup does not re-ask on the next tick, so a durable no (not my loot, bag full)
/// costs one callback rather than one per tick per player per pickup. There are three ways to re-offer, and a game
/// picks whichever matches WHY its decline can go stale:
/// <list type="bullet">
/// <item><description><b>Leaving and re-entering the radius</b> re-offers, always. The offer record is dropped the
/// first tick a player is measured outside, so walking away and back is a fresh entry.</description></item>
/// <item><description><b>The game says so.</b> <see cref="Reoffer"/> clears a pickup's offer records, and
/// <see cref="SetOwner"/> does it implicitly (the tag changed, so who may take it changed). This is the right tool
/// when the game KNOWS the decline went stale: a loot timer lapsed, a bag slot freed. It re-offers on the next
/// <see cref="Update"/>, standing still, with no polling.</description></item>
/// <item><description><b>A timer</b>, via <see cref="WorldPickupsConfig.RetryDeclinedSeconds"/>. Off by default. For
/// the case where a decline goes stale without the game noticing.</description></item>
/// </list>
/// A pickup a player is standing on when it SPAWNS (loot dropped at the killer's feet) is an entry like any other, so
/// it is offered on the next <see cref="Update"/>.</para>
///
/// <para><b>Proximity is a linear scan</b> over live pickups against live players, ordered deterministically (pickups
/// by ascending net id, players by ascending slot) so two players inside the same pickup resolve the same way on
/// every run. It is O(pickups x players) per tick, which matches the tens-of-entities-per-cell scale the sharding
/// model assumes and how every other proximity test at this layer is written. Distance is measured in FULL 3D from
/// the pickup's spawn position to the player's authoritative position, so a player on the floor above does not reach
/// through it. A game wanting a cylinder, a cone, a facing test or a line of sight adds it in
/// <see cref="WorldPickupsConfig.OnCollect"/> and declines, which is what the callback is for.</para>
///
/// <para><b>Persistence hazard (not solved here, name it in your boot sequence).</b> <see cref="CellPersistence"/>
/// snapshots every owned non-player entity in a cell on an interval and has NO per-entity opt-out, so a live pickup
/// can be caught in a save and resurrected on restart. A restored pickup is a plain entity carrying
/// <see cref="PickupState"/> that THIS seam knows nothing about: it has no time-to-live and is offered to nobody, so
/// it sits in the world forever. Nor can the component opt out of the persist channel: built-in ids below
/// <see cref="KhaozEngine.Replication.ReplicationRegistry.FirstExtensionTypeId"/> are pinned to
/// <see cref="KhaozEngine.Replication.ReplicationChannels.Default"/> and the registry throws otherwise. A game that
/// persists cells should therefore sweep at boot, which is what
/// <see cref="ShardedWorldServer.DespawnEntity"/> is for:
/// <code>
/// var stale = new List&lt;long&gt;();
/// foreach (CellSim cell in server.Host.Cells)
///     foreach (Entity e in cell.World.Query().With&lt;PickupState&gt;().Entities())
///         if (cell.World.TryGet(e, out NetId id)) stale.Add(id.Value);
/// foreach (long netId in stale) server.DespawnEntity(netId);
/// </code>
/// Sweep before spawning this run's pickups, or the sweep eats them too. <see cref="DespawnAll"/> is the
/// same-process equivalent and clears only what this seam is tracking.</para>
///
/// <para><b>Threading.</b> Single-threaded, like the servers themselves: construct it, and call every member, on the
/// server thread. Both game hooks are raised inline from <see cref="Update"/> on that thread.</para>
/// </remarks>
public sealed class WorldPickups
{
    private readonly IWorldPickupHost host;
    private readonly WorldPickupsConfig config;
    private readonly Dictionary<long, Pickup> live = new();

    // Per-Update scratch, reused so a steady-state tick allocates nothing. Both are sorted so the scan order is
    // deterministic (see the class remarks): a Dictionary's key order is an implementation detail, and which of two
    // co-located players gets the last orb must not be one.
    private readonly List<long> scanOrder = new();
    private readonly List<int> slotOrder = new();
    private readonly List<long> expired = new();

    // One live pickup. A class rather than a struct because the offer record is per-pickup mutable state that the
    // proximity pass writes through. Pickups are spawned in bursts and measured in tens, so the allocation is noise.
    private sealed class Pickup
    {
        public long PayloadId;
        public long OwnerNetId;
        public Vector3 Position;
        public float Radius;
        public float RadiusSquared;
        public float TimeToLive;    // <= 0: never expires
        public float Clock;         // seconds since spawn, advanced by Update's dt (no wall clock, so it is deterministic)

        // playerNetId -> the Clock reading when that player was last offered this pickup. Presence means "already
        // offered while inside", and the value only matters when RetryDeclinedSeconds is on. An entry is dropped the
        // first tick the player is measured outside the radius, which is what makes re-entry a fresh offer.
        public readonly Dictionary<long, float> Offered = new();
    }

    /// <summary>Binds the seam to a server (<see cref="WorldServer"/> or <see cref="ShardedWorldServer"/>, both of
    /// which implement <see cref="IWorldPickupHost"/>) and the game's hooks. A null <paramref name="config"/> takes
    /// the defaults, which decline every collect (see <see cref="WorldPickupsConfig.OnCollect"/>).</summary>
    public WorldPickups(IWorldPickupHost host, WorldPickupsConfig? config = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.config = config ?? new WorldPickupsConfig();
    }

    /// <summary>Number of pickups this seam is currently tracking.</summary>
    public int Count => live.Count;

    /// <summary>True while <paramref name="netId"/> names a pickup this seam is tracking.</summary>
    public bool IsLive(long netId) => live.ContainsKey(netId);

    /// <summary>The net ids of every tracked pickup, ascending. Allocates: for a boot sweep or an admin readout,
    /// not for a per-tick loop.</summary>
    public IReadOnlyList<long> LiveNetIds
    {
        get
        {
            var ids = new List<long>(live.Keys);
            ids.Sort();
            return ids;
        }
    }

    /// <summary>
    /// Spawns a pickup at <paramref name="position"/> carrying the opaque <paramref name="payloadId"/>, and returns
    /// its net id. The entity is server-owned and replicates through the normal area-of-interest pipeline, so any
    /// client in range reads its <see cref="PickupState"/> (and its <see cref="ReplicatedPosition"/>) immediately.
    /// </summary>
    /// <param name="position">Where it sits, in world space. The full <c>Y</c> is honoured, so a floating orb can
    /// hover at hip height. The cell that owns the pickup is chosen from <c>X</c>/<c>Z</c> as for any spawn.</param>
    /// <param name="payloadId">The game-defined payload the engine carries and never interprets. Pack whatever the
    /// game needs into the 64 bits (an item index, an index plus a quantity, a row id).</param>
    /// <param name="ownerNetId">The net id of the only player allowed to collect it, or <c>0</c> (the default) for
    /// unowned. A hard pre-filter: a non-owner is never offered the pickup at all. Change it later with
    /// <see cref="SetOwner"/>.</param>
    /// <param name="radius">Collect radius in metres, or <c>0</c> (the default) to take
    /// <see cref="WorldPickupsConfig.DefaultRadius"/>.</param>
    /// <param name="timeToLiveSeconds">Seconds until it expires on its own, or <c>0</c> (the default) to take
    /// <see cref="WorldPickupsConfig.DefaultTimeToLiveSeconds"/>. When both are 0 it never expires.</param>
    /// <exception cref="ArgumentException"><paramref name="position"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> or
    /// <paramref name="timeToLiveSeconds"/> is negative or not finite.</exception>
    public long Spawn(Vector3 position, long payloadId, long ownerNetId = 0L, float radius = 0f, float timeToLiveSeconds = 0f)
    {
        // Hostile-safe / bug-safe: a NaN slipped in here poisons the replicated position for every client in range,
        // and a NaN radius makes every distance comparison false, so the pickup would silently never be collectable.
        // Reject at the door rather than shipping either downstream.
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentException("Pickup position must be finite.", nameof(position));
        if (!float.IsFinite(radius) || radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Pickup radius must be finite and >= 0.");
        if (!float.IsFinite(timeToLiveSeconds) || timeToLiveSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(timeToLiveSeconds), timeToLiveSeconds, "Pickup time-to-live must be finite and >= 0.");

        float effectiveRadius = radius > 0f ? radius : Math.Max(0f, config.DefaultRadius);
        float effectiveTtl = timeToLiveSeconds > 0f ? timeToLiveSeconds : Math.Max(0f, config.DefaultTimeToLiveSeconds);
        var state = new PickupState { PayloadId = payloadId, OwnerNetId = ownerNetId };

        long netId = host.SpawnEntity(position.X, position.Z, (world, entity) =>
        {
            // SpawnEntity pre-sets ReplicatedPosition to (x, 0, z), so rewrite it with the real Y or the client draws
            // the orb where it actually is rather than on the ground plane.
            world.Set(entity, new ReplicatedPosition { Value = position });
            world.Set(entity, state);
        });

        live[netId] = new Pickup
        {
            PayloadId = payloadId,
            OwnerNetId = ownerNetId,
            Position = position,
            Radius = effectiveRadius,
            RadiusSquared = effectiveRadius * effectiveRadius,
            TimeToLive = effectiveTtl,
        };
        return netId;
    }

    /// <summary>The current state of a tracked pickup: its payload, owner tag, position, collect radius, and how far
    /// it is through its time-to-live. False for an unknown or already-removed net id.</summary>
    public bool TryGet(long netId, out PickupInfo info)
    {
        if (live.TryGetValue(netId, out Pickup? p))
        {
            info = new PickupInfo(netId, p.PayloadId, p.OwnerNetId, p.Position, p.Radius, p.TimeToLive, p.Clock);
            return true;
        }
        info = default;
        return false;
    }

    /// <summary>
    /// Re-tags a live pickup's owner (<c>0</c> = unowned) and re-offers it to everyone standing on it, so the change
    /// takes effect on the next <see cref="Update"/> without anyone having to step off and back on. This is how a
    /// game expresses free-after-a-delay: spawn owned, then hand the pickup to everybody when its own loot timer
    /// lapses. The new tag is written to the replicated <see cref="PickupState"/>, so clients see the change too (a
    /// tint, a highlight). False for an unknown pickup, or when its entity has already gone.
    /// </summary>
    public bool SetOwner(long netId, long ownerNetId)
    {
        if (!live.TryGetValue(netId, out Pickup? p)) return false;
        if (!host.TryGetEntity(netId, out World world, out Entity entity) || !world.IsAlive(entity)) return false;
        p.OwnerNetId = ownerNetId;
        p.Offered.Clear();   // who may take it changed, so every standing offer is stale
        world.Set(entity, new PickupState { PayloadId = p.PayloadId, OwnerNetId = ownerNetId });
        return true;
    }

    /// <summary>
    /// Clears a pickup's offer records, so every player currently inside its radius is offered it again on the next
    /// <see cref="Update"/>. Call it when the game knows a previous decline went stale (a bag slot freed, a
    /// need-before-greed roll finished) rather than reaching for
    /// <see cref="WorldPickupsConfig.RetryDeclinedSeconds"/>: this costs nothing until it happens. False for an
    /// unknown pickup.
    /// </summary>
    public bool Reoffer(long netId)
    {
        if (!live.TryGetValue(netId, out Pickup? p)) return false;
        p.Offered.Clear();
        return true;
    }

    /// <summary>Removes a pickup explicitly: despawns its entity (propagating to clients as a normal
    /// area-of-interest removal) and raises <see cref="WorldPickupsConfig.OnRemoved"/> with
    /// <see cref="PickupRemovalReason.Despawned"/>. False for an unknown pickup. Safe to call from inside
    /// <see cref="WorldPickupsConfig.OnCollect"/>, including on the pickup being offered.</summary>
    public bool Despawn(long netId) => Remove(netId, PickupRemovalReason.Despawned, slot: -1, playerNetId: 0L);

    /// <summary>
    /// Removes every pickup this seam is tracking and returns how many went. Each raises
    /// <see cref="WorldPickupsConfig.OnRemoved"/> with <see cref="PickupRemovalReason.Despawned"/>.
    /// <para>This clears what the seam KNOWS about, which is what this process spawned. It does NOT find a pickup
    /// resurrected out of a cell save (see the persistence hazard in the type remarks) - that needs the world sweep
    /// documented there, because the seam never saw those entities.</para>
    /// </summary>
    public int DespawnAll()
    {
        if (live.Count == 0) return 0;
        var all = new List<long>(live.Keys);
        all.Sort();
        int removed = 0;
        foreach (long netId in all)
            if (Remove(netId, PickupRemovalReason.Despawned, slot: -1, playerNetId: 0L)) removed++;
        return removed;
    }

    /// <summary>
    /// Ages every pickup, expires the ones whose time-to-live elapsed, then offers each live pickup to every joined
    /// player inside its radius (see the offer policy in the type remarks). Call once per server tick from
    /// <c>OnBeforeTick</c>, so a collect's despawn reaches the SAME tick's snapshot instead of showing the orb for
    /// one extra frame. <paramref name="dt"/> is the tick's seconds and is the only clock this seam has, so behaviour
    /// is deterministic and headless-testable (a negative value is treated as 0).
    /// </summary>
    public void Update(float dt)
    {
        if (live.Count == 0) return;
        if (!(dt > 0f)) dt = 0f;   // also catches NaN

        ExpirePass(dt);
        if (live.Count == 0) return;
        ProximityPass();
    }

    // Advance each pickup's clock and remove the ones that have outlived their time-to-live. Collected into a scratch
    // list first: OnRemoved is game code that may spawn or despawn, so the dictionary must not be mid-enumeration.
    private void ExpirePass(float dt)
    {
        expired.Clear();
        foreach (KeyValuePair<long, Pickup> kv in live)
        {
            Pickup p = kv.Value;
            p.Clock += dt;
            if (p.TimeToLive > 0f && p.Clock >= p.TimeToLive) expired.Add(kv.Key);
        }
        if (expired.Count == 0) return;
        expired.Sort();
        foreach (long netId in expired) Remove(netId, PickupRemovalReason.Expired, slot: -1, playerNetId: 0L);
    }

    private void ProximityPass()
    {
        // Snapshot both axes up front, sorted. Deterministic order, and it means a handler is free to spawn or
        // despawn without disturbing the pass in flight (a pickup spawned inside a handler is simply first
        // considered next Update).
        scanOrder.Clear();
        foreach (long netId in live.Keys) scanOrder.Add(netId);
        scanOrder.Sort();

        slotOrder.Clear();
        foreach (int slot in host.JoinedSlots) slotOrder.Add(slot);
        if (slotOrder.Count == 0) return;
        slotOrder.Sort();

        float retry = config.RetryDeclinedSeconds;
        foreach (long netId in scanOrder)
        {
            if (!live.TryGetValue(netId, out Pickup? p)) continue;   // a previous handler took or despawned it
            foreach (int slot in slotOrder)
            {
                if (!live.ContainsKey(netId)) break;                 // this pickup went during this pass
                if (!host.TryGetPlayerNetId(slot, out long playerNetId)) continue;
                // Owner tag: a hard engine-side pre-filter, so the game callback is never asked about a player who
                // could not have it anyway. The game controls the TAG (Spawn / SetOwner), the engine only enforces it.
                if (p.OwnerNetId != 0L && p.OwnerNetId != playerNetId) continue;
                if (!host.TryGetPlayerState(slot, out PlayerMoveState state)) continue;

                float distanceSquared = Vector3.DistanceSquared(state.Position, p.Position);
                if (!(distanceSquared <= p.RadiusSquared))
                {
                    // Outside (or a NaN position, which lands here rather than being read as a hit). Drop the offer
                    // record so walking back in is a fresh entry.
                    p.Offered.Remove(playerNetId);
                    continue;
                }

                if (p.Offered.TryGetValue(playerNetId, out float offeredAt)
                    && (retry <= 0f || p.Clock - offeredAt < retry))
                    continue;   // already offered while inside, and not due for a timed retry

                // Record the offer BEFORE raising it: a handler that despawns and respawns, or that throws, must not
                // leave this pickup able to re-offer the same player on the very next tick.
                p.Offered[playerNetId] = p.Clock;

                var request = new PickupCollect(netId, p.PayloadId, p.OwnerNetId, slot, playerNetId,
                    p.Position, state.Position, MathF.Sqrt(distanceSquared));
                bool accepted = config.OnCollect?.Invoke(request) ?? false;
                if (!accepted) continue;

                // The handler is allowed to have despawned it (or to have despawned it and spawned a replacement
                // under a new id), so removal is conditional on it still being the pickup we offered.
                if (live.ContainsKey(netId)) Remove(netId, PickupRemovalReason.Collected, slot, playerNetId);
                break;
            }
        }
    }

    // The single exit door: drop the bookkeeping, despawn the entity, then tell the game. Ordered so a handler that
    // re-enters (spawning a replacement, counting what is left) always sees a consistent seam.
    private bool Remove(long netId, PickupRemovalReason reason, int slot, long playerNetId)
    {
        if (!live.TryGetValue(netId, out Pickup? p)) return false;
        live.Remove(netId);
        host.DespawnEntity(netId);
        config.OnRemoved?.Invoke(new PickupRemoval(netId, p.PayloadId, p.OwnerNetId, p.Position, reason, slot, playerNetId));
        return true;
    }
}

/// <summary>A read-only view of one tracked pickup, returned by <see cref="WorldPickups.TryGet"/>.</summary>
/// <param name="NetId">The pickup entity's net id.</param>
/// <param name="PayloadId">The opaque game-defined payload. Never interpreted by the engine.</param>
/// <param name="OwnerNetId">The owner tag, or <c>0</c> when unowned.</param>
/// <param name="Position">Where it sits, in world space.</param>
/// <param name="Radius">Its collect radius in metres.</param>
/// <param name="TimeToLiveSeconds">Its time-to-live in seconds, or <c>0</c> when it never expires.</param>
/// <param name="AgeSeconds">Seconds of <c>Update</c> time since it spawned.</param>
public readonly record struct PickupInfo(
    long NetId,
    long PayloadId,
    long OwnerNetId,
    Vector3 Position,
    float Radius,
    float TimeToLiveSeconds,
    float AgeSeconds);
