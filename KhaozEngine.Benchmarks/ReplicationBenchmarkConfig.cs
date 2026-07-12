namespace KhaozEngine.Benchmarks;

/// <summary>
/// One point in the replication benchmark matrix: <see cref="ClientCount"/> simulated clients, each with a fixed
/// area-of-interest circle, <see cref="EntityCount"/> replicated entities spread across a
/// <see cref="FieldSize"/> x <see cref="FieldSize"/> square field, and <see cref="ComponentsPerEntity"/> replicated
/// components registered and set per entity (1 = <c>ReplPosition</c> only, up to 4 adds the never-mutated filler
/// components). Population is seeded (<see cref="Seed"/>) so re-running the same config is bit-identical.
/// </summary>
public sealed class ReplicationBenchmarkConfig
{
    /// <summary>Human-readable label for the matrix row.</summary>
    public required string Name { get; init; }

    /// <summary>Simulated connected clients (C), each with its own AoI circle and <c>AoiDeltaReplicator</c> slot.</summary>
    public required int ClientCount { get; init; }

    /// <summary>Replicated entities spread across the field (E).</summary>
    public required int EntityCount { get; init; }

    /// <summary>
    /// Replicated components registered and set per entity, including the always-present position. 1 = position
    /// only, 2/3/4 progressively add the never-mutated <c>ReplFillerA</c>/<c>ReplFillerB</c>/<c>ReplFillerC</c>.
    /// </summary>
    public required int ComponentsPerEntity { get; init; }

    /// <summary>Side length (world units) of the square field entities are spread across.</summary>
    public float FieldSize { get; init; } = 1000f;

    /// <summary>Ticks run before timing starts (JIT warm, and to move every client past its always-full first
    /// snapshot). Excluded from the reported numbers.</summary>
    public int WarmupTicks { get; init; } = 5;

    /// <summary>Ticks measured by the stopwatch and the allocation/GC snapshot.</summary>
    public int TimedTicks { get; init; } = 30;

    /// <summary>Seed for the population RNG. Same seed -> identical positions/velocities/client placement -> repeatable timings.</summary>
    public ulong Seed { get; init; } = 0xC0FFEEUL;

    /// <summary>Per-tick, per-axis position delta scale. Every entity moves every tick (movement-heavy steady
    /// state) by its own seeded unit-ish velocity times this step.</summary>
    public float MoveStep { get; init; } = 0.5f;

    /// <summary>
    /// Per-client AoI query radius: fixed at 18% of <see cref="FieldSize"/> so a client's circle (area = pi * r^2)
    /// covers about 10% of the field's area (pi * 0.18^2 ~= 0.102) - inside the 5-15% band the brief calls for,
    /// independent of field size. Client centers are placed with at least this much padding from every edge (see
    /// <see cref="ReplicationTickBenchmark.Build"/>) so the circle never clips outside the field.
    /// </summary>
    public float AoiRadius => FieldSize * 0.18f;
}
