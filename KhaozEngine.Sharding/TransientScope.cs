namespace KhaozEngine.Sharding;

/// <summary>
/// How far a <see cref="Transient"/> mark reaches: which of the two cell snapshots a marked entity is left out of.
/// The two answer different questions, and until 17.39.0 one mark decided both (#668).
/// </summary>
/// <remarks>
/// <para>A cell is captured for two reasons. The DURABLE capture is the save: bytes handed to an
/// <c>IWorldStore</c> that a later process reads back, so an entity in it outlives the process that spawned it. The
/// EVICTION capture is the in-memory freeze <c>KhaozEngine.NetWorld.CellEvictor</c> keeps while a cell is unloaded,
/// so a coordinate that is re-entered hands the same entities back inside the create call. An unload is not a
/// persistence decision, and a game routinely wants an entity to survive one and not the other.</para>
/// </remarks>
public enum TransientScope
{
    /// <summary>
    /// Excluded from EVERY cell snapshot, durable and eviction alike: the entity ceases to exist the moment its cell
    /// stops holding it, and nothing can bring it back. The default (<c>default(Transient)</c> is this) and the
    /// behaviour <see cref="Transient"/> shipped with in 17.38.0, so an existing mark is unchanged.
    /// <para>The right scope when the entity's meaning lives in per-process bookkeeping the restore cannot rebuild.
    /// A world pickup is the shipped example: its time-to-live, its clock and its offer records are in
    /// <c>KhaozEngine.NetWorld.WorldPickups</c>, so an orb that came back from ANY snapshot the seam did not write
    /// would be a husk offered to nobody.</para>
    /// </summary>
    Always = 0,

    /// <summary>
    /// Excluded from the DURABLE capture only. The entity is never saved, so a restart brings back no husk, but the
    /// eviction freeze keeps it and an unload plus a route back hands the SAME entity, under the same
    /// <see cref="KhaozEngine.Replication.NetId"/>, to whatever was tracking it.
    /// <para>The right scope for authored, whole-zone agent state: a spawner holding one record per authored
    /// creature keyed to a net id, which goes dormant while its cell is unloaded and expects its entity back on the
    /// restore, yet must be re-spawned from the authored content rather than resurrected out of a blob on a restart.
    /// That is the case #668 was filed for.</para>
    /// <para><b>The route back is bounded by the evictor's cache</b>
    /// (<c>KhaozEngine.NetWorld.CellEvictionConfig.MaxCachedSnapshots</c>, 1024 coordinates by default, oldest
    /// dropped first). A coordinate whose cached freeze has been dropped falls back to the store-backed load, and
    /// the store never held this entity, so past the cache the entity is gone exactly as
    /// <see cref="Always"/> would have left it. Size the cache for the world, and keep the spawner able to notice a
    /// net id it no longer owns.</para>
    /// </summary>
    DurableOnly = 1,
}
