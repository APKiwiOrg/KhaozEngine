using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

/// <summary>
/// Marks a server-owned entity as <b>transient</b>: <see cref="CellSim.SnapshotOwned(System.Collections.Generic.IReadOnlySet{long}, SnapshotPurpose)"/>
/// leaves it out of the cell capture entirely, so it is never written and can never be resurrected by a restore. The
/// opt-out for anything the server means to outlive nothing (a world pickup, a timed spawn, a temporary marker, a
/// projectile), which without it gets caught in an interval save and comes back on restart as a husk no subsystem is
/// tracking.
/// </summary>
/// <remarks>
/// <para><b><see cref="Scope"/> says which captures it is left out of (since 17.39.0).</b> A cell is captured for
/// two different reasons, and one mark used to decide both (#668). <see cref="TransientScope.Always"/>, the default
/// and what 17.38.0 shipped, excludes the entity from both the durable save and the evictor's in-memory freeze, so
/// it evaporates on an unload as surely as on a restart. <see cref="TransientScope.DurableOnly"/> excludes it from
/// the save alone, so a restart brings back no husk and an in-process unload plus a route back hands the SAME entity
/// under the same <see cref="KhaozEngine.Replication.NetId"/>. Which one an entity wants is a per-entity question:
/// a pickup whose tracking is dropped on eviction anyway (#374) wants
/// <see cref="TransientScope.Always"/>, and whole-zone agent state that goes dormant while its cell is unloaded
/// wants <see cref="TransientScope.DurableOnly"/>. <c>default(Transient)</c> is
/// <see cref="TransientScope.Always"/> on purpose, so every mark written before the scope existed still means what
/// it meant.
/// </para>
///
/// <para><b>It excludes the ENTITY, not a component's bytes.</b> That is the whole reason it exists rather than a
/// <see cref="KhaozEngine.Replication.ReplicationChannels"/> flag. A channel gates one component type on one
/// channel, which for a transient entity is the wrong axis twice over: it is per TYPE rather than per ENTITY, and
/// dropping a component's bytes would still persist the entity, just as a stripped husk. Excluding it here means
/// the blob has never heard of it.
/// </para>
///
/// <para><b>Unregistered on purpose, and one enum field wide.</b> It is deliberately absent from every
/// <see cref="KhaozEngine.Replication.ReplicationRegistry"/>: persistence is a server-local decision no client needs
/// to hear, so the marker spends no replication type id, adds no bytes to any snapshot, and changes no blob layout.
/// Nothing on the wire moves when an entity is marked. It was a zero-field ECS tag until the scope landed, costing
/// an archetype bit and no column at all, and now carries the one <see cref="TransientScope"/> field, so a marked
/// archetype holds a column of it. That is the price of the split, paid only by archetypes that carry the mark.
/// </para>
///
/// <para><b>It follows the entity, scope and all, across a cell handoff within one host.</b> Unlike
/// <see cref="Ghost"/> and <see cref="Migrating"/>, which are re-derived per cell by definition, a transient entity
/// that walks over a cell border must stay transient at the SAME scope or the destination cell would save it, or
/// drop it on the next unload. <see cref="ShardHost.ProcessHandoffs"/> carries the mark across the crossing itself,
/// which is why it survives one without any migrate-channel registration. That holds for every
/// <see cref="ICellLink"/> shape: the mark is read on the source when the Migrate is sent and re-applied on the
/// destination when it is adopted, whether the link completes the crossing inside one call
/// (<see cref="InProcessCellLink"/>) or delivers the Migrate on a later one (what the link's network-impl contract
/// describes).
/// </para>
///
/// <para><b>A genuinely cross-NODE handoff is NOT covered</b>, and that is the deliberate cost of spending no wire
/// id. Two <see cref="ShardHost"/> instances in two processes carry a crossing as bytes, and the mark is in no
/// <see cref="KhaozEngine.Replication.ReplicationRegistry"/>, so nothing in the Migrate payload names it and the
/// receiving host's scratch set has never heard of the entity. An infra link that spans nodes must carry the mark
/// AND its scope in its OWN envelope beside the payload and re-apply both on arrival (set the component on the
/// adopted entity), exactly as <see cref="ShardHost.ProcessHandoffs"/> does within one host.
/// </para>
///
/// <para><b>What it cannot fix is a blob that was already written.</b> A save taken before an entity was marked
/// still holds it, and restoring those older bytes still resurrects it. Clearing THOSE is a one-time boot sweep over
/// the restored cells (the sweep documented on <c>KhaozEngine.NetWorld.WorldPickups</c>), needed once for worlds
/// saved by an older build and never again for a save this one wrote.
/// </para>
/// </remarks>
public struct Transient : IComponent
{
    /// <summary>
    /// Which captures this entity is left out of. Defaults to <see cref="TransientScope.Always"/>, so
    /// <c>default(Transient)</c> is the 17.38.0 behaviour: excluded from the durable save and from the evictor's
    /// freeze alike.
    /// </summary>
    public TransientScope Scope;
}
