using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Puts the chunks of a <see cref="TileFragmentedMessage"/> back together for ONE peer, and hands the assembled
/// bytes back to the caller to decode.
/// <para>ONE REASSEMBLER PER CONNECTION SLOT is the holding shape, and the type holds no connection table of its
/// own: a server keeps an array or a map of these beside its session table and forwards each peer's chunks to that
/// peer's reassembler, exactly as it already routes a game message to a session. Mixing two peers' chunks in one
/// instance would let either of them evict the other's assemblies, which is why <see cref="Slot"/> is carried as
/// identity and <see cref="DropConnection"/> refuses a slot that is not this one.</para>
/// <para>The four rules, which are the design's, in order:</para>
/// <list type="number">
/// <item>A chunk whose <c>Sequence</c> differs from the assembly in progress for its stream DISCARDS that assembly
/// and starts a new one. That is what a server restarting a page mid transmission looks like, it is not an error,
/// and it is not counted as an eviction.</item>
/// <item>At most <see cref="MaxPartialAssemblies"/> partial assemblies are held at once. A fifth evicts the one
/// fed longest ago and increments <see cref="EvictedAssemblies"/>. A bounded memory rule rather than a timer,
/// because a timer on a reliable ordered channel measures nothing, and this type holds no clock at all. The
/// bound is a hard constant with no constructor knob, so a host with more concurrently fragmented streams per
/// peer than that evicts SILENTLY, and <see cref="EvictedAssemblies"/> is the only thing that reports it.</item>
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
/// <para>NOT REORDERING. The channel is <c>ReliableOrdered</c>, so a chunk cannot arrive out of order or be lost
/// without the connection failing. What this type checks is CONSISTENCY, and a chunk that is not the next one
/// expected is refused rather than buffered.</para>
/// <para>It never throws. Every refusal is a false plus one of the reason tokens below, on the rule every frame
/// decoder in <see cref="TileProtocol"/> follows. Memory is bounded by what the peer actually sent: a buffer grows
/// with the bytes that arrive rather than with the chunk count a header claims, so a lying <c>ChunkCount</c> buys
/// nothing, and the ceiling is <see cref="MaxPartialAssemblies"/> times
/// <see cref="TileFragmentedMessage.MaxPayloadBytes"/>, about one megabyte, for a peer that really did send
/// that.</para>
/// </summary>
public sealed class TileFragmentReassembler
{
    /// <summary>How many partial assemblies are held at once before the least recently fed one is evicted.</summary>
    public const int MaxPartialAssemblies = 4;

    /// <summary>A chunk this format never produces: too short to hold a header, a chunk count of zero, an index at
    /// or past the count, more bytes than a chunk can carry, a non final chunk that is not full, or a final chunk
    /// of a multi chunk transmission carrying nothing. Prefixed <c>ke:</c> like every other engine wire token, so a
    /// game routing these through the same counter as its own can never collide with one.</summary>
    public const string MalformedChunk = "ke:fragment-malformed";

    /// <summary>A well formed chunk that is not the one expected next: an index other than 0 with no assembly in
    /// progress for its stream, an index that skips one, or a chunk count that changed mid assembly. The assembly
    /// in progress is discarded with it, because a stream that contradicts itself has nothing worth keeping.</summary>
    public const string OutOfSequenceChunk = "ke:fragment-out-of-sequence";

    readonly Partial?[] partials = new Partial?[MaxPartialAssemblies];
    long fed;

    /// <summary>The connection slot these assemblies belong to. Identity, not a table: this instance holds the
    /// partial assemblies of one peer and knows nothing about any other.</summary>
    public int Slot { get; }

    /// <summary>How many partial assemblies have been thrown away to keep the count at
    /// <see cref="MaxPartialAssemblies"/>. Counts rule 2 ONLY: a stream restarting at a new sequence replaces its
    /// own assembly and is not an eviction, because it is not memory pressure.</summary>
    public int EvictedAssemblies { get; private set; }

    /// <summary>How many partial assemblies are open right now, at most <see cref="MaxPartialAssemblies"/>. A
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

    /// <summary>Builds a reassembler for one connection slot.</summary>
    public TileFragmentReassembler(int slot) => Slot = slot;

    /// <summary>
    /// Feeds one chunk, which is the payload of a game message the caller has already unwrapped with
    /// <see cref="TileProtocol.TryDecodeGameMessage"/>.
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
    /// <see cref="TileFragmentedMessage.TryReadChunk"/> reads off every one, so a game that sends several streams
    /// under one message kind routes the assembled bytes by the header rather than repeating the stream inside its
    /// payload. Set whenever the answer is true, and not to be read on a false.</para>
    /// </summary>
    public bool TryComplete(ReadOnlySpan<byte> chunk, out byte streamId, out ReadOnlyMemory<byte> assembled,
        out string? reason)
    {
        assembled = default;
        reason = null;
        if (!TileFragmentedMessage.TryReadChunk(chunk, out streamId, out ushort sequence, out int index,
                out int count, out ReadOnlySpan<byte> bytes))
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
            // A single chunk transmission is complete on arrival, so it costs no state at all.
            if (count == 1)
            {
                assembled = bytes.ToArray();
                return true;
            }
            held = Free();
            partials[held] = new Partial(streamId, sequence, count);
        }
        else if (index != partials[held]!.NextIndex || count != partials[held]!.ChunkCount)
        {
            partials[held] = null;
            reason = OutOfSequenceChunk;
            return false;
        }

        Partial partial = partials[held]!;
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
    // first started, because a long page interleaved with short ones is the live stream, not the stale one, and
    // the chunk counter is the only ordering this type has.
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
        byte[] buffer;
        int written;

        public Partial(byte streamId, ushort sequence, int chunkCount)
        {
            StreamId = streamId;
            Sequence = sequence;
            ChunkCount = chunkCount;
            buffer = new byte[TileFragmentedMessage.MaxChunkPayloadBytes];
        }

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
                // Doubling, capped at what the declared chunk count can actually hold, so a 255 chunk page costs
                // eight copies rather than 255 and a peer that lies about the count still only gets the memory it
                // paid for in bytes.
                int grown = Math.Min(Math.Max(buffer.Length * 2, needed),
                    ChunkCount * TileFragmentedMessage.MaxChunkPayloadBytes);
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
