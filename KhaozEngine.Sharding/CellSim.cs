using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Simulation;

namespace KhaozEngine.Sharding;

/// <summary>
/// One authoritative cell of the world grid: its own ECS <see cref="World"/>, a <see cref="FixedTickHost"/>
/// driving a fixed-rate sim, a <see cref="ServerReplicator"/> for snapshotting that world, and an
/// <see cref="InterestGrid"/> for area-of-interest queries. The cell owns (authoritatively simulates) the
/// entities whose position falls inside it. <see cref="Tick"/> advances the fixed-tick accumulator and steps the
/// cell's ECS systems once per whole fixed tick.
/// </summary>
/// <remarks>
/// A cell's world holds its <b>owned</b> entities plus read-only <b>ghosts</b> (<see cref="Ghost"/>) mirrored
/// from neighboring cells via <see cref="ApplyGhostSnapshot"/> (border overlap, Phase 3B). The cell only
/// simulates its owned entities; ghosts are read for cross-border collision / visibility / targeting. Authority
/// handoff (an entity changing owner cell) is a later stage. The <see cref="ServerReplicator"/> and
/// <see cref="InterestGrid"/> are exposed but not auto-driven by <see cref="Tick"/> - snapshot rate is
/// intentionally decoupled from tick rate, so the host/game captures and queries when it chooses.
/// </remarks>
public sealed class CellSim
{
    /// <summary>An empty full-state snapshot (entity count 0): applying it despawns all of a view's ghosts.</summary>
    private static readonly byte[] EmptySnapshot = new byte[4];

    private readonly FixedTickHost tickHost;
    private readonly ReplicationRegistry registry;
    private readonly Dictionary<CellCoord, ClientReplicationView> ghostViews = new();

    // netId -> the entity this cell authoritatively owns (present, alive, not a Ghost, not Migrating). The O(1)
    // ownership index (gap 6 of the MMO arch review) that replaces the linear World.ForEach in TryGetOwned. Kept in
    // sync at the ownership choke points - RegisterOwned/UnregisterOwned, called from the spawn path, AdoptFromMigrate,
    // RestoreOwned, ReleaseMigrating and the ShardHost migrate-freeze. ScanOwned behind it is the miss-fallback + oracle.
    private readonly Dictionary<long, Entity> owned = new();

    // Extension component frames whose id THIS cell's registry does not know, captured at restore keyed by netId, so
    // SnapshotOwned re-emits them verbatim (retain-and-rewrite): a registry downgrade (a build missing a registration,
    // a rollback) no longer strips those components off the persisted blob. Normally empty; populated only under a
    // downgrade. Pruned when an entity leaves this cell's ownership (UnregisterOwned). NOTE: a retained frame does not
    // follow a cell handoff - there is no live component to migrate - so retention protects the restart load/save
    // cycle (the stated goal), not migration during a regression.
    private readonly Dictionary<long, List<RetainedComponent>> retainedUnknown = new();

    // Wired by the owning ShardHost when it creates this cell, so the host's netId -> cell index tracks every
    // register/unregister without CellSim knowing about the host. Null for a standalone cell (direct construction).
    internal Action<long, Entity>? OwnedRegisteredHook;
    internal Action<long>? OwnedUnregisteredHook;

