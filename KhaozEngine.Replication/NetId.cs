using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Network identity of a replicated entity: stable across the wire, the same on server and client even though
/// the underlying <see cref="Entity"/> handles differ. The server assigns it; the client keys its
/// <see cref="ClientReplicationView"/> map on it.
/// </summary>
/// <remarks>
/// The id is 64-bit (widened from the pre-10.0.0 32-bit <c>int</c>) under a documented node-prefix scheme: the high
/// 16 bits are a node/allocator id (0 for today's single-process servers) and the low 48 bits a per-node monotonic
/// counter. See <see cref="NetIdAllocator"/> for allocation and the <see cref="NetIdAllocator.Pack"/> /
/// <see cref="NetIdAllocator.NodeOf"/> / <see cref="NetIdAllocator.CounterOf"/> helpers. A single-process server runs
/// entirely in node 0, so its ids are numerically identical to the old counter (id 1, 2, 3, …); the extra width is
/// what lets a future multi-process layer allocate collision-free without recycling (2^48 ids per node outlive any
/// server). The id is written to the wire and to persisted blobs as a 64-bit value; a pre-10.0.0 (32-bit) peer or save
/// is incompatible and is rejected by the ProtocolVersion handshake / migrated forward by the cell-blob migration
/// chain (see <c>KhaozEngine.NetWorld</c>).
/// </remarks>
public struct NetId : IComponent
{
    public NetId(long value) { Value = value; }

    /// <summary>The wire-stable id (64-bit; node id in the high 16 bits, per-node counter in the low 48).</summary>
    public long Value;

    /// <summary>The node/allocator id packed into this id's high 16 bits (0 for a single-process server).</summary>
    public readonly ushort Node => NetIdAllocator.NodeOf(Value);

    /// <summary>The per-node counter packed into this id's low 48 bits.</summary>
    public readonly long Counter => NetIdAllocator.CounterOf(Value);
}
