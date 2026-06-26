using System.Collections.Generic;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// The default benchmark matrix - the three population shapes the program's big-O table distinguishes, held at
/// equal total entity count (N = 65,536) so their per-tick / throughput numbers are directly comparable. The
/// single-threaded baseline is O(S·N) regardless of shape; the shape is what the <em>later</em> parallel layers
/// react to (cells split across cores, a hot cell's entities split across cores), so the baselines for all three
/// must exist now.
/// </summary>
public static class BenchmarkMatrix
{
    private const int TargetN = 65536;   // shared across regimes so entities/sec is comparable

    /// <summary>The default set of configs run by the benchmark exe.</summary>
    public static IReadOnlyList<BenchmarkConfig> Default() => new[]
    {
        // Many small cells (large C, small E): the dominant MMO shape - layer 1 (parallel cell ticks) targets this.
        new BenchmarkConfig { Name = "many small cells", GridWidth = 32, GridHeight = 32, EntitiesPerCell = TargetN / 1024 }, // C=1024, E=64
        // One hot cell (C=1, large E): the degenerate case layer 2 (parallel ForEach) targets - cells can't help here.
        new BenchmarkConfig { Name = "one hot cell",     GridWidth = 1,  GridHeight = 1,  EntitiesPerCell = TargetN },        // C=1, E=65536
        // A mid case (moderate C and E): the in-between the big-O table calls out.
        new BenchmarkConfig { Name = "mid (8x8 cells)",  GridWidth = 8,  GridHeight = 8,  EntitiesPerCell = TargetN / 64 },   // C=64, E=1024
    };
}
