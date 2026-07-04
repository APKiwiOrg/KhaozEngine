using System;

namespace KhaozEngine.Replication;

/// <summary>
/// Per-registration flags declaring WHICH of a component's four downstream consumers see its bytes: client
/// area-of-interest replication, cell persistence, cell handoff, and (as a <see cref="Replicate"/> modifier)
/// owner-only visibility. Passed to <see cref="ReplicationRegistry.Register{T}"/>; the default
/// (<see cref="Default"/> = <see cref="Replicate"/> | <see cref="Persist"/> | <see cref="Migrate"/>) is the
/// pre-9.28.0 behaviour where persisted == replicated == migrated, so existing registrations and the engine
/// built-ins are unchanged and the wire stays byte-identical for them.
/// </summary>
/// <remarks>
/// The flags gate the SERVER (write) side only: each consumer serves exactly one channel
/// (<see cref="Replicate"/> for client AoI + border ghosts, <see cref="Persist"/> for cell blobs,
/// <see cref="Migrate"/> for cell handoff) and includes a component only if that channel bit is set.
/// The client read side never filters (it decodes whatever is on the wire), so the flags on a client-built
/// registry are ignored - only the codec registration (to read the bytes) matters there.
/// <para>
/// Two examples the coupling used to make impossible: a mob's aggro/threat table registered
/// <c><see cref="Persist"/> | <see cref="Migrate"/></c> survives handoff and restart but never reaches a client;
/// a player's private inventory / exact HP registered <c><see cref="Default"/> | <see cref="OwnerOnly"/></c>
/// reaches only that player's own client, never another observer in AoI (closing the map-hack surface).
/// </para>
/// <para>
/// Built-in component ids (below <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) MUST keep
/// <see cref="Default"/>: their exact unframed wire encoding is the core protocol, so
/// <see cref="ReplicationRegistry.Register{T}"/> throws if a built-in is registered with any other channel set.
/// </para>
/// </remarks>
[Flags]
public enum ReplicationChannels
{
    /// <summary>In no channel: the component lives only in the ECS and is never serialized by any consumer.</summary>
    None = 0,

    /// <summary>Replicated to clients through area-of-interest serving (<see cref="SnapshotWriter.WriteFiltered"/> /
    /// <see cref="AoiDeltaReplicator.WriteFor"/>) and mirrored into neighbouring cells as border ghosts.</summary>
    Replicate = 1 << 0,

    /// <summary>Written into a cell's durable persistence blob (<c>CellSim.SnapshotOwned</c> in KhaozEngine.Sharding),
    /// so it survives a server restart.</summary>
    Persist = 1 << 1,

    /// <summary>Captured on cell handoff (authority transfer across a boundary), so it follows the entity to the
    /// destination cell.</summary>
    Migrate = 1 << 2,

    /// <summary>A modifier on <see cref="Replicate"/>: the component is replicated ONLY to the client that owns the
    /// entity (the entity's <see cref="NetId"/> equals that client's own net id), never to another observer whose
    /// AoI the entity is in. Has no effect on the <see cref="Persist"/> / <see cref="Migrate"/> channels. Requires
    /// <see cref="Replicate"/> (registering <see cref="OwnerOnly"/> without it throws).</summary>
    OwnerOnly = 1 << 3,

    /// <summary>The pre-9.28.0 default: replicated, persisted, and migrated (persisted == replicated == migrated).
    /// Every engine built-in and every registration that omits the channels argument uses this, so the wire is
    /// byte-identical to before channels existed.</summary>
    Default = Replicate | Persist | Migrate,
}
