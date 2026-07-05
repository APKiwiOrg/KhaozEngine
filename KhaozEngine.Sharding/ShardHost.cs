using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Simulation;

namespace KhaozEngine.Sharding;

/// <summary>
/// Reads an entity's world position, used for border-overlap detection. Returns false if the entity has no
/// position (it is then skipped for ghosting). Games supply this over their own position component.
/// </summary>
public delegate bool CellPositionAccessor(World world, Entity entity, out float x, out float y);

/// <summary>
/// In-process host of the world's uniform cell grid. Owns the <see cref="CellCoord"/> -&gt; <see cref="CellSim"/>
/// map, creates cells on demand, routes a world position (and the entities spawned there) to the cell that
/// contains it, <see cref="Tick"/>s every live cell at one shared fixed rate, and (Phase 3B)
/// <see cref="SyncGhosts"/> mirrors border-overlap entities into neighboring cells as read-only ghosts over an
/// <see cref="ICellLink"/>. This is the EVE / seamless-MMO topology run as a single process; a multi-process
/// deployment implements the same shape behind the inter-cell seam.
/// </summary>
/// <remarks>
/// An entity is owned by the cell its position falls in. With an overlap margin &gt; 0 and a position accessor,
/// <see cref="SyncGhosts"/> mirrors owned entities near a cell edge into the neighbor(s) on the other side as
/// <see cref="Ghost"/> entities (read-only; the owner stays the sole simulator). Authority handoff (an entity
/// changing owner on a crossing) is a later Phase 3 stage. Deterministic and headless. Cells are retained in
/// creation order (<see cref="Cells"/>) so iteration is stable. Replicated entities are assumed to carry
/// globally-unique <see cref="NetId"/>s across cells. Because cells are disjoint <see cref="World"/>s,
/// <see cref="Tick"/> fans them across an opt-in <see cref="Scheduler"/> (default single-threaded) for
/// near-linear-in-cores throughput; the cross-cell passes stay single-threaded.
/// </remarks>
public sealed class ShardHost
{
    private readonly float tickSeconds;
    private readonly ReplicationRegistry registry;
    private readonly float interestCellSize;
    private readonly CellPositionAccessor? positionAccessor;
    private readonly ICellLink link;
    private readonly Dictionary<CellCoord, CellSim> cells = new();
    private readonly List<CellSim> ordered = new();
    // netId -> the coord of the cell that authoritatively owns it: the O(1) half of the ownership index (gap 6 of the
    // MMO arch review) that replaces TryGetOwner's linear cell scan. Maintained purely as a projection of each cell's
    // owned index via the CellSim register/unregister hooks wired in GetOrCreateCell, so restore / adopt / spawn /
    // migrate-freeze all propagate here with no extra bookkeeping. OwnerCount stays an independent scan (the oracle).
    private readonly Dictionary<long, CellCoord> ownerCell = new();
    private readonly Dictionary<int, long> clientPlayerNetId = new(); // session slot -> the client's player NetId
    private IJobScheduler scheduler;
    private CellSim[] tickBuffer = Array.Empty<CellSim>(); // reused per-tick fan-out snapshot of `ordered`

