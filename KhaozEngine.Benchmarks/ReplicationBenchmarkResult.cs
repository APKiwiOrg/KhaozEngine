namespace KhaozEngine.Benchmarks;

/// <summary>
/// The measured outcome of running one <see cref="ReplicationBenchmarkConfig"/>: per-tick wall-clock, allocation,
/// GC collections, and wire bytes for the real per-client <c>AoiDeltaReplicator.WriteFor</c> hot path. This is the
/// baseline number every later replication-hot-path item (shared per-tick AoI projection, pooled capture buffers)
/// must move.
/// </summary>
public sealed class ReplicationBenchmarkResult
{
    /// <summary>The config that produced this result.</summary>
    public required ReplicationBenchmarkConfig Config { get; init; }

    /// <summary>Ticks the stopwatch and allocation snapshot actually covered.</summary>
    public required long TicksMeasured { get; init; }

    /// <summary>Wall-clock of the timed loop, milliseconds (warmup excluded).</summary>
    public required double ElapsedMs { get; init; }

    /// <summary>Bytes allocated on the calling thread across the whole timed loop
    /// (<c>GC.GetAllocatedBytesForCurrentThread</c> before/after), warmup excluded.</summary>
    public required long AllocatedBytes { get; init; }

    /// <summary>Gen0 collections observed during the timed loop (<c>GC.CollectionCount(0)</c> delta).</summary>
    public required int Gen0Collections { get; init; }

    /// <summary>Gen1 collections observed during the timed loop.</summary>
    public required int Gen1Collections { get; init; }

    /// <summary>Gen2 collections observed during the timed loop.</summary>
    public required int Gen2Collections { get; init; }

    /// <summary>Sum of every <c>WriteFor</c> return array's length across the whole timed loop (every client, every
    /// tick) - the wire output later items must reproduce byte-for-byte.</summary>
    public required long WireBytesTotal { get; init; }

    /// <summary>Mean wall-clock for one tick (movement + one shared interest-grid rebuild + one shared world capture +
    /// every client's <c>Query</c> + <c>WriteFor</c>), milliseconds.</summary>
    public double PerTickMs => TicksMeasured <= 0 ? 0 : ElapsedMs / TicksMeasured;

    /// <summary>Mean bytes allocated per tick.</summary>
    public double AllocBytesPerTick => TicksMeasured <= 0 ? 0 : (double)AllocatedBytes / TicksMeasured;

    /// <summary>Gen0 collections per 1000 ticks (collections are rare per single tick, so this keeps the figure readable).</summary>
    public double Gen0PerKTicks => TicksMeasured <= 0 ? 0 : Gen0Collections * 1000.0 / TicksMeasured;

    /// <summary>Gen1 collections per 1000 ticks.</summary>
    public double Gen1PerKTicks => TicksMeasured <= 0 ? 0 : Gen1Collections * 1000.0 / TicksMeasured;

    /// <summary>Gen2 collections per 1000 ticks.</summary>
    public double Gen2PerKTicks => TicksMeasured <= 0 ? 0 : Gen2Collections * 1000.0 / TicksMeasured;

    /// <summary>Mean total wire bytes written per tick, summed across every client.</summary>
    public double WireBytesPerTick => TicksMeasured <= 0 ? 0 : (double)WireBytesTotal / TicksMeasured;
}
