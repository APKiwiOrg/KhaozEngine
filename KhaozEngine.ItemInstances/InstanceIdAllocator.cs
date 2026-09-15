using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The durable name an owned item carries: <c>(node &lt;&lt; 48) | counter</c>, 16 node bits and 48
/// counter bits, the counter starting at 1, never recycled, throwing rather than wrapping, with the
/// packed high-water mark persisted per node through <see cref="IInstanceIdStore"/>.
/// <para>
/// <b>The scheme is <c>NetIdAllocator</c>'s and the counter is not.</b> Contracts 6.2 and spec 3.6 both
/// say an instance id reuses the packing behind that type with its OWN persisted high-water mark, because
/// a net id and an instance id are different spaces that must never share a counter.
/// <c>NetIdAllocator</c> lives in <c>KhaozEngine.Replication</c>, which is a Server package, and this
/// package is Foundation and cannot reference it, so the four constants and the
/// <see cref="Pack"/>, <see cref="NodeOf"/> and <see cref="CounterOf"/> arithmetic are MIRRORED from
/// <c>KhaozEngine.Replication/NetIdAllocator.cs:14-70</c>. The two producing identical packed values for
/// identical inputs is pinned by a test in <c>KhaozEngine.Server.Tests</c>, which is the one project that
/// already references both.
/// </para>
/// <para>
/// Not thread-safe, exactly like the type it mirrors: one owner, one allocator, called from the thread
/// that owns the container being committed.
/// </para>
/// </summary>
public sealed class InstanceIdAllocator
{
    /// <summary>Bits reserved for the per-node counter (the low bits of a packed id).</summary>
    public const int CounterBits = 48;

    /// <summary>Bits reserved for the node id (the high bits of a packed id).</summary>
    public const int NodeBits = 64 - CounterBits; // 16

    /// <summary>Mask selecting the counter portion (low <see cref="CounterBits"/> bits) of a packed id.</summary>
    public const long CounterMask = (1L << CounterBits) - 1; // 0x0000_FFFF_FFFF_FFFF

    /// <summary>The largest per-node counter (2^48 - 1). It is the EXHAUSTED marker rather than an
    /// issuable counter: the high-water mark is exclusive, so the last id a node hands out carries
    /// <c>MaxCounter - 1</c> and a reservation that cannot advance past this throws.</summary>
    public const long MaxCounter = CounterMask;

    /// <summary>The largest node id (2^16 - 1 = 65535).</summary>
    public const int MaxNodeId = (1 << NodeBits) - 1;

    /// <summary>How many ids one durable reservation covers. The batch size is free and the ORDER is the
    /// contract, so this number may move and the persist-before-issue rule may not.</summary>
    public const int ReservationBlock = 4096;

    readonly IInstanceIdStore _store;
    readonly long _liveStoreEpoch;
    readonly bool _epochMatches;
    ushort[] _retired;
    long _nextCounter;
    long _reservedThrough; // exclusive: the counter one past the last one this process may issue

    /// <summary>Boots an allocator over a host's durable store.</summary>
    /// <param name="store">The host's durable record. Read once here and written once per block.</param>
    /// <param name="liveStoreEpoch">The journal store's CURRENT epoch. When the persisted state names a
    /// different one, every issue is refused until an operator rotates.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The persisted node id is already on the retired list,
    /// which is an operator rotating onto a node this store has already used.</exception>
    public InstanceIdAllocator(IInstanceIdStore store, long liveStoreEpoch)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _liveStoreEpoch = liveStoreEpoch;

        InstanceIdState state = store.Read();
        _retired = state.RetiredNodes.ToArray();
        NodeId = state.NodeId;
        if (Array.IndexOf(_retired, NodeId) >= 0)
            throw new InvalidOperationException(
                $"Instance id node {NodeId} is already retired in this store, so booting on it would reissue ids it " +
                "has handed out. Rotate to a node id this store has never used.");

