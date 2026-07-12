using KhaozEngine.Ecs;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// The replicated position component every replication-benchmark entity carries and the per-tick movement step
/// mutates. Registered in the benchmark's own <see cref="KhaozEngine.Replication.ReplicationRegistry"/> at type id
/// 1 with a plain <c>BinaryWriter</c>/<c>BinaryReader</c> codec - this is the field <see cref="KhaozEngine.Replication.InterestGrid"/>
/// queries and <see cref="KhaozEngine.Replication.AoiDeltaReplicator"/> diffs are built from.
/// </summary>
public struct ReplPosition : IComponent
{
    public float X;
    public float Y;
}

/// <summary>
/// A small filler component the movement step never mutates. Present on every entity once a matrix row's
/// component count is 2 or more, so a full first-tick snapshot (position + fillers) is measurably larger than the
/// steady-state delta once a client has acked (fillers never change, so only <see cref="ReplPosition"/> keeps
/// showing up as changed) - the ack-promotion behaviour <c>ReplicationTickBenchmarkTests</c> pins.
/// </summary>
public struct ReplFillerA : IComponent { public float Value; }

/// <summary>See <see cref="ReplFillerA"/> - a second never-mutated filler component.</summary>
public struct ReplFillerB : IComponent { public float Value; }

/// <summary>See <see cref="ReplFillerA"/> - a third never-mutated filler component.</summary>
public struct ReplFillerC : IComponent { public float Value; }