    /// <param name="coord">This cell's coordinate in the world grid.</param>
    /// <param name="tickSeconds">Fixed timestep, seconds per tick (e.g. <c>1f / 30f</c>). Must be &gt; 0.</param>
    /// <param name="registry">Shared replication registry (the same component codecs across all cells).</param>
    /// <param name="interestCellSize">Cell edge length for this cell's AoI <see cref="InterestGrid"/>. Must be &gt; 0.</param>
    public CellSim(CellCoord coord, float tickSeconds, ReplicationRegistry registry, float interestCellSize)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Coord = coord;
        World = new World();
        tickHost = new FixedTickHost(tickSeconds);
        this.registry = registry;
        Replicator = new ServerReplicator(registry);
        Interest = new InterestGrid(interestCellSize);
    }

    /// <summary>This cell's coordinate in the world grid.</summary>
    public CellCoord Coord { get; }

    /// <summary>The cell's authoritative ECS world (its owned entities).</summary>
    public World World { get; }

    /// <summary>Snapshots <see cref="World"/> for clients/neighbors. Not auto-driven by <see cref="Tick"/>.</summary>
    public ServerReplicator Replicator { get; }

    /// <summary>Area-of-interest spatial index for this cell. Not auto-driven by <see cref="Tick"/>.</summary>
    public InterestGrid Interest { get; }

    /// <summary>Seconds per fixed tick.</summary>
    public float TickSeconds => tickHost.TickSeconds;

    /// <summary>Total fixed ticks this cell has advanced.</summary>
    public long TickCount => tickHost.TickCount;

    /// <summary>
    /// Adds <paramref name="elapsedSeconds"/> to the fixed-tick accumulator and steps the cell's ECS systems
    /// (<see cref="Ecs.World.Update"/> with <see cref="TickSeconds"/>) once per whole fixed tick, at most
    /// <paramref name="maxTicksPerFrame"/> times. Returns the number of ticks produced.
    /// </summary>
    public int Tick(float elapsedSeconds, int maxTicksPerFrame = 8) =>
        tickHost.Advance(elapsedSeconds, _ => World.Update(TickSeconds), maxTicksPerFrame);

    // The serve epoch this cell's Interest grid was last rebuilt at (-1 = never), used by RebuildInterestShared to
    // rebuild a cell's grid at most once per server serve pass. Only the shared path sets it. The unconditional
    // RebuildInterest leaves it alone (its callers always want a fresh rebuild).
    private long interestBuildEpoch = -1;

    /// <summary>Number of times this cell's <see cref="Interest"/> grid was actually rebuilt (Clear + reinsert),
    /// exposed for tests to prove the per-tick sharing rebuilds a served cell once, not once per client.</summary>
    internal long InterestRebuildCount { get; private set; }

    /// <summary>
    /// Rebuilds this cell's <see cref="Interest"/> grid from the current positions of every entity in its world
    /// (owned <b>and</b> ghosts), read via <paramref name="accessor"/>. Call before querying AoI for a client so
    /// the home cell's full neighbourhood (owned + border ghosts) is indexed. Always rebuilds. For the per-serve-pass
    /// shared rebuild the sharded server drives instead, see <see cref="RebuildInterestShared"/>.
    /// </summary>
    public void RebuildInterest(CellPositionAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        DoRebuildInterest(accessor);
    }

    /// <summary>
    /// Rebuilds this cell's <see cref="Interest"/> grid only if it has not already been rebuilt at
    /// <paramref name="serveEpoch"/> - the mechanism that amortizes the grid rebuild to once per cell per server serve
    /// pass instead of once per client. The server passes a fresh, monotonically increasing epoch each tick, so the
    /// first client served from this cell in a tick rebuilds and every later client that tick reuses. Because the
    /// epoch changes every tick, any world mutation applied before the serve pass (movement, handoff, ghost sync, an
    /// admin teleport) is always picked up on that tick's first rebuild.
    /// </summary>
    internal void RebuildInterestShared(CellPositionAccessor accessor, long serveEpoch)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        if (interestBuildEpoch == serveEpoch) return;   // already rebuilt this serve pass: reuse the grid
        DoRebuildInterest(accessor);
        interestBuildEpoch = serveEpoch;
    }

    private void DoRebuildInterest(CellPositionAccessor accessor)
    {
        Interest.Clear();
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (accessor(World, e, out float x, out float y)) Interest.Insert(id.Value, x, y);
        });
        InterestRebuildCount++;
    }

    /// <summary>Source cells this cell currently holds a ghost view for (may include emptied views).</summary>
    public IReadOnlyCollection<CellCoord> GhostSources => ghostViews.Keys;

    /// <summary>Total number of live ghosts mirrored into this cell from all sources.</summary>
    public int GhostCount
    {
        get
        {
            int n = 0;
            foreach (ClientReplicationView view in ghostViews.Values)
                foreach (KeyValuePair<long, Entity> kv in view.Entities)
                    if (World.IsAlive(kv.Value)) n++;
            return n;
        }
    }

    /// <summary>Finds a ghost entity in this cell by its (owner-assigned) <see cref="NetId"/> value.</summary>
    public bool TryGetGhost(long netId, out Entity entity)
    {
        foreach (ClientReplicationView view in ghostViews.Values)
            if (view.TryGetEntity(netId, out entity) && World.IsAlive(entity)) return true;
        entity = default;
        return false;
    }

    /// <summary>
    /// Mirrors a border snapshot from a neighboring <paramref name="source"/> cell into this cell's world as
    /// read-only ghosts: entities new to the snapshot are spawned, present ones updated, and ones that left the
    /// source's border despawned (full-state per source). Every resulting ghost is tagged <see cref="Ghost"/>
    /// with its <paramref name="source"/>. The snapshot is a <see cref="KhaozEngine.Replication"/> snapshot of the
    /// source's border entities.
    /// </summary>
    public void ApplyGhostSnapshot(CellCoord source, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClientReplicationView view = GhostViewFor(source);
        view.Apply(World, snapshot);
        foreach (KeyValuePair<long, Entity> kv in view.Entities)
            if (World.IsAlive(kv.Value)) World.Set(kv.Value, new Ghost { Source = source });
    }

    /// <summary>Despawns every ghost this cell holds from <paramref name="source"/> (the source stopped mirroring).</summary>
    public void ClearGhostsFrom(CellCoord source)
    {
        if (ghostViews.TryGetValue(source, out ClientReplicationView? view))
            view.Apply(World, EmptySnapshot);
    }

    /// <summary>
    /// Despawns every ghost this cell holds from <paramref name="source"/> AND drops the view keyed on it, so the
    /// source stops appearing in <see cref="GhostSources"/>. Use this when the source cell is gone for good (it was
    /// unloaded by <see cref="ShardHost.RemoveCell"/>): unlike <see cref="ClearGhostsFrom"/>, which leaves an
    /// emptied view ready for the source's next sync, nothing will ever refresh this one. Returns false if no view
    /// for that source existed.
    /// </summary>
    public bool RemoveGhostView(CellCoord source)
    {
        if (!ghostViews.TryGetValue(source, out ClientReplicationView? view)) return false;
        view.Apply(World, EmptySnapshot);
        ghostViews.Remove(source);
        return true;
    }

    private ClientReplicationView GhostViewFor(CellCoord source)
    {
        if (!ghostViews.TryGetValue(source, out ClientReplicationView? view))
        {
            view = new ClientReplicationView(registry);
            ghostViews[source] = view;
        }
        return view;
    }

    /// <summary>
    /// Records that this cell authoritatively owns <paramref name="entity"/> under <paramref name="netId"/>, so
    /// <see cref="TryGetOwned"/> (and <see cref="ShardHost.TryGetOwner"/>) resolve it in O(1) instead of scanning.
    /// Call right after assigning a freshly-owned entity its <see cref="NetId"/> (the spawn choke point);
    /// <see cref="AdoptFromMigrate"/> and <see cref="RestoreOwned"/> call it for you, and <see cref="ShardHost.SpawnOwned"/>
    /// wraps the whole spawn+assign+register. Overwrites any prior entry for <paramref name="netId"/>.
    /// </summary>
    public void RegisterOwned(long netId, Entity entity)
    {
        owned[netId] = entity;
        OwnedRegisteredHook?.Invoke(netId, entity);
    }

    /// <summary>
    /// Drops <paramref name="netId"/> from this cell's owned index (the entity was despawned or is no longer owned
    /// here). Call at the despawn / migrate-freeze choke points. Returns false if the netId was not indexed. A stale
    /// entry is also reaped lazily by <see cref="TryGetOwned"/>, so an unreported despawn is still correct - just not
    /// reflected in the index until the next lookup.
    /// </summary>
    public bool UnregisterOwned(long netId)
    {
        retainedUnknown.Remove(netId);   // the entity is leaving this cell: drop its retained unknown frames too
        if (!owned.Remove(netId)) return false;
        OwnedUnregisteredHook?.Invoke(netId);
        return true;
    }

    /// <summary>The maintained owned index (netId -&gt; owned entity), exposed for tests to check against a scan.</summary>
    internal IReadOnlyDictionary<long, Entity> OwnedIndexEntries => owned;

    /// <summary>
    /// How many entities this cell authoritatively owns, read off the owned index (ghosts excluded). A cheap
    /// signal for an <see cref="ICellEvictionPolicy"/>, not an oracle: an entity despawned out of band without an
    /// <see cref="UnregisterOwned"/> is still counted until the next lookup reaps it.
    /// </summary>
    public int OwnedCount => owned.Count;

    /// <summary>
    /// Whether any entity here is frozen mid-handoff (<see cref="Migrating"/>), meaning the migrate/ack handshake
    /// is still open. The gate <see cref="ShardHost.RemoveCell"/> checks, since unloading such a cell would strand
    /// the crossing entity between two owners.
    /// </summary>
    public bool HasMigratingEntities
    {
        get
        {
            bool any = false;
            World.ForEach<Migrating>((Entity _, ref Migrating _) => any = true);
            return any;
        }
    }

    /// <summary>
    /// Releases this cell's own state after it has been detached from its host by
    /// <see cref="ShardHost.RemoveCell"/>: the ghost views, the owned index and the retained unknown-extension
    /// frames. The cell's <see cref="World"/>, <see cref="Replicator"/> and <see cref="Interest"/> hold nothing
    /// unmanaged and nothing disposable, so dropping the last reference is the whole release. This just breaks the
    /// internal references eagerly rather than waiting for the graph to become garbage. Never call it on a live
    /// cell.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT touch the cell world's ECS systems. A system is routinely a single instance shared
    /// across every cell (the sharded server adds one <c>PlayerMovementSystem</c> to all of them), so disposing one
    /// on unload would break every other cell.
    /// </remarks>
    internal void Retire()
    {
        ghostViews.Clear();
        owned.Clear();
        retainedUnknown.Clear();
    }

    /// <summary>
    /// Finds an entity this cell <b>owns</b> by its <see cref="NetId"/> value: present, alive, and neither a
    /// <see cref="Ghost"/> nor <see cref="Migrating"/> (i.e. authoritatively simulated here). O(1) off the owned
    /// index (<see cref="RegisterOwned"/>); a stale index entry (an out-of-band despawn, or the entity became a
    /// ghost / started migrating) is reaped and treated as absent. On an index miss it falls back once to a
    /// <see cref="ScanOwned"/> scan behind the index and caches the hit, so the pre-index raw spawn idiom
    /// (<c>SpawnAt</c> + <c>World.Set(new NetId(..))</c> without <see cref="RegisterOwned"/>) still resolves.
    /// </summary>
    public bool TryGetOwned(long netId, out Entity entity)
    {
        if (owned.TryGetValue(netId, out entity))
        {
            if (World.IsAlive(entity) && !World.Has<Ghost>(entity) && !World.Has<Migrating>(entity)) return true;
            UnregisterOwned(netId); // stale index entry: reap it, then fall through (a re-owned copy may still exist)
        }
        if (ScanOwned(netId, out entity)) { RegisterOwned(netId, entity); return true; }
        entity = default;
        return false;
    }

    /// <summary>
    /// The linear ground-truth scan for an owned entity (the pre-index <c>World.ForEach</c>), kept behind the index
    /// as <see cref="TryGetOwned"/>'s miss-fallback and as the independent oracle for <see cref="ShardHost.OwnerCount"/>
    /// and tests - never the per-lookup fast path. Returns the last matching non-ghost, non-migrating entity.
    /// </summary>
    internal bool ScanOwned(long netId, out Entity entity)
    {
        Entity found = default;
        bool ok = false;
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (id.Value == netId && !World.Has<Ghost>(e) && !World.Has<Migrating>(e)) { found = e; ok = true; }
        });
        entity = found;
        return ok;
    }

    /// <summary>
    /// A durable Replication snapshot of this cell's <b>persistable</b> entities: those it owns (present, not a
    /// <see cref="Ghost"/>, not <see cref="Migrating"/>) whose <see cref="NetId"/> is not in
    /// <paramref name="excludedNetIds"/> (the caller passes the player NetIds, which persist separately). Reuses the
    /// same <see cref="SnapshotWriter"/> codec cells use for ghosting/migrate, but captures the
    /// <see cref="ReplicationChannels.Persist"/> channel, so a component is written to the blob only if it declared
    /// <see cref="ReplicationChannels.Persist"/> (a Replicate-only or Migrate-only component is not persisted).
    /// </summary>
    public byte[] SnapshotOwned(IReadOnlySet<long> excludedNetIds)
    {
        ArgumentNullException.ThrowIfNull(excludedNetIds);
        var ids = new HashSet<long>();
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (World.Has<Ghost>(e) || World.Has<Migrating>(e)) return;
            if (excludedNetIds.Contains(id.Value)) return;
            ids.Add(id.Value);
        });
        // Re-emit any retained unknown-extension frames (retain-and-rewrite) so a registry downgrade cannot strip
        // them off the blob. The provider is only wired when something is actually retained (normally nothing).
        Func<long, IReadOnlyList<RetainedComponent>?>? retainedFrames =
            retainedUnknown.Count == 0 ? null : RetainedFramesFor;
        return SnapshotWriter.WriteFiltered(World, registry, ids, ReplicationChannels.Persist, ownerNetId: null, retainedFrames);
    }

    private IReadOnlyList<RetainedComponent>? RetainedFramesFor(long netId) =>
        retainedUnknown.TryGetValue(netId, out List<RetainedComponent>? list) ? list : null;

    /// <summary>
    /// Restores the entities in <paramref name="snapshot"/> into this cell's world as freshly owned entities
    /// (a throwaway <see cref="ClientReplicationView"/>, exactly like <see cref="AdoptFromMigrate"/>), keeping their
    /// <see cref="NetId"/>s. Returns the restored NetId values (empty if the blob failed to decode). Non-throwing:
    /// delegates to <see cref="TryRestoreOwned"/>, so a corrupt blob rolls back and returns empty rather than
    /// throwing. Intended to run once on cell creation.
    /// </summary>
    public IReadOnlyList<long> RestoreOwned(byte[] snapshot) => TryRestoreOwned(snapshot).NetIds;

    /// <summary>
    /// Restores <paramref name="snapshot"/> into this cell as freshly owned entities (a throwaway
    /// <see cref="ClientReplicationView"/>), keeping their <see cref="NetId"/>s, and returns a
    /// <see cref="CellRestoreResult"/> reporting decode success. NON-THROWING and transactional: a blob that fails to
    /// decode (a corrupt frame, an unknown built-in id) is rolled back (every entity the partial apply spawned is
    /// despawned, so the cell is left empty) and reported as <see cref="CellRestoreResult.Ok"/> = false, so the
    /// persistence driver can quarantine the poisoned bytes instead of crash-looping. Extension frames whose id this
    /// cell's registry does not know are RETAINED per-netId and re-emitted verbatim by <see cref="SnapshotOwned"/>
    /// (retain-and-rewrite), so a registry downgrade cannot strip data at rest;
    /// <see cref="CellRestoreResult.RetainedFrameCount"/> reports how many. Intended to run once on cell creation.
    /// </summary>
    public CellRestoreResult TryRestoreOwned(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var view = new ClientReplicationView(registry);
        if (!view.TryApplyRetainingUnknown(World, snapshot, out IReadOnlyList<RetainedComponent> retained, out string? error))
        {
            // Roll back the partial apply so the cell starts genuinely fresh (the caller quarantines the bytes).
            foreach (KeyValuePair<long, Entity> kv in view.Entities)
                if (World.IsAlive(kv.Value)) World.Despawn(kv.Value);
            return CellRestoreResult.Failed(error ?? "cell snapshot failed to decode");
        }
        var netIds = new List<long>(view.Entities.Count);
        foreach (KeyValuePair<long, Entity> kv in view.Entities)
        {
            netIds.Add(kv.Key);
            RegisterOwned(kv.Key, kv.Value); // restored entities are owned here -> index them
        }
        foreach (RetainedComponent rc in retained)
        {
            if (!retainedUnknown.TryGetValue(rc.NetId, out List<RetainedComponent>? list))
                retainedUnknown[rc.NetId] = list = new List<RetainedComponent>();
            list.Add(rc);
        }
        return new CellRestoreResult(true, netIds, retained.Count, null);
    }

    /// <summary>The largest owned (non-ghost, non-migrating) <see cref="NetId"/> in this cell, or 0 if none.</summary>
    public long MaxOwnedNetId()
    {
        long max = 0;
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (World.Has<Ghost>(e) || World.Has<Migrating>(e)) return;
            if (id.Value > max) max = id.Value;
        });
        return max;
    }

    /// <summary>
    /// Adopts a migrated entity into this cell as a freshly owned entity from <paramref name="snapshot"/> (a
    /// single-entity <see cref="KhaozEngine.Replication"/> capture). Any existing ghost of the same NetId here is
    /// despawned so the owned copy is the only one. Returns the adopted NetId values.
    /// </summary>
    public IReadOnlyList<long> AdoptFromMigrate(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Throwaway view: spawns the entity (and sets NetId + components), then is discarded so the entity is
        // untracked = a normal owned entity.
        var adopter = new ClientReplicationView(registry);
        adopter.Apply(World, snapshot);
        var netIds = new List<long>(adopter.Entities.Count);
        foreach (KeyValuePair<long, Entity> kv in adopter.Entities)
        {
            netIds.Add(kv.Key);
            RegisterOwned(kv.Key, kv.Value); // the adopted entity is now owned here -> index it
        }
        foreach (long netId in netIds) DespawnGhost(netId); // drop any pre-existing ghost of the now-owned entity
        return netIds;
    }

    /// <summary>Releases (despawns) the <see cref="Migrating"/> entity with <paramref name="netId"/> after its destination acked.</summary>
    public bool ReleaseMigrating(long netId)
    {
        Entity found = default;
        bool ok = false;
        World.ForEach<NetId, Migrating>((Entity e, ref NetId id, ref Migrating _) =>
        {
            if (id.Value == netId) { found = e; ok = true; }
        });
        if (ok && World.IsAlive(found)) World.Despawn(found);
        if (ok) UnregisterOwned(netId); // defensive: the migrate-freeze already unregistered it, so normally a no-op
        return ok;
    }

    private void DespawnGhost(long netId)
    {
        foreach (ClientReplicationView view in ghostViews.Values)
            if (view.TryGetEntity(netId, out Entity g))
            {
                if (World.IsAlive(g)) World.Despawn(g); // view's stale entry self-heals on the next ghost sync
                return;
            }
    }
}
