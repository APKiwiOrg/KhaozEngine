using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// Puts the chunks of a <see cref="MessageFragmenter"/> transmission back together for ONE peer at ONE chunk
/// payload width, and hands the assembled bytes back to the caller to decode.
/// <para>ONE REASSEMBLER PER CONNECTION SLOT is the holding shape, and the type holds no connection table of its
/// own: a server keeps an array or a map of these beside its session table and forwards each peer's chunks to that
/// peer's reassembler, exactly as it already routes a message to a session. Mixing two peers' chunks in one
/// instance would let either of them evict the other's assemblies, which is why <see cref="Slot"/> is carried as
/// identity and <see cref="DropConnection"/> refuses a slot that is not this one.</para>
/// <para><see cref="ChunkPayloadBytes"/> is fixed at construction and must be the width the sender fragments
/// with. A non final chunk of any other length is refused as <see cref="MalformedChunk"/>, so a width mismatch
/// between the two ends fails on the first chunk rather than assembling the wrong bytes.</para>
/// <para>The four rules, in order:</para>
/// <list type="number">
/// <item>A chunk whose <c>Sequence</c> differs from the assembly in progress for its stream DISCARDS that assembly
/// and starts a new one. That is what a sender restarting a transmission mid way looks like, it is not an error,
/// and it is not counted as an eviction.</item>
/// <item>At most <see cref="PartialAssemblyLimit"/> partial assemblies are held at once, which is
/// <see cref="MaxPartialAssemblies"/> unless the bounded constructor set it. One more evicts the one fed longest
/// ago and increments <see cref="EvictedAssemblies"/>. A bounded memory rule rather than a timer, because a timer
/// on a reliable ordered channel measures nothing, and this type holds no clock at all. A host with more
/// concurrently fragmented streams per peer than the limit evicts SILENTLY, and <see cref="EvictedAssemblies"/>
/// is the only thing that reports it.</item>
/// <item>On the last chunk the assembled bytes are handed BACK through
/// <see cref="TryComplete(ReadOnlySpan{byte}, out byte, out ReadOnlyMemory{byte}, out string?)"/>, with the
/// stream id the chunk headers carried. Nothing here decodes them, so a payload that fails to decode is the
/// caller's quarantine rather than a throw from the wire. A caller with several streams on one message kind routes
/// the bytes by that id rather than repeating the stream inside its own payload, where the two could
/// disagree.</item>
/// <item>A partial assembly still open when the connection drops is discarded through an explicit
/// <see cref="DropConnection"/> the server calls from its own disconnect path. Nothing here holds a timer or a
/// background task.</item>
/// </list>
/// <para>NOT REORDERING. The channel is reliable ordered, so a chunk cannot arrive out of order or be lost
/// without the connection failing. What this type checks is CONSISTENCY, and a chunk that is not the next one
/// expected is refused rather than buffered.</para>
/// <para>It never throws on a chunk. Every refusal is a false plus one of the reason tokens below, on the rule
/// every frame decoder follows. Memory is bounded by what the peer actually sent: a buffer grows with the bytes
/// that arrive rather than with the chunk count a header claims, so a lying <c>ChunkCount</c> buys nothing, and
/// the ceiling is <see cref="PartialAssemblyLimit"/> times <see cref="MaxAssembledBytes"/>, for a peer that
/// really did send that.</para>
/// <para>ASSEMBLED-BYTE LIMIT. <see cref="MaxAssembledBytes"/> caps one transmission and is
/// <see cref="MessageFragmenter.MaxPayloadBytes(int)"/> at <see cref="ChunkPayloadBytes"/> unless the bounded
/// constructor set it lower, so the two-argument constructor never refuses on it. A first chunk whose declared
/// count cannot fit the limit even with a one-byte final chunk, a single chunk transmission longer than the
/// limit, and a chunk that would carry an assembly past the limit are each refused as
/// <see cref="PayloadLimitExceeded"/> BEFORE anything is copied, and the assembly is discarded. A backing buffer
/// never grows past the limit, spare capacity included.</para>
/// </summary>
public sealed class MessageReassembler
{
    /// <summary>The DEFAULT partial-assembly limit, the <see cref="PartialAssemblyLimit"/> the two-argument
    /// constructor uses. The bounded constructor takes its own.</summary>
    public const int MaxPartialAssemblies = 4;

