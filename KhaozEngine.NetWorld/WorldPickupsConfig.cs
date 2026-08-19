using System;
using System.Numerics;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// One offer of a pickup to one player: the pickup, the player, and the geometry that produced the offer.
/// Handed to <see cref="WorldPickupsConfig.OnCollect"/>, whose return value DECIDES the collect.
/// </summary>
/// <param name="PickupNetId">The pickup entity's net id (what <see cref="WorldPickups.Spawn"/> returned).</param>
/// <param name="PayloadId">The opaque game-defined payload the pickup carries. The engine never interprets it.</param>
/// <param name="OwnerNetId">The pickup's owner tag, or <c>0</c> when unowned. Already checked: an offer is only ever
/// raised for a player this tag admits, so a handler does not repeat the test.</param>
/// <param name="Slot">The collecting player's server slot: what a game grants inventory against and sends a game
/// message to (<c>SendGameMessageTo</c>).</param>
/// <param name="PlayerNetId">The collecting player's net id.</param>
/// <param name="PickupPosition">Where the pickup is.</param>
/// <param name="PlayerPosition">Where the player was when the offer was raised (the authoritative position).</param>
/// <param name="Distance">Metres between the two positions, always within the pickup's radius.</param>
public readonly record struct PickupCollect(
    long PickupNetId,
    long PayloadId,
    long OwnerNetId,
    int Slot,
    long PlayerNetId,
    Vector3 PickupPosition,
    Vector3 PlayerPosition,
    float Distance);

/// <summary>Why a pickup left the world. Carried on <see cref="PickupRemoval"/>.</summary>
public enum PickupRemovalReason
{
    /// <summary>A player was offered it and <see cref="WorldPickupsConfig.OnCollect"/> accepted.</summary>
    Collected = 0,

    /// <summary>Its time-to-live elapsed before anyone collected it.</summary>
    Expired = 1,

    /// <summary>The game removed it explicitly (<see cref="WorldPickups.Despawn"/> /
    /// <see cref="WorldPickups.DespawnAll"/> / <see cref="WorldPickups.ForgetWhere"/>).</summary>
    Despawned = 2,

    /// <summary>The cell holding it was unloaded (<see cref="WorldPickups.ForgetCell"/>, which a
    /// <see cref="WorldPickupsConfig.Evictor"/> subscription calls on <see cref="CellEvictor.CellEvicted"/>). The
    /// world took it, not the game and not a timer: nobody collected it and its time-to-live had not run out.
    /// Distinguished from <see cref="Despawned"/> because a game that returns an uncollected payload to a loot
    /// table, or logs why an orb never landed, needs to tell an unload from a deliberate removal.</summary>
    CellEvicted = 3,
}

/// <summary>
/// One pickup leaving the world, for whatever reason. Handed to <see cref="WorldPickupsConfig.OnRemoved"/> AFTER the
/// entity is gone, so every exit route (collect, expiry, explicit despawn) is observable in one place: a game writes
/// its ledger row, plays a sound, or refreshes a counter without having to mirror the seam's bookkeeping.
/// </summary>
/// <param name="PickupNetId">The pickup entity's net id. Already despawned by the time this is raised.</param>
/// <param name="PayloadId">The opaque game-defined payload it carried.</param>
/// <param name="OwnerNetId">Its owner tag at removal, or <c>0</c> when unowned.</param>
/// <param name="Position">Where it was.</param>
/// <param name="Reason">Why it left.</param>
/// <param name="Slot">The collecting player's slot for <see cref="PickupRemovalReason.Collected"/>, else <c>-1</c>.</param>
/// <param name="PlayerNetId">The collecting player's net id for <see cref="PickupRemovalReason.Collected"/>, else <c>0</c>.</param>
public readonly record struct PickupRemoval(
    long PickupNetId,
    long PayloadId,
    long OwnerNetId,
    Vector3 Position,
    PickupRemovalReason Reason,
    int Slot,
    long PlayerNetId);

