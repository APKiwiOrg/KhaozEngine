using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

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
/// globally-unique <see cref="NetId"/>s across cells.
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

    /// <param name="cellSize">World-grid cell edge length in world units. Must be &gt; 0.</param>
    /// <param name="tickSeconds">Fixed timestep shared by every cell, seconds per tick. Must be &gt; 0.</param>
    /// <param name="registry">Shared replication registry handed to each cell's <see cref="ServerReplicator"/> and used to (de)serialize ghost snapshots.</param>
    /// <param name="interestCellSize">Cell edge length for each cell's AoI <see cref="InterestGrid"/>. Must be &gt; 0.</param>
    /// <param name="overlapMargin">Border overlap distance: owned entities within this distance of a cell edge are mirrored as ghosts into the neighbor across that edge. Must be &gt;= 0; 0 disables ghosting.</param>
    /// <param name="positionAccessor">Reads an entity's world position (over the game's position component). Required when <paramref name="overlapMargin"/> &gt; 0.</param>
    /// <param name="cellLink">Inter-cell message transport. Defaults to a fresh in-process <see cref="InProcessCellLink"/>.</param>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry, float interestCellSize,
        float overlapMargin, CellPositionAccessor? positionAccessor = null, ICellLink? cellLink = null)
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

    /// <summary>Number of cells currently instantiated.</summary>
    public int CellCount => ordered.Count;

    /// <summary>The instantiated cells, in creation order.</summary>
    public IReadOnlyCollection<CellSim> Cells => ordered;

    /// <summary>The cell coordinate containing a world position. Pure - does not instantiate a cell.</summary>
    public CellCoord CoordFor(float worldX, float worldY) => CellCoord.FromWorld(worldX, worldY, CellSize);

    /// <summary>The <see cref="CellSim"/> containing a world position, creating it if it does not exist yet.</summary>
    public CellSim CellFor(float worldX, float worldY)
    {
        CellCoord coord = CoordFor(worldX, worldY);
        if (!cells.TryGetValue(coord, out CellSim? cell))
        {
            cell = new CellSim(coord, tickSeconds, registry, interestCellSize);
            cells[coord] = cell;
            ordered.Add(cell);
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
    /// Advances every cell by <paramref name="elapsedSeconds"/> at the shared fixed rate (see
    /// <see cref="CellSim.Tick"/>). Steps owned-entity simulation only; ghost mirroring is <see cref="SyncGhosts"/>.
    /// </summary>
    public void Tick(float elapsedSeconds, int maxTicksPerFrame = 8)
    {
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Tick(elapsedSeconds, maxTicksPerFrame);
    }

    /// <summary>
    /// Mirrors border-overlap entities into neighboring cells as read-only ghosts. Each cell sends the owned
    /// entities within <see cref="OverlapMargin"/> of a shared edge to the existing neighbor across that edge (via
    /// <see cref="CellLink"/>, using the Replication codecs); each cell then applies inbound snapshots into its
    /// world as <see cref="Ghost"/> entities and despawns ghosts from any source that stopped mirroring. No-op when
    /// <see cref="OverlapMargin"/> is 0. Deterministic. Ghosting only targets cells that already exist (it never
    /// creates a neighbor).
    /// </summary>
    public void SyncGhosts()
    {
        if (OverlapMargin <= 0f) return;
        if (positionAccessor is null)
            throw new InvalidOperationException("SyncGhosts requires a position accessor when OverlapMargin > 0.");

        // Phase 1: each cell mirrors its owned border entities to existing neighbor cells.
        for (int i = 0; i < ordered.Count; i++)
        {
            CellSim owner = ordered[i];
            Dictionary<CellCoord, HashSet<int>>? byTarget = CollectBorders(owner);
            if (byTarget is null) continue;
            foreach (KeyValuePair<CellCoord, HashSet<int>> kv in byTarget)
            {
                if (!cells.ContainsKey(kv.Key)) continue; // only mirror to neighbors that exist
                byte[] snapshot = SnapshotWriter.WriteFiltered(owner.World, registry, kv.Value);
                link.Send(new CellMessage(owner.Coord, kv.Key, CellMessageKind.GhostSync, snapshot));
            }
        }

        // Phase 2: each cell applies inbound ghosts, then clears ghosts from sources that sent nothing this sync.
        for (int i = 0; i < ordered.Count; i++)
        {
            CellSim target = ordered[i];
            var receivedFrom = new HashSet<CellCoord>();
            foreach (CellMessage msg in link.Drain(target.Coord))
            {
                if (msg.Kind != CellMessageKind.GhostSync) continue;
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
    /// Groups an owner cell's owned (non-ghost) border entities by the neighbor cell they should be mirrored into.
    /// An entity within <see cref="OverlapMargin"/> of an edge mirrors into the neighbor across that edge; near a
    /// corner it mirrors into the two edge neighbors and the diagonal neighbor. Returns null if no border entities.
    /// </summary>
    private Dictionary<CellCoord, HashSet<int>>? CollectBorders(CellSim owner)
    {
        Dictionary<CellCoord, HashSet<int>>? byTarget = null;
        float s = CellSize, m = OverlapMargin;
        CellCoord c = owner.Coord;
        float minX = c.X * s, maxX = minX + s, minY = c.Y * s, maxY = minY + s;
        World world = owner.World;

        world.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (world.Has<Ghost>(e)) return;                          // never re-ghost a ghost
            if (!positionAccessor!(world, e, out float x, out float y)) return;

            bool nearW = x - minX < m, nearE = maxX - x < m, nearS = y - minY < m, nearN = maxY - y < m;
            if (!(nearW || nearE || nearS || nearN)) return;

            int netId = id.Value;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (dx == -1 && !nearW) continue;
                if (dx == 1 && !nearE) continue;
                if (dy == -1 && !nearS) continue;
                if (dy == 1 && !nearN) continue;

                var target = new CellCoord(c.X + dx, c.Y + dy);
                byTarget ??= new Dictionary<CellCoord, HashSet<int>>();
                if (!byTarget.TryGetValue(target, out HashSet<int>? set))
                {
                    set = new HashSet<int>();
                    byTarget[target] = set;
                }
                set.Add(netId);
            }
        });

        return byTarget;
    }
}
