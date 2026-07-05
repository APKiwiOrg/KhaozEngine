using System;

namespace KhaozEngine.Replication;

/// <summary>
/// The single place the engine allocates 64-bit <see cref="NetId"/> values, replacing the raw <c>++int</c> counter
/// the servers used before 10.0.0. Allocation follows a documented node-prefix scheme: an id is
/// <c>(node &lt;&lt; 48) | counter</c> - the high 16 bits a node/allocator id, the low 48 bits a per-node monotonic
/// counter that starts at 1 and is never recycled. A single-process server runs in node 0, so its ids are numerically
/// identical to the old counter (1, 2, 3, …) and its behaviour is unchanged; a future multi-process layer gives each
/// node a distinct prefix, so two nodes can allocate concurrently without ever colliding and without needing to recycle
/// ids (2^48 per node outlives any server). Not thread-safe: call <see cref="Next"/> from the owning server thread.
/// </summary>
public sealed class NetIdAllocator
{
    /// <summary>Bits reserved for the per-node counter (the low bits of a packed id).</summary>
    public const int CounterBits = 48;

    /// <summary>Bits reserved for the node/allocator id (the high bits of a packed id).</summary>
    public const int NodeBits = 64 - CounterBits; // 16

    /// <summary>Mask selecting the counter portion (low <see cref="CounterBits"/> bits) of a packed id.</summary>
    public const long CounterMask = (1L << CounterBits) - 1; // 0x0000_FFFF_FFFF_FFFF

    /// <summary>The largest per-node counter (2^48 - 1). Allocation past this throws rather than wrapping.</summary>
    public const long MaxCounter = CounterMask;

    /// <summary>The largest node/allocator id (2^16 - 1 = 65535).</summary>
    public const int MaxNodeId = (1 << NodeBits) - 1;

    private long nextCounter; // counter of the NEXT id this node will hand out (>= 1)

    /// <param name="nodeId">This allocator's node id (0 for a single-process server). Must be &lt;= <see cref="MaxNodeId"/>.</param>
    /// <param name="startCounter">The first counter value handed out. Defaults to 1 (matching the pre-10.0.0 first id);
    /// must be &gt;= 1 and &lt;= <see cref="MaxCounter"/>.</param>
    public NetIdAllocator(ushort nodeId = 0, long startCounter = 1)
    {
        if (nodeId > MaxNodeId) // ushort can't exceed 65535, but keep the invariant explicit and symmetric.
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, $"Node id must be <= {MaxNodeId}.");
        if (startCounter < 1 || startCounter > MaxCounter)
            throw new ArgumentOutOfRangeException(nameof(startCounter), startCounter, $"Start counter must be in [1, {MaxCounter}].");
        NodeId = nodeId;
        nextCounter = startCounter;
    }

    /// <summary>This allocator's node id (the high 16 bits stamped into every id it hands out).</summary>
    public ushort NodeId { get; }

    /// <summary>Packs a node id and per-node counter into a 64-bit id: <c>(nodeId &lt;&lt; 48) | (counter &amp; mask)</c>.</summary>
    public static long Pack(ushort nodeId, long counter) => ((long)nodeId << CounterBits) | (counter & CounterMask);

    /// <summary>Extracts the node/allocator id (high 16 bits) from a packed id.</summary>
    public static ushort NodeOf(long value) => (ushort)((ulong)value >> CounterBits);

    /// <summary>Extracts the per-node counter (low 48 bits) from a packed id.</summary>
    public static long CounterOf(long value) => value & CounterMask;

    /// <summary>The next id this allocator will hand out - the packed high-water mark (one past the highest allocated).
    /// Persisted per node so the allocator resumes above every id ever handed out after a restart.</summary>
    public long NextValue => Pack(NodeId, nextCounter);

    /// <summary>Allocates the next id for this node and advances the counter. Throws once the node's 2^48 counter space
    /// is exhausted (unreachable in practice) rather than wrapping into another node's id space.</summary>
    public NetId Next()
    {
        if (nextCounter > MaxCounter)
            throw new InvalidOperationException($"NetId counter for node {NodeId} is exhausted (2^48 ids allocated).");
        long value = Pack(NodeId, nextCounter);
        nextCounter++;
        return new NetId(value);
    }

    /// <summary>
    /// Raises this node's counter so the next id handed out is at least <paramref name="atLeastNextValue"/> (a packed
    /// id, one past a restored high-water). Never lowers it. An id whose node bits are NOT this allocator's node is
    /// ignored: its counter space is separate, so it can't advance this node (relevant only to a future multi-node
    /// deployment; today everything is node 0). Used on restart to resume above persisted/restored ids so a fresh spawn
    /// can never collide with a restored one.
    /// </summary>
    public void EnsureNextAtLeast(long atLeastNextValue)
    {
        if (NodeOf(atLeastNextValue) != NodeId) return; // a different node's high-water: not ours to advance
        long counter = CounterOf(atLeastNextValue);
        if (counter > nextCounter) nextCounter = counter;
    }
}