    // One partial per stream id is the most that can ever be occupied, because a stream holds at most one.
    const int MostPartialAssemblies = byte.MaxValue + 1;

    /// <summary>A chunk this format never produces at this width: too short to hold a header, a chunk count of
    /// zero, an index at or past the count, more bytes than a chunk can carry, a non final chunk that is not full,
    /// or a final chunk of a multi chunk transmission carrying nothing. Prefixed <c>ke:</c> like every other engine
    /// wire token, so a game routing these through the same counter as its own can never collide with one.</summary>
    public const string MalformedChunk = "ke:fragment-malformed";

    /// <summary>A well formed chunk that is not the one expected next: an index other than 0 with no assembly in
    /// progress for its stream, an index that skips one, or a chunk count that changed mid assembly. The assembly
    /// in progress is discarded with it, because a stream that contradicts itself has nothing worth keeping.</summary>
    public const string OutOfSequenceChunk = "ke:fragment-out-of-sequence";

    /// <summary>A well formed chunk whose transmission cannot fit <see cref="MaxAssembledBytes"/>: a first chunk
    /// whose declared count needs more even with a one-byte final chunk, a single chunk transmission longer than the
    /// limit, or a chunk that would carry the assembly past it. Refused before the bytes are copied, and the
    /// assembly in progress for its stream is discarded with it.</summary>
    public const string PayloadLimitExceeded = "ke:fragment-payload-limit";

    readonly Partial?[] partials;
    long fed;

    /// <summary>The connection slot these assemblies belong to. Identity, not a table: this instance holds the
    /// partial assemblies of one peer and knows nothing about any other.</summary>
    public int Slot { get; }

    /// <summary>The chunk payload width this reassembler reads at, the body a full chunk carries beside
    /// <see cref="MessageFragmenter.HeaderBytes"/>. Fixed at construction.</summary>
    public int ChunkPayloadBytes { get; }

    /// <summary>The most bytes one assembled transmission may hold, fixed at construction.
    /// <see cref="MessageFragmenter.MaxPayloadBytes(int)"/> at <see cref="ChunkPayloadBytes"/> for the two-argument
    /// constructor. Anything longer is refused as <see cref="PayloadLimitExceeded"/>.</summary>
    public int MaxAssembledBytes { get; }

    /// <summary>How many partial assemblies are held at once before the least recently fed one is evicted, fixed at
    /// construction. <see cref="MaxPartialAssemblies"/> for the two-argument constructor.</summary>
    public int PartialAssemblyLimit { get; }

    /// <summary>How many partial assemblies have been thrown away to keep the count at
    /// <see cref="PartialAssemblyLimit"/>. Counts rule 2 ONLY: a stream restarting at a new sequence replaces its
    /// own assembly and is not an eviction, because it is not memory pressure.</summary>
    public int EvictedAssemblies { get; private set; }

    /// <summary>How many partial assemblies are open right now, at most <see cref="PartialAssemblyLimit"/>. A
    /// single chunk transmission completes on arrival and never occupies one.</summary>
    public int PartialAssemblyCount
    {
        get
        {
            int open = 0;
            for (int i = 0; i < partials.Length; i++) if (partials[i] != null) open++;
            return open;
        }
    }

    /// <summary>Builds a reassembler for one connection slot at the width the sender fragments with, holding up to
    /// <see cref="MaxPartialAssemblies"/> partial assemblies of up to
    /// <see cref="MessageFragmenter.MaxPayloadBytes(int)"/> bytes each, the most the format carries at that
    /// width.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkPayloadBytes"/> is outside <c>1</c> to
    /// <see cref="ushort.MaxValue"/>.</exception>
    public MessageReassembler(int slot, int chunkPayloadBytes)
        : this(slot, chunkPayloadBytes, MessageFragmenter.MaxPayloadBytes(chunkPayloadBytes), MaxPartialAssemblies)
    {
    }