    /// <param name="cellSize">World-grid cell edge length in world units. Must be &gt; 0.</param>
    /// <param name="tickSeconds">Fixed timestep shared by every cell, seconds per tick. Must be &gt; 0.</param>
    /// <param name="registry">Shared replication registry handed to each cell's <see cref="ServerReplicator"/> and used to (de)serialize ghost snapshots.</param>
    /// <param name="interestCellSize">Cell edge length for each cell's AoI <see cref="InterestGrid"/>. Must be &gt; 0.</param>
    /// <param name="overlapMargin">Border overlap distance: owned entities within this distance of a cell edge are mirrored as ghosts into the neighbor across that edge. Must be &gt;= 0; 0 disables ghosting.</param>
    /// <param name="positionAccessor">Reads an entity's world position (over the game's position component). Required when <paramref name="overlapMargin"/> &gt; 0.</param>
    /// <param name="cellLink">Inter-cell message transport. Defaults to a fresh in-process <see cref="InProcessCellLink"/>.</param>
    /// <param name="scheduler">Worker pool that <see cref="Tick"/> fans the independent per-cell sim steps across. Defaults to an inline <see cref="SingleThreadedJobScheduler"/> (single-threaded, byte-unchanged behaviour); pass a <see cref="ThreadPoolJobScheduler"/> to tick cells across cores. Also settable later via <see cref="Scheduler"/>.</param>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry, float interestCellSize,
        float overlapMargin, CellPositionAccessor? positionAccessor = null, ICellLink? cellLink = null,
        IJobScheduler? scheduler = null)
    {
        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        if (tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "Tick duration must be > 0.");
        if (interestCellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(interestCellSize), interestCellSize, "Interest cell size must be positive.");
        if (overlapMargin < 0f)
            throw new ArgumentOutOfRangeException(nameof(overlapMargin), overlapMargin, "Overlap margin must be >= 0.");
        CellSize = cellSize;
        this.tickSeconds = tickSeconds;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.interestCellSize = interestCellSize;
        OverlapMargin = overlapMargin;
        this.positionAccessor = positionAccessor;
        link = cellLink ?? new InProcessCellLink();
        this.scheduler = scheduler ?? new SingleThreadedJobScheduler();
    }

    /// <summary>Convenience overload: no border ghosting (overlap margin 0).</summary>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry, float interestCellSize)
        : this(cellSize, tickSeconds, registry, interestCellSize, overlapMargin: 0f) { }

    /// <summary>Convenience overload: AoI interest cell size defaults to <paramref name="cellSize"/>, no ghosting.</summary>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry)
        : this(cellSize, tickSeconds, registry, cellSize, overlapMargin: 0f) { }

    /// <summary>World-grid cell edge length in world units.</summary>
    public float CellSize { get; }

    /// <summary>Border overlap distance for ghosting (0 = ghosting disabled).</summary>
    public float OverlapMargin { get; }

    /// <summary>The inter-cell message transport ghost sync runs over.</summary>
    public ICellLink CellLink => link;

    /// <summary>
    /// The worker pool <see cref="Tick"/> fans the independent per-cell sim steps across. Defaults to an inline
    /// <see cref="SingleThreadedJobScheduler"/>; assign a <see cref="ThreadPoolJobScheduler"/> to tick cells across
    /// cores. Set it during setup, not concurrently with a tick. Only <see cref="Tick"/> is parallelized; the
    /// cross-cell passes (<see cref="SyncGhosts"/>, <see cref="ProcessHandoffs"/>) stay single-threaded.
    /// </summary>
    public IJobScheduler Scheduler
    {
        get => scheduler;
        set => scheduler = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Number of cells currently instantiated.</summary>
    public int CellCount => ordered.Count;

    /// <summary>The instantiated cells, in creation order.</summary>
    public IReadOnlyCollection<CellSim> Cells => ordered;

    /// <summary>
    /// Raised once for each cell the first time its coordinate is instantiated (via <see cref="CellFor"/>,
    /// <see cref="SpawnAt"/>, a handoff destination, or <see cref="EnsureCell"/>). The load hook for per-cell
    /// persistence: a subscriber restores that cell's saved state. Fired synchronously on the creating thread.
    /// </summary>
    public event Action<CellSim>? CellCreated;

    /// <summary>The cell coordinate containing a world position. Pure - does not instantiate a cell.</summary>
    public CellCoord CoordFor(float worldX, float worldY) => CellCoord.FromWorld(worldX, worldY, CellSize);

    /// <summary>The <see cref="CellSim"/> containing a world position, creating it if it does not exist yet.</summary>
    public CellSim CellFor(float worldX, float worldY) => GetOrCreateCell(CoordFor(worldX, worldY));

    /// <summary>Gets the cell at <paramref name="coord"/>, creating it (and raising <see cref="CellCreated"/>) if absent.</summary>
    public CellSim EnsureCell(CellCoord coord) => GetOrCreateCell(coord);

    private CellSim GetOrCreateCell(CellCoord coord)
    {
        if (!cells.TryGetValue(coord, out CellSim? cell))
        {
            cell = new CellSim(coord, tickSeconds, registry, interestCellSize);
            // Project this cell's owned index into the host netId -> cell map. The register hook always wins (a cell
            // adopting an entity overwrites the prior owner); the unregister hook only clears the entry if it still
            // points here, so a stale release after a handoff can't wipe the new owner's entry.
            cell.OwnedRegisteredHook = (netId, _) => ownerCell[netId] = coord;
            cell.OwnedUnregisteredHook = netId =>
            {
                if (ownerCell.TryGetValue(netId, out CellCoord owner) && owner == coord) ownerCell.Remove(netId);
            };
            cells[coord] = cell;
            ordered.Add(cell);
            CellCreated?.Invoke(cell);
        }
        return cell;
    }

    /// <summary>Gets an existing cell by coordinate without creating one.</summary>
    public bool TryGetCell(CellCoord coord, out CellSim cell) => cells.TryGetValue(coord, out cell!);

    /// <summary>
    /// Spawns a new entity in the world of the cell that contains (<paramref name="worldX"/>,
    /// <paramref name="worldY"/>), creating that cell if needed. Returns the new entity; <paramref name="cell"/>
    /// is the cell it was routed to (so the caller can set its position / components).
    /// </summary>
    public Entity SpawnAt(float worldX, float worldY, out CellSim cell)
    {
        cell = CellFor(worldX, worldY);
        return cell.World.Spawn();
    }

    /// <summary>
    /// Spawns a freshly-owned entity for <paramref name="netId"/> in the cell containing (<paramref name="worldX"/>,
    /// <paramref name="worldY"/>), assigns its <see cref="NetId"/>, and registers it in the O(1) ownership index in
    /// one step - the eager spawn choke point. Equivalent to <see cref="SpawnAt"/> then <c>World.Set(new NetId(..))</c>
    /// then <see cref="CellSim.RegisterOwned"/>, so <see cref="TryGetOwner"/> resolves it without ever falling back to
    /// a scan. Returns the new entity; <paramref name="cell"/> is the cell it landed in, so the caller can set its
    /// position and other components.
    /// </summary>
    public Entity SpawnOwned(float worldX, float worldY, long netId, out CellSim cell)
    {
        cell = CellFor(worldX, worldY);
        Entity e = cell.World.Spawn();
        cell.World.Set(e, new NetId(netId));
        cell.RegisterOwned(netId, e);
        return e;
    }

    /// <summary>
    /// Advances every cell by <paramref name="elapsedSeconds"/> at the shared fixed rate (see
    /// <see cref="CellSim.Tick"/>), fanning the independent per-cell sim steps across <see cref="Scheduler"/>.
    /// Steps owned-entity simulation only; ghost mirroring is <see cref="SyncGhosts"/>. Cells are disjoint
    /// <see cref="World"/>s touching only their own state, so the fan-out is embarrassingly parallel and the
    /// parallel result is identical to the single-threaded one. The cell list is snapshotted before fanning (a
    /// sim step never creates cells, and <c>ordered</c> is append-only, so the reused buffer stays valid while the
    /// count is unchanged), giving the scheduler a stable index space.
    /// </summary>
    public void Tick(float elapsedSeconds, int maxTicksPerFrame = 8)
    {
        int n = ordered.Count;
        if (n == 0) return;
        if (tickBuffer.Length != n) tickBuffer = ordered.ToArray();
        CellSim[] snapshot = tickBuffer;
        scheduler.For(n, i => snapshot[i].Tick(elapsedSeconds, maxTicksPerFrame));
    }

    /// <summary>
    /// Mirrors border-overlap entities into neighboring cells as read-only ghosts. Each cell sends the owned
    /// entities within <see cref="OverlapMargin"/> of a shared edge to the existing neighbor across that edge (via
    /// <see cref="CellLink"/>, using the Replication codecs); each cell then applies inbound snapshots into its
    /// world as <see cref="Ghost"/> entities and despawns ghosts from any source that stopped mirroring. No-op when
    /// <see cref="OverlapMargin"/> is 0. Deterministic. Ghosting only targets cells that already exist (it never
    /// creates a neighbor). Runs single-threaded (unlike <see cref="Tick"/>): each cell writes ghosts into its
    /// <em>neighbours'</em> worlds via the <see cref="ICellLink"/>, so the passes are not cell-independent.
    /// </summary>
    /// <remarks>
    /// Channel rule: ghosts carry the <see cref="ReplicationChannels.Replicate"/> set with no owner, so a mob's
    /// server-only state (a <see cref="ReplicationChannels.Persist"/>/<see cref="ReplicationChannels.Migrate"/>-only
    /// aggro table) and a player's <see cref="ReplicationChannels.OwnerOnly"/> private state are NOT mirrored. This
    /// is correct: a ghost is a read-only mirror the neighbour cell serves to OTHER clients for cross-border
    /// collision / visibility / targeting - it never simulates the ghost (the owner does, with the full state) and
    /// no client should read another entity's private/server state from it. Nothing in the ghost path reads a
    /// non-Replicate component, so the Replicate set is complete for ghosting.
    /// </remarks>
    public void SyncGhosts()
    {
        if (OverlapMargin <= 0f) return;
        if (positionAccessor is null)
            throw new InvalidOperationException("SyncGhosts requires a position accessor when OverlapMargin > 0.");

        // Phase 1: each cell mirrors its owned border entities to existing neighbor cells.
        for (int i = 0; i < ordered.Count; i++)
        {
            CellSim owner = ordered[i];
            Dictionary<CellCoord, HashSet<long>>? byTarget = CollectBorders(owner);
            if (byTarget is null) continue;
            foreach (KeyValuePair<CellCoord, HashSet<long>> kv in byTarget)
            {
                if (!cells.ContainsKey(kv.Key)) continue; // only mirror to neighbors that exist
                // Ghosts serve OTHER cells' clients: the Replicate channel with no owner (so OwnerOnly private state
                // and Persist/Migrate-only server state are never mirrored - see the channel rule above).
                byte[] snapshot = SnapshotWriter.WriteFiltered(
                    owner.World, registry, kv.Value, ReplicationChannels.Replicate, ownerNetId: null);
                link.Send(new CellMessage(owner.Coord, kv.Key, CellMessageKind.GhostSync, snapshot));
            }
        }

        // Phase 2: each cell applies inbound ghosts, then clears ghosts from sources that sent nothing this sync.
        for (int i = 0; i < ordered.Count; i++)
        {
            CellSim target = ordered[i];
            var receivedFrom = new HashSet<CellCoord>();
            foreach (CellMessage msg in link.Drain(target.Coord, CellMessageKind.GhostSync))
            {
                target.ApplyGhostSnapshot(msg.Source, msg.Payload);
                receivedFrom.Add(msg.Source);
            }

            List<CellCoord>? stale = null;
            foreach (CellCoord source in target.GhostSources)
                if (!receivedFrom.Contains(source)) (stale ??= new List<CellCoord>()).Add(source);
            if (stale is not null)
                foreach (CellCoord source in stale) target.ClearGhostsFrom(source);
        }
    }

    /// <summary>
    /// Transfers authority for entities that crossed a cell boundary since the last call, with exactly-once
    /// semantics (never two owners, never zero). For each owned entity whose position now falls in another cell,
    /// the owner captures its <see cref="ReplicationChannels.Migrate"/> component set (Replication codecs), sends a
    /// <see cref="CellMessageKind.Migrate"/> over the link and freezes it (<see cref="Migrating"/>); the
    /// destination adopts it as owned (despawning any prior ghost of it) and acks; the owner then releases
    /// (despawns) it. The in-process link completes the whole Migrate -&gt; ack -&gt; release handshake within this
    /// call, so at every call boundary each entity is owned by exactly one cell. The entity keeps its
    /// <see cref="NetId"/>. Creates the destination cell if it does not exist. Requires a position accessor.
    /// </summary>
    /// <remarks>
    /// A networked <see cref="ICellLink"/> would instead span calls: a migrated entity stays <see cref="Migrating"/>
    /// (frozen, not counted as an owner, not simulated) on the source until the ack arrives, while the destination
    /// owns it once received - so there is never double-simulation and no permanent duplication or loss, only
    /// in-flight latency. Runs single-threaded (unlike <see cref="Tick"/>): handoff moves entities
    /// <em>between</em> cells via the <see cref="ICellLink"/>, so the work is not cell-independent.
    /// </remarks>
    public void ProcessHandoffs()
    {
        if (positionAccessor is null)
            throw new InvalidOperationException("ProcessHandoffs requires a position accessor.");

        // Phase 1: detect crossings with a read-only scan, then send Migrate + freeze (mutations after the scan).
        var crossings = new List<(CellSim source, Entity entity, long netId, CellCoord dest)>();
        foreach (CellSim owner in ordered.ToArray())
        {
            World w = owner.World;
            CellCoord oc = owner.Coord;
            w.ForEach<NetId>((Entity e, ref NetId id) =>
            {
                if (w.Has<Ghost>(e) || w.Has<Migrating>(e)) return;
                if (!positionAccessor!(w, e, out float x, out float y)) return;
                CellCoord dest = CoordFor(x, y);
                if (dest != oc) crossings.Add((owner, e, id.Value, dest));
            });
        }
        foreach ((CellSim source, Entity entity, long netId, CellCoord dest) in crossings)
        {
            GetOrCreateCell(dest); // ensure the destination exists to receive the migrate
            // Capture the Migrate channel: the entity carries only its migratable components to the destination cell
            // (a Replicate-only or Persist-only component that isn't also Migrate does not follow the crossing).
            byte[] capture = SnapshotWriter.WriteFiltered(
                source.World, registry, new HashSet<long> { netId }, ReplicationChannels.Migrate, ownerNetId: null);
            link.Send(new CellMessage(source.Coord, dest, CellMessageKind.Migrate, capture));
            source.World.Set(entity, new Migrating { Destination = dest });
            source.UnregisterOwned(netId); // frozen: relinquished here, so drop it from the owned index at once
        }

        // Phase 2: destinations adopt the migrated entities and ack their source.
        foreach (CellSim cell in ordered.ToArray())
        {
            foreach (CellMessage msg in link.Drain(cell.Coord, CellMessageKind.Migrate))
            {
                foreach (long netId in cell.AdoptFromMigrate(msg.Payload))
                    link.Send(new CellMessage(cell.Coord, msg.Source, CellMessageKind.MigrateAck, BitConverter.GetBytes(netId)));
            }
        }

        // Phase 3: sources release the frozen entity once its destination acked.
        foreach (CellSim cell in ordered.ToArray())
        {
            foreach (CellMessage msg in link.Drain(cell.Coord, CellMessageKind.MigrateAck))
                cell.ReleaseMigrating(BitConverter.ToInt64(msg.Payload, 0));
        }
    }

    /// <summary>
    /// How many cells currently own (authoritatively hold) the entity with <paramref name="netId"/>. For a live
    /// entity this is exactly 1 at every <see cref="ProcessHandoffs"/> call boundary (0 = lost, 2 = duplicated).
    /// </summary>
    public int OwnerCount(long netId)
    {
        // Deliberately an independent from-scratch scan (CellSim.ScanOwned), NOT an index read: it is the oracle that
        // cross-checks the exactly-once handoff invariant, so it must be able to observe a duplicate (2) or loss (0)
        // that a structurally-single-valued index never could.
        int n = 0;
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].ScanOwned(netId, out _)) n++;
        return n;
    }

    /// <summary>
    /// Finds the cell that owns <paramref name="netId"/> and the owned entity, in O(1) off the netId -&gt; cell index.
    /// False if no cell owns it. On an index miss or a stale entry it falls back to a scan across cells behind the
    /// index (rare: an unregistered raw spawn, or the indexed owner just lost the entity); the fallback re-caches the
    /// hit via <see cref="CellSim.TryGetOwned"/>, so the next lookup is O(1) again.
    /// </summary>
    public bool TryGetOwner(long netId, out CellSim cell, out Entity entity)
    {
        if (ownerCell.TryGetValue(netId, out CellCoord coord)
            && cells.TryGetValue(coord, out CellSim? c)
            && c.TryGetOwned(netId, out entity))
        {
            cell = c;
            return true;
        }
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].TryGetOwned(netId, out entity)) { cell = ordered[i]; return true; }
        cell = null!;
        entity = default;
        return false;
    }

    /// <summary>The maintained host ownership index (netId -&gt; owning cell coord), exposed for tests to check against a scan.</summary>
    internal IReadOnlyDictionary<long, CellCoord> OwnerCellEntries => ownerCell;

    /// <summary>
    /// Binds a client (session <paramref name="slot"/>, e.g. from <c>NetServer</c>) to its player entity
    /// (<paramref name="playerNetId"/>). The client's <b>home cell</b> is then derived as the cell that currently
    /// owns that player, so it follows the player across handoffs automatically (seamless re-bind).
    /// </summary>
    public void BindClient(int slot, long playerNetId) => clientPlayerNetId[slot] = playerNetId;

    /// <summary>Removes a client binding. Returns false if the slot was not bound.</summary>
    public bool UnbindClient(int slot) => clientPlayerNetId.Remove(slot);

    /// <summary>Whether a client is bound to <paramref name="slot"/>.</summary>
    public bool IsClientBound(int slot) => clientPlayerNetId.ContainsKey(slot);

    /// <summary>
    /// The cell currently serving <paramref name="slot"/> - the cell that owns the client's player. False if the
    /// slot is unbound or the player is not currently owned by any cell.
    /// </summary>
    public bool TryGetHomeCell(int slot, out CellSim cell)
    {
        cell = null!;
        return clientPlayerNetId.TryGetValue(slot, out long playerNetId) && TryGetOwner(playerNetId, out cell, out _);
    }

    /// <summary>
    /// Builds the area-of-interest snapshot for a client from its <b>single home cell</b>: the entities (owned and
    /// border ghosts) within <paramref name="interestRadius"/> of the client's player, serialized via the
    /// Replication codecs. Relies on the invariant <see cref="OverlapMargin"/> &gt;= <paramref name="interestRadius"/>
    /// (so the home cell already holds, as ghosts, everything in the player's interest) and throws if it is
    /// violated. On a player crossing, the home cell re-binds automatically, so the next snapshot comes from the new
    /// cell - which already held the surroundings as ghosts, so the client's view is continuous.
    /// Serves the <see cref="ReplicationChannels.Replicate"/> channel scoped to this client's own player, so an
    /// <see cref="ReplicationChannels.OwnerOnly"/> component reaches this client only on its own player entity, never
    /// on another player it observes.
    /// </summary>
    public byte[] SnapshotForClient(int slot, float interestRadius)
    {
        (World world, HashSet<long> interest) = HomeInterest(slot, interestRadius);
        // HomeInterest validated the binding, so the slot's player net id is present: it is this client's owner id.
        long ownerNetId = clientPlayerNetId[slot];
        return SnapshotWriter.WriteFiltered(world, registry, interest, ReplicationChannels.Replicate, ownerNetId);
    }

    /// <summary>
    /// Resolves a client's <b>home-cell</b> world and its area-of-interest net-id set (owned + border ghosts within
    /// <paramref name="interestRadius"/> of the client's player) - the shared basis for serving that client, whether
    /// as a full snapshot (<see cref="SnapshotForClient"/>) or as a per-client delta (fed to
    /// <see cref="AoiDeltaReplicator.WriteFor"/>). Because the interest is keyed by <see cref="NetId"/> and the home
    /// cell already holds the surroundings as ghosts, a delta encoder built on it reads a boundary crossing as
    /// component changes on stable ids, never a despawn+respawn. Same invariants (and throws) as
    /// <see cref="SnapshotForClient"/>: requires a position accessor, a bound client with an owned, positioned player,
    /// and <paramref name="interestRadius"/> in <c>[0, OverlapMargin]</c>.
    /// </summary>
    public (World world, HashSet<long> interest) HomeInterest(int slot, float interestRadius)
    {
        if (positionAccessor is null)
            throw new InvalidOperationException("HomeInterest requires a position accessor.");
        if (interestRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(interestRadius), interestRadius, "Interest radius must be >= 0.");
        if (interestRadius > OverlapMargin)
            throw new InvalidOperationException(
                $"Interest radius {interestRadius} exceeds overlap margin {OverlapMargin}: the home cell can't hold the full AoI as ghosts. Increase OverlapMargin so it is >= the interest radius.");
        if (!clientPlayerNetId.TryGetValue(slot, out long playerNetId))
            throw new InvalidOperationException($"No client bound to slot {slot}.");
        if (!TryGetOwner(playerNetId, out CellSim home, out Entity player))
            throw new InvalidOperationException($"Client {slot}'s player {playerNetId} is not owned by any cell.");
        if (!positionAccessor(home.World, player, out float px, out float py))
            throw new InvalidOperationException($"Client {slot}'s player {playerNetId} has no position.");

        home.RebuildInterest(positionAccessor);
        HashSet<long> interest = home.Interest.Query(px, py, interestRadius);
        return (home.World, interest);
    }

    /// <summary>
    /// Groups an owner cell's owned (non-ghost) border entities by the neighbor cell they should be mirrored into.
    /// An entity within <see cref="OverlapMargin"/> of an edge mirrors into the neighbor across that edge; near a
    /// corner it mirrors into the two edge neighbors and the diagonal neighbor. Returns null if no border entities.
    /// </summary>
    private Dictionary<CellCoord, HashSet<long>>? CollectBorders(CellSim owner)
    {
        Dictionary<CellCoord, HashSet<long>>? byTarget = null;
        float s = CellSize, m = OverlapMargin;
        CellCoord c = owner.Coord;
        float minX = c.X * s, maxX = minX + s, minY = c.Y * s, maxY = minY + s;
        World world = owner.World;

        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e)) return; // never ghost a ghost or a leaving entity
            if (!positionAccessor!(world, e, out float x, out float y)) return;

            bool nearW = x - minX < m, nearE = maxX - x < m, nearS = y - minY < m, nearN = maxY - y < m;
            if (!(nearW || nearE || nearS || nearN)) return;

            long netId = id.Value;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (dx == -1 && !nearW) continue;
                if (dx == 1 && !nearE) continue;
                if (dy == -1 && !nearS) continue;
                if (dy == 1 && !nearN) continue;

                var target = new CellCoord(c.X + dx, c.Y + dy);
                byTarget ??= new Dictionary<CellCoord, HashSet<long>>();
                if (!byTarget.TryGetValue(target, out HashSet<long>? set))
                {
                    set = new HashSet<long>();
                    byTarget[target] = set;
                }
                set.Add(netId);
            }
        });

        return byTarget;
    }
}
