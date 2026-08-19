using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

/// <summary>
/// Marks a server-owned entity as <b>transient</b>: <see cref="CellSim.SnapshotOwned"/> leaves it out of the cell
/// blob entirely, so it is never saved and can never be resurrected by a restore. The opt-out for anything the
/// server means to outlive nothing (a world pickup, a timed spawn, a temporary marker, a projectile), which without
/// it gets caught in an interval save and comes back on restart as a husk no subsystem is tracking.
/// </summary>
/// <remarks>
/// <para><b>It excludes the ENTITY, not a component's bytes.</b> That is the whole reason it exists rather than a
/// <see cref="KhaozEngine.Replication.ReplicationChannels"/> flag. A channel gates one component type on one
/// channel, which for a transient entity is the wrong axis twice over: it is per TYPE rather than per ENTITY, and
/// dropping a component's bytes would still persist the entity, just as a stripped husk. Excluding it here means
/// the blob has never heard of it.
/// </para>
///
/// <para><b>A field-less tag, and unregistered on purpose.</b> The ECS stores a component with no fields as a tag,
/// with no column behind it at all, so the mark costs an archetype bit and nothing else. It is also deliberately
/// absent from every <see cref="KhaozEngine.Replication.ReplicationRegistry"/>: persistence is a server-local
/// decision no client needs to hear, so the marker spends no replication type id, adds no bytes to any snapshot,
/// and changes no blob layout. Nothing on the wire moves when an entity is marked.
/// </para>
///
/// <para><b>It follows the entity across a cell handoff within one host.</b> Unlike <see cref="Ghost"/> and
/// <see cref="Migrating"/>, which are re-derived per cell by definition, a transient entity that walks over a cell
/// border must stay transient or the destination cell would happily save it.
/// <see cref="ShardHost.ProcessHandoffs"/> carries the mark across the crossing itself, which is why it survives one
/// without any migrate-channel registration. That holds for every <see cref="ICellLink"/> shape: the mark is read on
/// the source when the Migrate is sent and re-applied on the destination when it is adopted, whether the link
/// completes the crossing inside one call (<see cref="InProcessCellLink"/>) or delivers the Migrate on a later one
/// (what the link's network-impl contract describes).
/// </para>
///
/// <para><b>A genuinely cross-NODE handoff is NOT covered</b>, and that is the deliberate cost of spending no wire
/// id. Two <see cref="ShardHost"/> instances in two processes carry a crossing as bytes, and the mark is in no
/// <see cref="KhaozEngine.Replication.ReplicationRegistry"/>, so nothing in the Migrate payload names it and the
/// receiving host's scratch set has never heard of the entity. An infra link that spans nodes must carry the mark in
/// its OWN envelope beside the payload and re-apply it on arrival (set the tag on the adopted entity), exactly as
/// <see cref="ShardHost.ProcessHandoffs"/> does within one host.
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
}
