using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Sharding;

/// <summary>
/// In-process host of the world's uniform cell grid. Owns the <see cref="CellCoord"/> -&gt; <see cref="CellSim"/>
/// map, creates cells on demand, routes a world position (and the entities spawned there) to the cell that
/// contains it, and <see cref="Tick"/>s every live cell at one shared fixed rate. This is the EVE / seamless-MMO
/// topology run as a single process; a multi-process deployment implements the same shape behind the inter-cell
/// seam.
/// </summary>
/// <remarks>
/// Phase 3A: cells are independent - an entity is owned by the cell its position falls in, but nothing crosses,
/// ghosts, or hands off between cells yet (those are later Phase 3 stages). Deterministic and headless. Cells are
/// retained in creation order (<see cref="Cells"/>) so iteration is stable.
/// </remarks>
public sealed class ShardHost
{
    private readonly float tickSeconds;
    private readonly ReplicationRegistry registry;
    private readonly float interestCellSize;
    private readonly Dictionary<CellCoord, CellSim> cells = new();
    private readonly List<CellSim> ordered = new();

    /// <param name="cellSize">World-grid cell edge length in world units. Must be &gt; 0.</param>
    /// <param name="tickSeconds">Fixed timestep shared by every cell, seconds per tick. Must be &gt; 0.</param>
    /// <param name="registry">Shared replication registry handed to each cell's <see cref="ServerReplicator"/>.</param>
    /// <param name="interestCellSize">Cell edge length for each cell's AoI <see cref="InterestGrid"/>. Must be &gt; 0.</param>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry, float interestCellSize)
    {
        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        if (tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "Tick duration must be > 0.");
        if (interestCellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(interestCellSize), interestCellSize, "Interest cell size must be positive.");
        CellSize = cellSize;
        this.tickSeconds = tickSeconds;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.interestCellSize = interestCellSize;
    }

    /// <summary>Convenience overload: the AoI interest cell size defaults to the world <paramref name="cellSize"/>.</summary>
    public ShardHost(float cellSize, float tickSeconds, ReplicationRegistry registry)
        : this(cellSize, tickSeconds, registry, cellSize) { }

    /// <summary>World-grid cell edge length in world units.</summary>
    public float CellSize { get; }

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
    /// <see cref="CellSim.Tick"/>). Cells tick independently; cross-cell interaction is a later Phase 3 stage.
    /// </summary>
    public void Tick(float elapsedSeconds, int maxTicksPerFrame = 8)
    {
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Tick(elapsedSeconds, maxTicksPerFrame);
    }
}
