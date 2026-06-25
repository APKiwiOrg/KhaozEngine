using System;
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
/// Phase 3A: a self-contained, deterministic, headless container. No cross-cell crossing, ghosting, or authority
/// handoff yet (those are later Phase 3 stages); a cell here simulates only its own world. The
/// <see cref="ServerReplicator"/> and <see cref="InterestGrid"/> are exposed but not auto-driven by
/// <see cref="Tick"/> - snapshot rate is intentionally decoupled from tick rate, so the host/game captures and
/// queries when it chooses.
/// </remarks>
public sealed class CellSim
{
    private readonly FixedTickHost tickHost;

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
}