/// <summary>
/// Tunables and the game hooks for <see cref="WorldPickups"/>. Every member is optional: the defaults give a seam
/// that spawns and replicates pickups, expires nothing, and grants nothing until a game supplies
/// <see cref="OnCollect"/>.
/// </summary>
public sealed class WorldPickupsConfig
{
    /// <summary>
    /// Raised on the server thread from <see cref="WorldPickups.Update"/> when a player is inside a pickup's radius
    /// and passes its owner tag. <b>Return true to accept the collect</b> (the pickup despawns immediately), false to
    /// decline (it stays standing and can be offered again, see <see cref="RetryDeclinedSeconds"/> and
    /// <see cref="WorldPickups.Reoffer"/>).
    /// <para><b>Null declines everything.</b> The engine has no notion of what a pickup is worth, so it never grants
    /// anything on its own: with no handler a pickup can be walked over forever and will only ever leave the world
    /// through its time-to-live or an explicit despawn. This is the whole ownership seam: killer-only, party loot,
    /// need-before-greed, inventory-full, level-gated and free-after-a-delay are all one predicate here.</para>
    /// <para>Reentrancy is safe: the handler may <see cref="WorldPickups.Spawn"/>, <see cref="WorldPickups.Despawn"/>
    /// (including the pickup being offered) or <see cref="WorldPickups.SetOwner"/>. The scan runs over a snapshot
    /// taken before the pass, so a pickup spawned inside a handler is first considered on the NEXT
    /// <see cref="WorldPickups.Update"/>, and a pickup the handler despawned is not despawned twice.</para>
    /// </summary>
    public Func<PickupCollect, bool>? OnCollect { get; init; }

    /// <summary>Raised on the server thread after a pickup has left the world, for every reason (see
    /// <see cref="PickupRemoval"/>). Observational: the entity is already gone.</summary>
    public Action<PickupRemoval>? OnRemoved { get; init; }

    /// <summary>
    /// The cell evictor to follow, so a pickup stops being tracked when the cell holding its entity is unloaded.
    /// <see cref="WorldPickups"/> subscribes to its <see cref="CellEvictor.CellEvicted"/> at construction and calls
    /// <see cref="WorldPickups.ForgetCell"/> per evicted coordinate.
    /// <para>Null (the default) is correct for a server that never evicts, which includes every
    /// <see cref="WorldServer"/>. A server that DOES evict and leaves this null keeps offering pickups in cells that
    /// no longer exist, so set it, or call <see cref="WorldPickups.TrackEvictions"/> when the evictor is built after
    /// the seam.</para>
    /// </summary>
    public CellEvictor? Evictor { get; init; }

    /// <summary>The collect radius, in metres, for a <see cref="WorldPickups.Spawn"/> that does not pass its own.
    /// Measured as a full 3D distance from the pickup to the player's authoritative position (see the
    /// <see cref="WorldPickups"/> remarks). Default <c>1.5</c>.</summary>
    public float DefaultRadius { get; init; } = 1.5f;

    /// <summary>The time-to-live, in seconds, for a <see cref="WorldPickups.Spawn"/> that does not pass its own.
    /// <c>0</c> (the default) means pickups do not expire unless a spawn asks for it.</summary>
    public float DefaultTimeToLiveSeconds { get; init; }

    /// <summary>
    /// How long before a player who stayed inside the radius after a DECLINED collect is offered the same pickup
    /// again, in seconds. <c>0</c> (the default) means never: while the player remains inside they are offered
    /// exactly once, and the next offer waits for them to leave and come back, for
    /// <see cref="WorldPickups.SetOwner"/>, or for <see cref="WorldPickups.Reoffer"/>.
    /// <para>The default is the strict reading of "once per entering the radius" and it is deliberate: a decline is
    /// usually a durable no (not my loot, inventory full), so re-asking every tick would spin a game callback tens of
    /// times a second per player per pickup for an answer that cannot change on its own. Set a positive value only
    /// when a decline can go stale WITHOUT the game noticing. When the game does notice (a loot timer lapsed, a bag
    /// slot freed), call <see cref="WorldPickups.Reoffer"/> or <see cref="WorldPickups.SetOwner"/> instead: those
    /// re-offer on the next <see cref="WorldPickups.Update"/> with no polling at all.</para>
    /// </summary>
    public float RetryDeclinedSeconds { get; init; }
}
