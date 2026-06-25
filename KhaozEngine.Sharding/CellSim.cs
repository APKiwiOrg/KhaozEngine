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

    /// <summary>
    /// Rebuilds this cell's <see cref="Interest"/> grid from the current positions of every entity in its world
    /// (owned <b>and</b> ghosts), read via <paramref name="accessor"/>. Call before querying AoI for a client so
    /// the home cell's full neighbourhood (owned + border ghosts) is indexed.
    /// </summary>
    public void RebuildInterest(CellPositionAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        Interest.Clear();
        World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (accessor(World, e, out float x, out float y)) Interest.Insert(id.Value, x, y);
        });
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
                foreach (KeyValuePair<int, Entity> kv in view.Entities)
                    if (World.IsAlive(kv.Value)) n++;
            return n;
        }
    }

    /// <summary>Finds a ghost entity in this cell by its (owner-assigned) <see cref="NetId"/> value.</summary>
    public bool TryGetGhost(int netId, out Entity entity)
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
        foreach (KeyValuePair<int, Entity> kv in view.Entities)
            if (World.IsAlive(kv.Value)) World.Set(kv.Value, new Ghost { Source = source });
    }

    /// <summary>Despawns every ghost this cell holds from <paramref name="source"/> (the source stopped mirroring).</summary>
    public void ClearGhostsFrom(CellCoord source)
    {
        if (ghostViews.TryGetValue(source, out ClientReplicationView? view))
            view.Apply(World, EmptySnapshot);
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
    /// Finds an entity this cell <b>owns</b> by its <see cref="NetId"/> value: present, alive, and neither a
    /// <see cref="Ghost"/> nor <see cref="Migrating"/> (i.e. authoritatively simulated here).
    /// </summary>
    public bool TryGetOwned(int netId, out Entity entity)
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
    /// Adopts a migrated entity into this cell as a freshly owned entity from <paramref name="snapshot"/> (a
    /// single-entity <see cref="KhaozEngine.Replication"/> capture). Any existing ghost of the same NetId here is
    /// despawned so the owned copy is the only one. Returns the adopted NetId values.
    /// </summary>
    public IReadOnlyList<int> AdoptFromMigrate(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Throwaway view: spawns the entity (and sets NetId + components), then is discarded so the entity is
        // untracked = a normal owned entity.
        var adopter = new ClientReplicationView(registry);
        adopter.Apply(World, snapshot);
        var netIds = new List<int>(adopter.Entities.Count);
        foreach (KeyValuePair<int, Entity> kv in adopter.Entities) netIds.Add(kv.Key);
        foreach (int netId in netIds) DespawnGhost(netId); // drop any pre-existing ghost of the now-owned entity
        return netIds;
    }

    /// <summary>Releases (despawns) the <see cref="Migrating"/> entity with <paramref name="netId"/> after its destination acked.</summary>
    public bool ReleaseMigrating(int netId)
    {
        Entity found = default;
        bool ok = false;
        World.ForEach<NetId, Migrating>((Entity e, ref NetId id, ref Migrating _) =>
        {
            if (id.Value == netId) { found = e; ok = true; }
        });
        if (ok && World.IsAlive(found)) World.Despawn(found);
        return ok;
    }

    private void DespawnGhost(int netId)
    {
        foreach (ClientReplicationView view in ghostViews.Values)
            if (view.TryGetEntity(netId, out Entity g))
            {
                if (World.IsAlive(g)) World.Despawn(g); // view's stale entry self-heals on the next ghost sync
                return;
            }
    }
}