        // A store that has never been written carries no mark, so it has no epoch to disagree with: the
        // epoch BINDS the high-water mark, and a packed mark is never 0 once one exists (the counter
        // starts at 1, so Pack answers at least 1 for every node).
        _epochMatches = state.PackedHighWater == 0 || state.StoreEpoch == liveStoreEpoch;
        long persisted = CounterOf(state.PackedHighWater);
        _nextCounter = persisted < 1 ? 1 : persisted;

        // Nothing is reserved at boot, whatever the previous process reserved: the unissued remainder of
        // its block is SKIPPED, which is free at 2^48 per node and is what makes a reissue impossible.
        _reservedThrough = _nextCounter;
    }

    /// <summary>This allocator's node id, stamped into the high 16 bits of every id it hands out.</summary>
    public ushort NodeId { get; private set; }

    /// <summary>Whether the persisted state's store epoch is the live one. False means a point-in-time
    /// restore happened and nobody rotated, so no id may be issued.</summary>
    public bool CanIssue => _epochMatches;

    /// <summary>The next id this allocator will hand out, packed. Not yet reserved: reading it never
    /// persists anything.</summary>
    public long NextValue => Pack(NodeId, _nextCounter);

    /// <summary>Packs a node id and per-node counter into a 64 bit id:
    /// <c>(nodeId &lt;&lt; 48) | (counter &amp; mask)</c>.</summary>
    /// <param name="nodeId">The node id.</param>
    /// <param name="counter">The per-node counter.</param>
    public static long Pack(ushort nodeId, long counter) => ((long)nodeId << CounterBits) | (counter & CounterMask);

    /// <summary>Extracts the node id (high 16 bits) from a packed id.</summary>
    /// <param name="value">A packed id.</param>
    public static ushort NodeOf(long value) => (ushort)((ulong)value >> CounterBits);

    /// <summary>Extracts the per-node counter (low 48 bits) from a packed id.</summary>
    /// <param name="value">A packed id.</param>
    public static long CounterOf(long value) => value & CounterMask;

    /// <summary>
    /// Allocates the next id for this node, reserving a durable block first when the current one is
    /// spent. The persist happens BEFORE the first id in the block is returned, always.
    /// </summary>
    /// <exception cref="InvalidOperationException">The persisted store epoch is not the live one, or this
    /// node's 2^48 counter space is spent.</exception>
    public long Next()
    {
        RefuseOnStaleEpoch();
        if (_nextCounter >= _reservedThrough) Reserve();
        long value = Pack(NodeId, _nextCounter);
        _nextCounter++;
        return value;
    }

    /// <summary>
    /// Moves this store onto a new node id, retiring the old one. Every id issued afterwards carries the
    /// new prefix, so nothing in the old node's range can be handed out twice, and a later boot on the
    /// retired id throws.
    /// </summary>
    /// <param name="newNodeId">The node id to move to. It must be one this store has never used.</param>
    /// <exception cref="ArgumentException"><paramref name="newNodeId"/> is the current node or is already
    /// retired.</exception>
    /// <exception cref="InvalidOperationException">The persisted store epoch is not the live one. A
    /// rotation writes durable state, and writing it against restored data is the thing the epoch refusal
    /// exists to stop.</exception>
    public void Rotate(ushort newNodeId)
    {
        RefuseOnStaleEpoch();
        if (newNodeId == NodeId)
            throw new ArgumentException($"Node {newNodeId} is already this allocator's node.", nameof(newNodeId));
        if (Array.IndexOf(_retired, newNodeId) >= 0)
            throw new ArgumentException($"Node {newNodeId} is already retired in this store.", nameof(newNodeId));

        ushort[] retired = new ushort[_retired.Length + 1];
        _retired.CopyTo(retired, 0);
        retired[^1] = NodeId;
        _retired = retired;
        NodeId = newNodeId;
        _nextCounter = 1;
        _reservedThrough = 1;
        _store.Persist(new InstanceIdState(Pack(NodeId, _nextCounter), NodeId, _liveStoreEpoch, _retired));
    }

    /// <summary>Writes an id as an UNSIGNED varint over the int64 bit pattern, never zig-zagged
    /// (contracts 15). Returns the bytes written, at most ten.</summary>
    /// <param name="destination">Where to write.</param>
    /// <param name="instanceId">The packed id.</param>
    /// <remarks>
    /// The sign is why this method exists rather than a bare call at each site. <c>Pack(65535, counter)</c>
    /// sets the high bit and is a NEGATIVE <see cref="long"/>, so the unsigned and the zig-zag encodings of
    /// the same id are DIFFERENT bytes and the format has to say which it means. Contracts 15 declares
    /// instance ids unsigned, so this is the unsigned one, written through one method rather than through a
    /// cast at each call site.
    /// <para>
    /// What that costs is the high node and what it buys is node 0. <c>Pack(65535, 1)</c> is ten bytes
    /// unsigned and seven zig-zagged, because a zig-zag folds the sign into the low bit. Node 0, the only
    /// shape a single-process server has and the common case everywhere else, keeps ids numerically
    /// identical to a plain counter and costs four bytes up to 268,435,455, where a zig-zag would double
    /// the value and cost five.
    /// </para>
    /// </remarks>
    public static int WriteId(Span<byte> destination, long instanceId) =>
        ContentVarint.WriteUInt64(destination, (ulong)instanceId);

    /// <summary>The bytes <see cref="WriteId"/> would take, so a caller can size a page without writing.</summary>
    /// <param name="instanceId">The packed id.</param>
    public static int SizeOf(long instanceId) => ContentVarint.SizeUInt64((ulong)instanceId);

    /// <summary>Reads an id written by <see cref="WriteId"/>, advancing the offset past it. Total: a
    /// malformed varint answers false with a reason rather than throwing.</summary>
    /// <param name="source">The bytes.</param>
    /// <param name="offset">Where to read, advanced past the id on success and left alone on failure.</param>
    /// <param name="instanceId">The packed id.</param>
    /// <param name="reason">The failure reason, null on success.</param>
    public static bool TryReadId(ReadOnlySpan<byte> source, ref int offset, out long instanceId, out string? reason)
    {
        if (!ContentVarint.TryReadUInt64(source, ref offset, out ulong raw, out reason))
        {
            instanceId = 0;
            return false;
        }

        instanceId = (long)raw;
        return true;
    }

    /// <summary>
    /// Contracts 6.2's which-items-get-an-id rule, verbatim and PURE, so a container commit path can ask
    /// it per item without touching an allocator: an item gets an instance id when its encoded payload is
    /// NON-EMPTY, or its definition declares durability, sockets or any per-instance field.
    /// </summary>
    /// <param name="encodedPayloadLength">The length of the item's canonical encoded payload, in bytes.
    /// Zero for a plain stack.</param>
    /// <param name="declared">What the item's definition declares per instance.</param>
    /// <remarks>
    /// The rule is a property of the ITEM rather than of the definition, which is why the payload length
    /// comes first and is enough on its own. A definition gaining a per-instance property later does not
    /// reach back into every stored copy: the copy is asked about the bytes it actually carries.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encodedPayloadLength"/> is negative.</exception>
    public static bool NeedsInstanceId(int encodedPayloadLength, DeclaredInstanceProperties declared)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(encodedPayloadLength);
        return encodedPayloadLength > 0 || declared != DeclaredInstanceProperties.None;
    }

    void Reserve()
    {
        long target = _nextCounter + ReservationBlock;
        if (target > MaxCounter) target = MaxCounter;
        if (target <= _nextCounter)
            throw new InvalidOperationException(
                $"Instance id counter for node {NodeId} is exhausted (2^48 ids allocated). Rotate to a fresh node id.");

        _store.Persist(new InstanceIdState(Pack(NodeId, target), NodeId, _liveStoreEpoch, _retired));
        _reservedThrough = target;
    }

    void RefuseOnStaleEpoch()
    {
        if (_epochMatches) return;
        throw new InvalidOperationException(
            $"The instance id allocator's persisted store epoch is not the live one ({_liveStoreEpoch}), so this " +
            "store was restored to a point in time and no id may be issued against it. A point-in-time restore " +
            "must rotate the store epoch through IMutationJournalMaintenance.RotateStoreEpochAsync before writers " +
            "reopen, which is DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md section 10.");
    }
}
