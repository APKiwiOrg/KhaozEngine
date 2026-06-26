namespace KhaozEngine.Benchmarks;

/// <summary>
/// One point in the benchmark matrix: a grid of <see cref="CellCount"/> cells
/// (<see cref="GridWidth"/> × <see cref="GridHeight"/>), <see cref="EntitiesPerCell"/> entities each, and
/// <see cref="Systems"/> trivial systems per cell. Population is seeded (<see cref="Seed"/>) so re-running the
/// same config is bit-identical; <see cref="WarmupTicks"/> are excluded from timing and <see cref="TimedTicks"/>
/// are measured. The total work of one server tick is <c>O(Systems · TotalEntities)</c>.
/// </summary>
public sealed class BenchmarkConfig
{
    /// <summary>Human-readable label for the matrix row (e.g. "many small cells").</summary>
    public required string Name { get; init; }

    /// <summary>Cells along X. <c>CellCount = GridWidth * GridHeight = C</c>.</summary>
    public required int GridWidth { get; init; }

    /// <summary>Cells along Y.</summary>
    public required int GridHeight { get; init; }

    /// <summary>Entities populated into every cell (E).</summary>
    public required int EntitiesPerCell { get; init; }

    /// <summary>Trivial integrate-position systems registered per cell (S). Models S systems' worth of per-tick work.</summary>
    public int Systems { get; init; } = 4;

    /// <summary>Ticks run before timing starts (JIT warm, caches hot). Excluded from the reported numbers.</summary>
    public int WarmupTicks { get; init; } = 30;

    /// <summary>Ticks measured by the stopwatch. Per-tick wall-clock = elapsed / ticks actually produced.</summary>
    public int TimedTicks { get; init; } = 120;

    /// <summary>Seed for the population RNG. Same seed ⇒ identical positions/velocities ⇒ repeatable timings.</summary>
    public ulong Seed { get; init; } = 0xC0FFEEUL;

    /// <summary>World-grid cell edge length handed to the <c>ShardHost</c>. Population is placed inside each cell.</summary>
    public float CellSize { get; init; } = 100f;

    /// <summary>Fixed timestep, seconds per tick (default 30 Hz). One <c>ShardHost.Tick</c> produces one fixed tick.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;

    /// <summary>Number of cells in the grid (C).</summary>
    public int CellCount => GridWidth * GridHeight;

    /// <summary>Total entities across the whole grid (N = C · E).</summary>
    public long TotalEntities => (long)CellCount * EntitiesPerCell;
}