    /// <summary>
    /// Builds a BOUNDED reassembler for one connection slot at the width the sender fragments with. One assembled
    /// transmission holds at most <paramref name="maxAssembledBytes"/>, and at most
    /// <paramref name="maxPartialAssemblies"/> are open at once. A transmission that cannot fit is refused as
    /// <see cref="PayloadLimitExceeded"/> before its excess bytes are copied, and no backing buffer grows past the
    /// limit. The chunk wire is the same as for the two-argument constructor, so both ends still agree only on the
    /// width.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkPayloadBytes"/> is outside <c>1</c> to
    /// <see cref="ushort.MaxValue"/>, <paramref name="maxAssembledBytes"/> is outside <c>1</c> to
    /// <see cref="MessageFragmenter.MaxPayloadBytes(int)"/> at that width, or
    /// <paramref name="maxPartialAssemblies"/> is outside <c>1</c> to <c>256</c>, one per possible stream
    /// id.</exception>
    public MessageReassembler(int slot, int chunkPayloadBytes, int maxAssembledBytes, int maxPartialAssemblies)
    {
        int formatCap = MessageFragmenter.MaxPayloadBytes(chunkPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAssembledBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAssembledBytes, formatCap);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPartialAssemblies, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPartialAssemblies, MostPartialAssemblies);
        Slot = slot;
        ChunkPayloadBytes = chunkPayloadBytes;
        MaxAssembledBytes = maxAssembledBytes;
        PartialAssemblyLimit = maxPartialAssemblies;
        partials = new Partial?[maxPartialAssemblies];
    }

    /// <summary>
    /// Feeds one chunk, which is the payload of a message the caller has already unwrapped from its envelope.
    /// <para>True when this chunk COMPLETED a transmission, with <paramref name="assembled"/> holding the whole
    /// logical payload and <paramref name="reason"/> null. False otherwise, and then the two outs say which
    /// kind of false it is: a null <paramref name="reason"/> means the chunk was accepted and more are expected, a
    /// non null one means the chunk was REFUSED and names why.</para>
    /// <para><paramref name="assembled"/> is the assembly's own buffer, which this type drops on the way out, so
    /// the caller owns it and nothing here writes to it again.</para>
    /// <para>The stream the bytes belong to is not handed back by this overload. A caller with more than one
    /// fragmented stream on a message kind uses
    /// <see cref="TryComplete(ReadOnlySpan{byte}, out byte, out ReadOnlyMemory{byte}, out string?)"/>, which this
    /// one delegates to.</para>
    /// </summary>
    public bool TryComplete(ReadOnlySpan<byte> chunk, out ReadOnlyMemory<byte> assembled, out string? reason) =>
        TryComplete(chunk, out _, out assembled, out reason);

    /// <summary>
    /// Feeds one chunk and, when it completes a transmission, hands back WHICH stream it completed. Every answer
    /// is <see cref="TryComplete(ReadOnlySpan{byte}, out ReadOnlyMemory{byte}, out string?)"/>'s, which delegates
    /// here.
    /// <para><paramref name="streamId"/> is the <c>StreamId</c> the chunk headers carried, the same byte
    /// <see cref="MessageFragmenter.TryReadChunk"/> reads off every one, so a caller that sends several streams
    /// under one message kind routes the assembled bytes by the header rather than repeating the stream inside its
    /// payload. Set whenever the answer is true, and not to be read on a false.</para>
    /// </summary>
    public bool TryComplete(ReadOnlySpan<byte> chunk, out byte streamId, out ReadOnlyMemory<byte> assembled,
        out string? reason)
    {
        assembled = default;
        reason = null;
        if (!MessageFragmenter.TryReadChunk(chunk, ChunkPayloadBytes, out streamId, out ushort sequence,
                out int index, out int count, out ReadOnlySpan<byte> bytes))
        {
            reason = MalformedChunk;
            return false;
        }

        int held = Find(streamId);
        // Rule 1: a new sequence on a stream means the sender restarted it, so whatever was in progress is dead.
        if (held >= 0 && partials[held]!.Sequence != sequence)
        {
            partials[held] = null;
            held = -1;
        }

        if (held < 0)
        {
            if (index != 0)
            {
                reason = OutOfSequenceChunk;
                return false;
            }
            // The least a transmission of this count can hold is full chunks then a one-byte tail, or the one chunk
            // itself. Refusing here, before any state exists, is what keeps a lying count from buying a buffer.
            long least = count == 1 ? bytes.Length : (long)(count - 1) * ChunkPayloadBytes + 1;
            if (least > MaxAssembledBytes)
            {
                reason = PayloadLimitExceeded;
                return false;
            }
            // A single chunk transmission is complete on arrival, so it costs no state at all.
            if (count == 1)
            {
                assembled = bytes.ToArray();
                return true;
            }
            held = Free();
            partials[held] = new Partial(streamId, sequence, count, ChunkPayloadBytes, MaxAssembledBytes);
        }
        else if (index != partials[held]!.NextIndex || count != partials[held]!.ChunkCount)
        {
            partials[held] = null;
            reason = OutOfSequenceChunk;
            return false;
        }

        Partial partial = partials[held]!;
        // A full count can still carry more than the limit, because the final chunk may be up to a full width.
        // Checked before the copy, so the buffer never holds or grows toward the excess.
        if (partial.Written + bytes.Length > MaxAssembledBytes)
        {
            partials[held] = null;
            reason = PayloadLimitExceeded;
            return false;
        }
        partial.Append(bytes, ++fed);
        if (partial.NextIndex < count) return false;

        assembled = partial.Assembled();
        partials[held] = null;
        return true;
    }

    /// <summary>
    /// Rule 4. Discards every partial assembly held for <paramref name="slot"/>, which the server calls from the
    /// same place it tears the session down. True when this reassembler discarded something, false when it held
    /// nothing or when <paramref name="slot"/> is not <see cref="Slot"/>. Dropping a slot twice is not an error,
    /// and neither is dropping one that never fragmented anything.
    /// </summary>
    public bool DropConnection(int slot)
    {
        if (slot != Slot) return false;
        bool held = false;
        for (int i = 0; i < partials.Length; i++)
        {
            if (partials[i] == null) continue;
            partials[i] = null;
            held = true;
        }
        return held;
    }

    int Find(byte streamId)
    {
        for (int i = 0; i < partials.Length; i++)
            if (partials[i] is { } partial && partial.StreamId == streamId) return i;
        return -1;
    }

    // A free slot, evicting the least recently fed assembly when there is none. Least recently FED rather than
    // first started, because a long transmission interleaved with short ones is the live stream, not the stale
    // one, and the chunk counter is the only ordering this type has.
    int Free()
    {
        int oldest = 0;
        for (int i = 0; i < partials.Length; i++)
        {
            if (partials[i] == null) return i;
            if (partials[i]!.LastFed < partials[oldest]!.LastFed) oldest = i;
        }
        EvictedAssemblies++;
        partials[oldest] = null;
        return oldest;
    }

    sealed class Partial
    {
        readonly int capacityCap;
        byte[] buffer;
        int written;

        public Partial(byte streamId, ushort sequence, int chunkCount, int chunkPayloadBytes, int maxAssembledBytes)
        {
            StreamId = streamId;
            Sequence = sequence;
            ChunkCount = chunkCount;
            // What the declared count can hold, but never past the assembled-byte limit, so spare capacity is bounded
            // by the same limit the bytes are.
            capacityCap = (int)Math.Min((long)chunkCount * chunkPayloadBytes, maxAssembledBytes);
            buffer = new byte[Math.Min(chunkPayloadBytes, capacityCap)];
        }

        public int Written => written;

        public byte StreamId { get; }

        public ushort Sequence { get; }

        public int ChunkCount { get; }

        public int NextIndex { get; private set; }

        public long LastFed { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes, long tick)
        {
            int needed = written + bytes.Length;
            if (needed > buffer.Length)
            {
                // Doubling, capped at what the declared chunk count can actually hold and at the assembled-byte
                // limit, so a 255 chunk transmission costs eight copies rather than 255 and a peer that lies about
                // the count still only gets the memory it paid for in bytes.
                int grown = (int)Math.Min(Math.Max((long)buffer.Length * 2, needed), capacityCap);
                Array.Resize(ref buffer, grown);
            }
            bytes.CopyTo(buffer.AsSpan(written));
            written = needed;
            NextIndex++;
            LastFed = tick;
        }

        public ReadOnlyMemory<byte> Assembled() => new(buffer, 0, written);
    }
}
