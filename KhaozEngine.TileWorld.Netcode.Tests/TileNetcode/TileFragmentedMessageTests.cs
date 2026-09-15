using System;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileFragmentedMessageTests
{
    const int Slot = 3;

    static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 + (i >> 8) * 7 + 1);
        return bytes;
    }

    [Fact]
    public void A_payload_under_one_chunk_is_one_chunk_and_round_trips()
    {
        byte[] payload = Payload(200);
        byte[][] chunks = TileFragmentedMessage.Fragment(streamId: 1, sequence: 0, payload);
        Assert.Single(chunks);

        var reassembler = new TileFragmentReassembler(Slot);
        Assert.True(reassembler.TryComplete(chunks[0], out ReadOnlyMemory<byte> assembled, out string? reason));
        Assert.Null(reason);
        Assert.Equal(payload, assembled.ToArray());

        // A one-chunk message is complete on arrival, so it never occupies one of the four partial assemblies.
        Assert.Equal(0, reassembler.PartialAssemblyCount);

        // An EMPTY payload is still one chunk, so a caller with nothing to say is not a special case.
        byte[][] empty = TileFragmentedMessage.Fragment(streamId: 1, sequence: 1, ReadOnlySpan<byte>.Empty);
        Assert.Single(empty);
        Assert.True(reassembler.TryComplete(empty[0], out ReadOnlyMemory<byte> none, out reason));
        Assert.Null(reason);
        Assert.Equal(0, none.Length);
    }

    [Fact]
    public void A_page_sized_payload_round_trips_through_every_chunk_in_order()
    {
        // The 100 slot page of rares of spec 3.8, about 6.9 KB, which is what forced a fragmenter in the first
        // place: 6.7 times the game message cap.
        byte[] payload = Payload(6900);
        byte[][] chunks = TileFragmentedMessage.Fragment(streamId: 7, sequence: 42, payload);
        Assert.Equal(7, chunks.Length);

        var reassembler = new TileFragmentReassembler(Slot);
        for (int i = 0; i < chunks.Length - 1; i++)
        {
            Assert.False(reassembler.TryComplete(chunks[i], out _, out string? pending));
            Assert.Null(pending);
            Assert.Equal(1, reassembler.PartialAssemblyCount);
        }

        Assert.True(reassembler.TryComplete(chunks[^1], out ReadOnlyMemory<byte> assembled, out string? reason));
        Assert.Null(reason);
        Assert.Equal(payload, assembled.ToArray());
        Assert.Equal(0, reassembler.PartialAssemblyCount);
        Assert.Equal(0, reassembler.EvictedAssemblies);

        // Every chunk but the last carries a full load, which is what makes a short one detectable as truncation.
        for (int i = 0; i < chunks.Length - 1; i++)
            Assert.Equal(TileFragmentedMessage.HeaderBytes + TileFragmentedMessage.MaxChunkPayloadBytes, chunks[i].Length);
    }

    [Fact]
    public void A_chunk_whose_sequence_differs_mid_assembly_discards_and_restarts()
    {
        // Rule 1. A server restarting a page mid transmission looks exactly like this, and it is not an error.
        byte[] first = Payload(3000);
        byte[] second = Payload(2500);
        byte[][] abandoned = TileFragmentedMessage.Fragment(streamId: 2, sequence: 8, first);
        byte[][] restarted = TileFragmentedMessage.Fragment(streamId: 2, sequence: 9, second);

        var reassembler = new TileFragmentReassembler(Slot);
        Assert.False(reassembler.TryComplete(abandoned[0], out _, out string? pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(abandoned[1], out _, out pending));
        Assert.Null(pending);

        foreach (byte[] chunk in restarted[..^1])
        {
            Assert.False(reassembler.TryComplete(chunk, out _, out pending));
            Assert.Null(pending);
        }
        Assert.True(reassembler.TryComplete(restarted[^1], out ReadOnlyMemory<byte> assembled, out string? reason));
        Assert.Null(reason);
        Assert.Equal(second, assembled.ToArray());

        // The restart replaced the assembly rather than joining the eviction count, which measures a different
        // thing: memory pressure from concurrent streams, not a stream that changed its mind.
        Assert.Equal(0, reassembler.EvictedAssemblies);
        Assert.Equal(0, reassembler.PartialAssemblyCount);

        // A late chunk of the abandoned sequence has nothing to join, so it is refused rather than mixed in.
        Assert.False(reassembler.TryComplete(abandoned[2], out _, out reason));
        Assert.Equal(TileFragmentReassembler.OutOfSequenceChunk, reason);
    }

    [Fact]
    public void A_fifth_concurrent_assembly_evicts_the_oldest_and_counts_it()
    {
        // Rule 2. Bounded memory rather than a timer, because a timer on a reliable ordered channel measures
        // nothing.
        var reassembler = new TileFragmentReassembler(Slot);
        var opened = new byte[6][][];
        for (int stream = 1; stream <= 5; stream++)
        {
            opened[stream] = TileFragmentedMessage.Fragment((byte)stream, sequence: 1, Payload(2500));
            Assert.False(reassembler.TryComplete(opened[stream][0], out _, out string? pending));
            Assert.Null(pending);
        }

        Assert.Equal(TileFragmentReassembler.MaxPartialAssemblies, reassembler.PartialAssemblyCount);
        Assert.Equal(1, reassembler.EvictedAssemblies);

        // Stream 1 was the one fed longest ago, so it is the one that went. Its next chunk has nothing to join.
        Assert.False(reassembler.TryComplete(opened[1][1], out _, out string? reason));
        Assert.Equal(TileFragmentReassembler.OutOfSequenceChunk, reason);

        // The four that survived still complete.
        for (int stream = 2; stream <= 5; stream++)
        {
            Assert.False(reassembler.TryComplete(opened[stream][1], out _, out reason));
            Assert.Null(reason);
            Assert.True(reassembler.TryComplete(opened[stream][2], out ReadOnlyMemory<byte> assembled, out reason));
            Assert.Null(reason);
            Assert.Equal(2500, assembled.Length);
        }
        Assert.Equal(1, reassembler.EvictedAssemblies);
    }

    [Fact]
    public void A_truncated_final_chunk_answers_a_reason_rather_than_throwing()
    {
        byte[] payload = Payload(3000);
        byte[][] chunks = TileFragmentedMessage.Fragment(streamId: 4, sequence: 5, payload);

        // Cut INTO the header, which is the truncation that would index off the end of a span if anything here
        // sliced before it measured.
        var reassembler = new TileFragmentReassembler(Slot);
        Assert.False(reassembler.TryComplete(chunks[0], out _, out string? pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(chunks[1], out _, out pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(chunks[^1].AsSpan(0, 3), out _, out string? reason));
        Assert.Equal(TileFragmentReassembler.MalformedChunk, reason);

        // Cut to the header exactly. A final chunk carrying no bytes at all is a frame the fragmenter never
        // writes, so it is refused rather than completing an assembly a byte short.
        Assert.False(reassembler.TryComplete(chunks[^1].AsSpan(0, TileFragmentedMessage.HeaderBytes), out _, out reason));
        Assert.Equal(TileFragmentReassembler.MalformedChunk, reason);

        // A NON final chunk cut short is detectable the same way, because every chunk but the last is full.
        var fresh = new TileFragmentReassembler(Slot);
        Assert.False(fresh.TryComplete(chunks[0].AsSpan(0, chunks[0].Length - 40), out _, out reason));
        Assert.Equal(TileFragmentReassembler.MalformedChunk, reason);

        // What is NOT detectable here is a final chunk cut in its BODY: the header declares no total length, so
        // those bytes assemble and the CALLER's decoder is what refuses them. Rule 3, and the reason this type
        // hands bytes back rather than decoding them.
        var bodyCut = new TileFragmentReassembler(Slot);
        Assert.False(bodyCut.TryComplete(chunks[0], out _, out reason));
        Assert.False(bodyCut.TryComplete(chunks[1], out _, out reason));
        Assert.True(bodyCut.TryComplete(chunks[^1].AsSpan(0, chunks[^1].Length - 10), out ReadOnlyMemory<byte> assembled, out reason));
        Assert.Null(reason);
        Assert.Equal(payload.Length - 10, assembled.Length);
    }

    [Fact]
    public void A_dropped_connection_discards_every_partial_assembly()
    {
        // Rule 4. An explicit call from the server's own disconnect path, because nothing here holds a timer or a
        // background task that could notice a connection going away on its own.
        var reassembler = new TileFragmentReassembler(Slot);
        byte[][] one = TileFragmentedMessage.Fragment(streamId: 1, sequence: 1, Payload(2500));
        byte[][] two = TileFragmentedMessage.Fragment(streamId: 2, sequence: 1, Payload(2500));
        Assert.False(reassembler.TryComplete(one[0], out _, out string? pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(two[0], out _, out pending));
        Assert.Null(pending);
        Assert.Equal(2, reassembler.PartialAssemblyCount);

        // A drop naming another connection is not this reassembler's, so it changes nothing. One reassembler per
        // connection slot is the server's holding shape, and this is what keeps a mis-wired forward from wiping
        // the wrong peer's assemblies.
        Assert.False(reassembler.DropConnection(Slot + 1));
        Assert.Equal(2, reassembler.PartialAssemblyCount);

        Assert.True(reassembler.DropConnection(Slot));
        Assert.Equal(0, reassembler.PartialAssemblyCount);

        // The reconnecting peer starts clean: a chunk of the dropped assembly has nothing to join.
        Assert.False(reassembler.TryComplete(one[1], out _, out string? reason));
        Assert.Equal(TileFragmentReassembler.OutOfSequenceChunk, reason);

        // Dropping twice is not an error, because a server that drops a slot it already dropped has done nothing
        // wrong.
        Assert.False(reassembler.DropConnection(Slot));
    }

    [Fact]
    public void No_chunk_exceeds_the_game_message_cap()
    {
        // The whole point of the five byte header sitting INSIDE the game message payload: a chunk plus its
        // envelope is still a frame the wire carries, so EncodeGameMessage never throws on one.
        Assert.Equal(TileProtocol.MaxGameMessageBytes - TileProtocol.GameMessageHeader - TileFragmentedMessage.HeaderBytes,
            TileFragmentedMessage.MaxChunkPayloadBytes);
        Assert.Equal(1015, TileFragmentedMessage.MaxChunkPayloadBytes);
        Assert.Equal(255 * 1015, TileFragmentedMessage.MaxPayloadBytes);

        foreach (int length in new[] { 0, 1, TileFragmentedMessage.MaxChunkPayloadBytes,
                     TileFragmentedMessage.MaxChunkPayloadBytes + 1, 6900, 65000 })
        {
            byte[][] chunks = TileFragmentedMessage.Fragment(streamId: 9, sequence: 3, Payload(length));
            Assert.Equal(TileFragmentedMessage.ChunkCount(length), chunks.Length);
            foreach (byte[] chunk in chunks)
            {
                byte[] frame = TileProtocol.EncodeGameMessage(TileProtocol.ServerFrameGameMessage, kind: 77, chunk);
                Assert.True(frame.Length <= TileProtocol.MaxGameMessageBytes,
                    $"a chunk of a {length} byte payload made a {frame.Length} byte frame");
                Assert.True(TileProtocol.TryDecodeGameMessage(frame, TileProtocol.ServerFrameGameMessage, out _,
                    out ReadOnlySpan<byte> back));
                Assert.True(back.SequenceEqual(chunk));
            }
        }

        // Above the cap is a LOCAL caller bug, in the same class as the game message cap throw, so the fragmenter
        // throws rather than truncating a page nobody would notice was short.
        Assert.Throws<ArgumentException>(() =>
            TileFragmentedMessage.Fragment(1, 0, new byte[TileFragmentedMessage.MaxPayloadBytes + 1]));
    }

    [Fact]
    public void No_chunk_of_any_bytes_makes_the_reassembler_throw()
    {
        // The never-throw rule of TileProtocol.Frames.cs:27-34, which this type inherits because its bytes come
        // from a remote peer: every refusal is a false plus a token, and nothing reaches the receive loop.
        byte[][] chunks = TileFragmentedMessage.Fragment(streamId: 1, sequence: 1, Payload(3000));
        var reassembler = new TileFragmentReassembler(Slot);

        // Every truncation of a non final chunk is detectable: below the header it is too short to read, and above
        // it the chunk is not the full load a non final chunk always carries.
        for (int cut = 0; cut < chunks[0].Length; cut++)
        {
            Assert.False(reassembler.TryComplete(chunks[0].AsSpan(0, cut), out _, out string? cutReason));
            Assert.Equal(TileFragmentReassembler.MalformedChunk, cutReason);
        }

        var overLong = new byte[TileFragmentedMessage.HeaderBytes + TileFragmentedMessage.MaxChunkPayloadBytes + 1];
        overLong[0] = 1;
        overLong[4] = 1;
        byte[][] adversarial =
        {
            Array.Empty<byte>(),
            new byte[] { 1 },
            new byte[] { 1, 0, 0, 0, 0 },                     // a chunk count of zero
            new byte[] { 1, 0, 0, 5, 3 },                     // an index past the count
            new byte[] { 1, 0, 0, 0, 2 },                     // a non final chunk carrying nothing
            new byte[] { 1, 0, 0, 1, 2 },                     // a final chunk carrying nothing
            overLong,                                         // one byte more than a chunk can carry
        };
        foreach (byte[] chunk in adversarial)
        {
            Assert.False(reassembler.TryComplete(chunk, out _, out string? reason));
            Assert.Equal(TileFragmentReassembler.MalformedChunk, reason);
        }

        Assert.Equal(0, reassembler.PartialAssemblyCount);
    }
}
