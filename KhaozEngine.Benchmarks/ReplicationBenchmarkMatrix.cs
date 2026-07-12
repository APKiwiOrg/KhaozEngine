using System.Collections.Generic;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// The default replication benchmark matrix: clients (C) in {8, 64}, entities (E) in {4096, 16384}, and
/// components/entity in {1, 4}, trimmed from the full 2x2x2 cross product to the 6 rows that exercise every axis
/// at least once while keeping a full <c>--replication</c> run under about two minutes in Release. The two
/// heaviest rows (C=64, E=16384) use fewer warmup/timed ticks than the default - their O(C*E) per-tick scan cost
/// is already large enough that fewer samples keep the run time bounded without losing a stable mean.
/// </summary>
public static class ReplicationBenchmarkMatrix
{
    /// <summary>The default set of configs the full (non-quick) replication run measures.</summary>
    public static IReadOnlyList<ReplicationBenchmarkConfig> Default() => new[]
    {
        new ReplicationBenchmarkConfig { Name = "C=8  E=4096  comp=1",  ClientCount = 8,  EntityCount = 4096,  ComponentsPerEntity = 1 },
        new ReplicationBenchmarkConfig { Name = "C=8  E=4096  comp=4",  ClientCount = 8,  EntityCount = 4096,  ComponentsPerEntity = 4 },
        new ReplicationBenchmarkConfig { Name = "C=8  E=16384 comp=1", ClientCount = 8,  EntityCount = 16384, ComponentsPerEntity = 1 },
        new ReplicationBenchmarkConfig { Name = "C=64 E=4096  comp=1", ClientCount = 64, EntityCount = 4096,  ComponentsPerEntity = 1 },
        new ReplicationBenchmarkConfig { Name = "C=64 E=16384 comp=1", ClientCount = 64, EntityCount = 16384, ComponentsPerEntity = 1, WarmupTicks = 3, TimedTicks = 15 },
        new ReplicationBenchmarkConfig { Name = "C=64 E=16384 comp=4", ClientCount = 64, EntityCount = 16384, ComponentsPerEntity = 4, WarmupTicks = 3, TimedTicks = 15 },
    };

    /// <summary>A lighter matrix for <c>--quick</c>: small E and few ticks so a run is sub-second.</summary>
    public static IReadOnlyList<ReplicationBenchmarkConfig> Quick() => new[]
    {
        new ReplicationBenchmarkConfig { Name = "C=8  E=512  comp=1", ClientCount = 8,  EntityCount = 512, ComponentsPerEntity = 1, WarmupTicks = 1, TimedTicks = 5 },
        new ReplicationBenchmarkConfig { Name = "C=8  E=512  comp=4", ClientCount = 8,  EntityCount = 512, ComponentsPerEntity = 4, WarmupTicks = 1, TimedTicks = 5 },
        new ReplicationBenchmarkConfig { Name = "C=64 E=512  comp=1", ClientCount = 64, EntityCount = 512, ComponentsPerEntity = 1, WarmupTicks = 1, TimedTicks = 5 },
    };
}
