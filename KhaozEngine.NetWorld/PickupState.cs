using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The replicated state of a world pickup (a dropped item, a resource node, a health pack, a capture point): an
/// opaque game-defined <see cref="PayloadId"/> plus an optional <see cref="OwnerNetId"/>. Built-in replicated
/// component, type id <see cref="MoveProtocol.PickupTypeId"/> (5), riding alongside the entity's
/// <see cref="ReplicatedPosition"/> exactly as <see cref="DynamicBodyState"/> does for a physics prop. Read it
/// client-side with <see cref="WorldClient.TryGetComponent{T}"/> to pick the model, tint, or label to draw.
/// <para><b>The engine never interprets <see cref="PayloadId"/>.</b> There is no item, inventory, rarity or loot
/// concept in the engine and this component does not introduce one: the value is carried to clients verbatim and
/// handed back to the game on collect, in the same spirit as <see cref="ShardedWorldServer.Teleport"/> moving a
/// player without owning any notion of why. Pack whatever a game needs into the 64 bits (an item index, an item
/// index plus a quantity, a row id into the game's own table).</para>
/// <para>Spawned and driven by <see cref="WorldPickups"/>, which owns the proximity test, the time-to-live, and the
/// collect callback. Nothing stops a game from setting this component on an entity of its own, but such an entity
/// is invisible to the seam (no offers, no expiry).</para>
/// </summary>
public struct PickupState : IComponent
{
    /// <summary>The opaque game-defined payload. Never interpreted by the engine (see the type remarks).</summary>
    public long PayloadId;

    /// <summary>
    /// The net id of the only player allowed to collect this pickup, or <c>0</c> for unowned (anyone may collect).
    /// <c>0</c> is a safe sentinel because <see cref="KhaozEngine.Replication.NetIdAllocator"/> counters start at 1,
    /// so no live entity ever carries id 0.
    /// <para>The engine enforces this tag as a hard pre-filter: a non-owner is never even offered the pickup, so the
    /// game's collect callback is not asked about players who could not have it anyway. The engine owns the TAG and
    /// not the RULE, so a game expresses killer-only / party / free-for-all / free-after-a-delay by choosing the
    /// value at spawn and changing it later with <see cref="WorldPickups.SetOwner"/>.</para>
    /// </summary>
    public long OwnerNetId;

    /// <summary>True when this pickup is reserved for one player (<see cref="OwnerNetId"/> is not 0).</summary>
    public readonly bool IsOwned => OwnerNetId != 0;

    /// <summary>True when <paramref name="playerNetId"/> passes the owner tag: the pickup is unowned, or it is that
    /// player's. This is the exact gate <see cref="WorldPickups.Update"/> applies before offering a collect.</summary>
    public readonly bool AllowsCollectBy(long playerNetId) => OwnerNetId == 0 || OwnerNetId == playerNetId;
}
