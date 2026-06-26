namespace KhaozEngine.Benchmarks;

/// <summary>
/// The measured outcome of running one <see cref="BenchmarkConfig"/> on the single-threaded baseline: the number
/// every later parallel layer must move. Wall-clock is divided by the ticks <em>actually</em> produced
/// (<see cref="TicksMeasured"/>), so float accumulator drift can never skew the per-tick figure.
/// </summary>
public sealed class BenchmarkResult
{
    /// <summary>The config that produced this result.</summary>
    public required BenchmarkConfig Config { get; init; }

    /// <summary>Cells instantiated (C).</summary>
    public required int CellCount { get; init; }

    /// <summary>Total entities simulated (N = C · E).</summary>
    public required long TotalEntities { get; init; }

    /// <summary>Fixed ticks the stopwatch actually covered (each <c>ShardHost.Tick</c> = one tick across all cells).</summary>
    public required long TicksMeasured { get; init; }

    /// <summary>Wall-clock of the timed loop, milliseconds (warmup excluded).</summary>
    public required double ElapsedMs { get; init; }

    /// <summary>Mean wall-clock for one server tick (all cells advanced once), milliseconds.</summary>
    public double PerTickMs => TicksMeasured <= 0 ? 0 : ElapsedMs / TicksMeasured;

    /// <summary>Entities processed per second: <c>N</c> per tick ÷ per-tick seconds. The headline throughput number.</summary>
    public double EntitiesPerSecond
    {
        get
        {
            double perTickSeconds = PerTickMs / 1000.0;
            return perTickSeconds <= 0 ? 0 : TotalEntities / perTickSeconds;
        }
    }

    /// <summary>
    /// Component-visits per second: <c>S · entities/sec</c>. Surfaces the real <c>O(S·N)</c> work rate (each of the
    /// S systems passes over all N entities every tick), so rows with different S stay comparable.
    /// </summary>
    public double ComponentVisitsPerSecond => EntitiesPerSecond * Config.Systems;
}
