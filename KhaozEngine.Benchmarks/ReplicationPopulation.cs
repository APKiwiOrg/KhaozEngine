using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// One simulated client's fixed AoI circle and its <see cref="AoiDeltaReplicator"/> slot id. Centers are placed by
/// <see cref="ReplicationTickBenchmark.Build"/> with enough padding from the field edge that the circle never
/// clips outside it.
/// </summary>
public readonly record struct ClientAoi(int Slot, float Cx, float Cy, float Radius);

/// <summary>
/// Everything <see cref="ReplicationTickBenchmark.Run"/> needs to drive ticks: the populated world, the
/// registry/replicator/grid under test, and the per-entity / per-client state the timed loop mutates and queries
/// every tick. Returned by <see cref="ReplicationTickBenchmark.Build"/> so structural tests can inspect the
/// population directly (entity/client counts, positions) without re-deriving the build logic.
/// </summary>
public sealed class ReplicationPopulation
{
    /// <summary>The populated world (every entity carries a <see cref="NetId"/> plus the configured replicated components).</summary>
    public required World World { get; init; }

    /// <summary>The registry the population's components were registered in.</summary>
    public required ReplicationRegistry Registry { get; init; }

    /// <summary>The per-client AoI delta encoder under test. Never modified by the benchmark - only measured.</summary>
    public required AoiDeltaReplicator Replicator { get; init; }

    /// <summary>The spatial hash rebuilt from fresh positions once per client per tick (mirroring
    /// <c>ShardHost.HomeInterest</c>'s <c>CellSim.RebuildInterest</c> cadence) and queried once per client.</summary>
    public required InterestGrid Grid { get; init; }

    /// <summary>Every populated entity's ECS handle, in spawn order.</summary>
    public required Entity[] Entities { get; init; }

    /// <summary>Every populated entity's <see cref="NetId"/> value, parallel to <see cref="Entities"/>.</summary>
    public required long[] NetIds { get; init; }

    /// <summary>Per-entity X velocity (world units per <see cref="ReplicationBenchmarkConfig.MoveStep"/>), parallel to <see cref="Entities"/>.</summary>
    public required float[] VelX { get; init; }

    /// <summary>Per-entity Y velocity, parallel to <see cref="Entities"/>.</summary>
    public required float[] VelY { get; init; }

    /// <summary>Every simulated client's fixed AoI circle and slot id.</summary>
    public required ClientAoi[] Clients { get; init; }

    /// <summary>
    /// Hoisted <see cref="World.ForEach{T1,T2}"/> delegate that inserts every entity's current position into
    /// <see cref="Grid"/>, built once in <see cref="ReplicationTickBenchmark.Build"/> and reused on every rebuild
    /// so the interest-grid rebuild step itself never allocates a closure (the measured allocation is the
    /// replication hot path's, not the benchmark harness's own driving code).
    /// </summary>
    public required RefAction<NetId, ReplPosition> InsertIntoGrid { get; init; }
}
