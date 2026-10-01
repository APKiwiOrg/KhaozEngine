using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class MessageFragmenterTests
{
    const int Slot = 3;

    // The tile host's width, which every ported fact runs at so the wire bytes stay the ones legacy peers read.
    const int Width = 1015;

    static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 + (i >> 8) * 7 + 1);
        return bytes;
    }

    static MessageReassembler Reassembler(int width = Width) => new(Slot, width);

    [Fact]
    public void APayloadUnderOneChunkIsOneChunkAndRoundTrips()
    {
        byte[] payload = Payload(200);
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 1, sequence: 0, payload, Width);
        Assert.Single(chunks);

        var reassembler = Reassembler();
        Assert.True(reassembler.TryComplete(chunks[0], out ReadOnlyMemory<byte> assembled, out string? reason));
        Assert.Null(reason);
        Assert.Equal(payload, assembled.ToArray());

        // A one-chunk message is complete on arrival, so it never occupies one of the four partial assemblies.
        Assert.Equal(0, reassembler.PartialAssemblyCount);

        // An EMPTY payload is still one chunk, so a caller with nothing to say is not a special case.
        byte[][] empty = MessageFragmenter.Fragment(streamId: 1, sequence: 1, ReadOnlySpan<byte>.Empty, Width);
        Assert.Single(empty);
        Assert.True(reassembler.TryComplete(empty[0], out ReadOnlyMemory<byte> none, out reason));
        Assert.Null(reason);
        Assert.Equal(0, none.Length);
    }

    [Fact]
    public void APageSizedPayloadRoundTripsThroughEveryChunkInOrder()
    {
        byte[] payload = Payload(6900);
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 7, sequence: 42, payload, Width);
        Assert.Equal(7, chunks.Length);

        var reassembler = Reassembler();
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
            Assert.Equal(MessageFragmenter.HeaderBytes + Width, chunks[i].Length);
    }

    [Fact]
    public void AChunkWhoseSequenceDiffersMidAssemblyDiscardsAndRestarts()
    {
        // Rule 1. A sender restarting a transmission mid way looks exactly like this, and it is not an error.
        byte[] first = Payload(3000);
        byte[] second = Payload(2500);
        byte[][] abandoned = MessageFragmenter.Fragment(streamId: 2, sequence: 8, first, Width);
        byte[][] restarted = MessageFragmenter.Fragment(streamId: 2, sequence: 9, second, Width);

        var reassembler = Reassembler();
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
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);
    }

    [Fact]
    public void AFifthConcurrentAssemblyEvictsTheOldestAndCountsIt()
    {
        // Rule 2. Bounded memory rather than a timer, because a timer on a reliable ordered channel measures
        // nothing.
        var reassembler = Reassembler();
        var opened = new byte[6][][];
        for (int stream = 1; stream <= 5; stream++)
        {
            opened[stream] = MessageFragmenter.Fragment((byte)stream, sequence: 1, Payload(2500), Width);
            Assert.False(reassembler.TryComplete(opened[stream][0], out _, out string? pending));
            Assert.Null(pending);
        }

        Assert.Equal(MessageReassembler.MaxPartialAssemblies, reassembler.PartialAssemblyCount);
        Assert.Equal(1, reassembler.EvictedAssemblies);

        // Stream 1 was the one fed longest ago, so it is the one that went. Its next chunk has nothing to join.
        Assert.False(reassembler.TryComplete(opened[1][1], out _, out string? reason));
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);

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
    public void ATruncatedFinalChunkAnswersAReasonRatherThanThrowing()
    {
        byte[] payload = Payload(3000);
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 4, sequence: 5, payload, Width);

        // Cut INTO the header, which is the truncation that would index off the end of a span if anything here
        // sliced before it measured.
        var reassembler = Reassembler();
        Assert.False(reassembler.TryComplete(chunks[0], out _, out string? pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(chunks[1], out _, out pending));
        Assert.Null(pending);
        Assert.False(reassembler.TryComplete(chunks[^1].AsSpan(0, 3), out _, out string? reason));
        Assert.Equal(MessageReassembler.MalformedChunk, reason);

        // Cut to the header exactly. A final chunk carrying no bytes at all is a frame the fragmenter never
        // writes, so it is refused rather than completing an assembly a byte short.
        Assert.False(reassembler.TryComplete(chunks[^1].AsSpan(0, MessageFragmenter.HeaderBytes), out _, out reason));
        Assert.Equal(MessageReassembler.MalformedChunk, reason);

        // A NON final chunk cut short is detectable the same way, because every chunk but the last is full.
        var fresh = Reassembler();
        Assert.False(fresh.TryComplete(chunks[0].AsSpan(0, chunks[0].Length - 40), out _, out reason));
        Assert.Equal(MessageReassembler.MalformedChunk, reason);

        // What is NOT detectable here is a final chunk cut in its BODY: the header declares no total length, so
        // those bytes assemble and the CALLER's decoder is what refuses them. Rule 3, and the reason this type
        // hands bytes back rather than decoding them.
        var bodyCut = Reassembler();
        Assert.False(bodyCut.TryComplete(chunks[0], out _, out reason));
        Assert.False(bodyCut.TryComplete(chunks[1], out _, out reason));
        Assert.True(bodyCut.TryComplete(chunks[^1].AsSpan(0, chunks[^1].Length - 10), out ReadOnlyMemory<byte> assembled, out reason));
        Assert.Null(reason);
        Assert.Equal(payload.Length - 10, assembled.Length);
    }

    [Fact]
    public void ADroppedConnectionDiscardsEveryPartialAssembly()
    {
        // Rule 4. An explicit call from the server's own disconnect path, because nothing here holds a timer or a
        // background task that could notice a connection going away on its own.
        var reassembler = Reassembler();
        byte[][] one = MessageFragmenter.Fragment(streamId: 1, sequence: 1, Payload(2500), Width);
        byte[][] two = MessageFragmenter.Fragment(streamId: 2, sequence: 1, Payload(2500), Width);
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
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);

        // Dropping twice is not an error, because a server that drops a slot it already dropped has done nothing
        // wrong.
        Assert.False(reassembler.DropConnection(Slot));
    }

    [Fact]
    public void AChunkCanCarryTheFullWidthBesideItsHeader()
    {
        // The host-neutral half of the tile envelope fact: the width is the body a chunk carries, not a frame
        // cap that also counts the header, so a payload exactly one width long is one chunk of header plus width
        // and one byte more is refused rather than read.
        byte[] payload = Payload(Width);
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 3, sequence: 2, payload, Width);
        byte[] chunk = Assert.Single(chunks);
        Assert.Equal(MessageFragmenter.HeaderBytes + Width, chunk.Length);
        Assert.True(MessageFragmenter.TryReadChunk(chunk, Width, out _, out _, out _, out _, out ReadOnlySpan<byte> body));
        Assert.True(body.SequenceEqual(payload));

        var overWidth = new byte[chunk.Length + 1];
        chunk.CopyTo(overWidth, 0);
        Assert.False(MessageFragmenter.TryReadChunk(overWidth, Width, out _, out _, out _, out _, out _));
        Assert.Equal(2, MessageFragmenter.Fragment(3, 2, Payload(Width + 1), Width).Length);
    }

    [Fact]
    public void LegacySizedChunksKeepTheirWireBytesAndReassemble()
    {
        // Independent wire fixture: two full 1015-byte bodies and a 15-byte tail. Any change to the header or to
        // how a payload is cut would either change the emitted bytes or reject these chunks from a legacy peer.
        byte[] payload = Payload(2045);
        byte[][] legacy = { new byte[1020], new byte[1020], new byte[20] };
        byte[][] emitted = MessageFragmenter.Fragment(streamId: 7, sequence: 0x1234, payload, Width);
        Assert.Equal(3, emitted.Length);
        var reassembler = Reassembler();
        for (int i = 0; i < legacy.Length; i++)
        {
            byte[] chunk = legacy[i];
            new byte[] { 7, 0x34, 0x12, (byte)i, 3 }.CopyTo(chunk, 0);
            payload.AsSpan(i * 1015, chunk.Length - 5).CopyTo(chunk.AsSpan(5));
            Assert.Equal(chunk, emitted[i]);

            Assert.True(MessageFragmenter.TryReadChunk(chunk, Width, out byte streamId, out ushort sequence,
                out int index, out int count, out ReadOnlySpan<byte> body));
            Assert.Equal(7, streamId);
            Assert.Equal(0x1234, sequence);
            Assert.Equal(i, index);
            Assert.Equal(3, count);
            Assert.True(body.SequenceEqual(payload.AsSpan(i * 1015, chunk.Length - 5)));

            bool complete = reassembler.TryComplete(chunk, out ReadOnlyMemory<byte> assembled, out string? reason);
            Assert.Null(reason);
            Assert.Equal(i == 2, complete);
            if (complete) Assert.Equal(payload, assembled.ToArray());
        }
        Assert.Equal(0, reassembler.PartialAssemblyCount);
    }

    [Fact]
    public void NoChunkExceedsTheHeaderPlusTheWidth()
    {
        // Expected counts are derived from the legacy 1015-byte width, independently of ChunkCount.
        (int Length, int Count)[] cases =
        {
            (0, 1), (1, 1), (15, 1), (1015, 1), (1016, 2), (2030, 2), (2031, 3), (6900, 7),
            (65000, 65), (258825, 255),
        };

        foreach ((int length, int count) in cases)
        {
            Assert.Equal(count, MessageFragmenter.ChunkCount(length, Width));
            byte[][] chunks = MessageFragmenter.Fragment(streamId: 9, sequence: 3, Payload(length), Width);
            Assert.Equal(count, chunks.Length);
            foreach (byte[] chunk in chunks)
                Assert.True(chunk.Length <= MessageFragmenter.HeaderBytes + Width,
                    $"a chunk of a {length} byte payload was {chunk.Length} bytes");
        }

        // Above the cap is a LOCAL caller bug, so the fragmenter throws rather than truncating a payload nobody
        // would notice was short.
        Assert.Equal(258825, MessageFragmenter.MaxPayloadBytes(Width));
        Assert.Throws<ArgumentException>(() => MessageFragmenter.Fragment(1, 0, new byte[258826], Width));
        Assert.Throws<ArgumentException>(() => MessageFragmenter.ChunkCount(258826, Width));
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageFragmenter.ChunkCount(-1, Width));
    }

    [Fact]
    public void NoChunkOfAnyBytesMakesTheReassemblerThrow()
    {
        // The never-throw rule every frame decoder follows, which this type inherits because its bytes come from a
        // remote peer: every refusal is a false plus a token, and nothing reaches the receive loop.
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 1, sequence: 1, Payload(3000), Width);
        var reassembler = Reassembler();

        // Every truncation of a non final chunk is detectable: below the header it is too short to read, and above
        // it the chunk is not the full load a non final chunk always carries.
        for (int cut = 0; cut < chunks[0].Length; cut++)
        {
            Assert.False(reassembler.TryComplete(chunks[0].AsSpan(0, cut), out _, out string? cutReason));
            Assert.Equal(MessageReassembler.MalformedChunk, cutReason);
        }

        var overLong = new byte[MessageFragmenter.HeaderBytes + Width + 1];
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
            Assert.Equal(MessageReassembler.MalformedChunk, reason);
        }

        Assert.Equal(0, reassembler.PartialAssemblyCount);
    }

    [Fact]
    public void InterleavedStreamsEachCompleteWithTheirOwnStreamId()
    {
        // Several streams on one message kind, told apart by the header's stream id alone. Two multi chunk
        // transmissions interleave on the wire and a single chunk one lands between them.
        byte[] bag = Payload(2500);
        byte[] bank = Payload(3100);
        bank[0] ^= 0xFF;   // distinct contents, so a mixed up completion cannot pass by coincidence
        byte[] worn = Payload(40);
        byte[][] bagChunks = MessageFragmenter.Fragment(streamId: 0, sequence: 5, bag, Width);
        byte[][] bankChunks = MessageFragmenter.Fragment(streamId: 2, sequence: 5, bank, Width);
        byte[][] wornChunks = MessageFragmenter.Fragment(streamId: 1, sequence: 5, worn, Width);
        Assert.Equal(3, bagChunks.Length);
        Assert.Equal(4, bankChunks.Length);
        Assert.Single(wornChunks);

        byte[][] wire =
        {
            bagChunks[0], bankChunks[0], bagChunks[1], wornChunks[0], bankChunks[1], bankChunks[2], bagChunks[2],
            bankChunks[3],
        };
        var reassembler = Reassembler();
        var completed = new List<(byte StreamId, byte[] Bytes)>();
        foreach (byte[] chunk in wire)
        {
            bool done = reassembler.TryComplete(chunk, out byte streamId, out ReadOnlyMemory<byte> assembled,
                out string? reason);
            Assert.Null(reason);   // every chunk here is accepted, completing or pending
            if (done) completed.Add((streamId, assembled.ToArray()));
        }

        // The single chunk transmission completes on arrival and the two longer ones on their own last chunks,
        // each naming the stream its header carried rather than whichever stream was fed last.
        Assert.Equal(3, completed.Count);
        Assert.Equal(1, completed[0].StreamId);
        Assert.Equal(worn, completed[0].Bytes);
        Assert.Equal(0, completed[1].StreamId);
        Assert.Equal(bag, completed[1].Bytes);
        Assert.Equal(2, completed[2].StreamId);
        Assert.Equal(bank, completed[2].Bytes);
        Assert.Equal(0, reassembler.PartialAssemblyCount);
        Assert.Equal(0, reassembler.EvictedAssemblies);
    }

    [Fact]
    public void TheOverloadWithoutAStreamIdAnswersExactlyAsTheOneWithIt()
    {
        // The three-out overload delegates, so the same wire fed to one reassembler of each shape gets the same
        // answer chunk for chunk: completions, pending accepts and refusals alike.
        byte[][] first = MessageFragmenter.Fragment(streamId: 4, sequence: 1, Payload(2200), Width);
        byte[][] second = MessageFragmenter.Fragment(streamId: 6, sequence: 9, Payload(1500), Width);
        byte[][] wire =
        {
            first[0], second[0], first[1], second[1], first[2],
            first[1],                        // out of sequence: nothing open for stream 4 any more
            new byte[] { 1, 0, 0, 0, 0 },    // malformed: a chunk count of zero
        };

        var withId = Reassembler();
        var withoutId = Reassembler();
        var completedIds = new List<byte>();
        foreach (byte[] chunk in wire)
        {
            bool answered = withId.TryComplete(chunk, out byte streamId, out ReadOnlyMemory<byte> expected,
                out string? expectedReason);
            Assert.Equal(answered, withoutId.TryComplete(chunk, out ReadOnlyMemory<byte> assembled,
                out string? reason));
            Assert.Equal(expectedReason, reason);
            Assert.Equal(expected.ToArray(), assembled.ToArray());
            if (answered) completedIds.Add(streamId);
        }

        Assert.Equal(new byte[] { 6, 4 }, completedIds);
        Assert.Equal(withId.PartialAssemblyCount, withoutId.PartialAssemblyCount);
        Assert.Equal(withId.EvictedAssemblies, withoutId.EvictedAssemblies);
    }

    [Fact]
    public void ANonTileWidthRoundTripsAndRefusesAShortNonFinalChunk()
    {
        // Review Focus 1. A NetWorld consumer picks its width from its own envelope, so the reassembler must
        // accept exactly that width and nothing else as a non final chunk.
        const int NetWidth = 200;
        byte[] payload = Payload(1050);
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 5, sequence: 11, payload, NetWidth);
        Assert.Equal(6, chunks.Length);
        Assert.Equal(MessageFragmenter.ChunkCount(payload.Length, NetWidth), chunks.Length);
        for (int i = 0; i < chunks.Length - 1; i++)
            Assert.Equal(MessageFragmenter.HeaderBytes + NetWidth, chunks[i].Length);
        Assert.Equal(MessageFragmenter.HeaderBytes + 50, chunks[^1].Length);

        var reassembler = new MessageReassembler(Slot, NetWidth);
        Assert.Equal(NetWidth, reassembler.ChunkPayloadBytes);
        Assert.Equal(Slot, reassembler.Slot);
        for (int i = 0; i < chunks.Length - 1; i++)
        {
            Assert.False(reassembler.TryComplete(chunks[i], out _, out string? pending));
            Assert.Null(pending);
        }
        Assert.True(reassembler.TryComplete(chunks[^1], out byte streamId, out ReadOnlyMemory<byte> assembled,
            out string? reason));
        Assert.Null(reason);
        Assert.Equal(5, streamId);
        Assert.Equal(payload, assembled.ToArray());

        // A non final chunk one byte short of THIS width is truncation and refused.
        var shortened = Reassembler(NetWidth);
        Assert.False(shortened.TryComplete(chunks[0].AsSpan(0, chunks[0].Length - 1), out _, out reason));
        Assert.Equal(MessageReassembler.MalformedChunk, reason);
        Assert.Equal(0, shortened.PartialAssemblyCount);

        // A reassembler built for the tile width reads these chunks as short non final ones, so a width mismatch
        // between the two ends is refused on the first chunk rather than assembling the wrong bytes.
        var tileWidth = Reassembler();
        Assert.False(tileWidth.TryComplete(chunks[0], out _, out reason));
        Assert.Equal(MessageReassembler.MalformedChunk, reason);

        // And a chunk one byte past this width is refused even though the tile width would carry it.
        var overWidth = new byte[MessageFragmenter.HeaderBytes + NetWidth + 1];
        overWidth[4] = 1;
        Assert.False(MessageFragmenter.TryReadChunk(overWidth, NetWidth, out _, out _, out _, out _, out _));
        Assert.True(MessageFragmenter.TryReadChunk(overWidth, Width, out _, out _, out _, out _, out _));
        Assert.Equal(MessageFragmenter.MaxChunks * NetWidth, MessageFragmenter.MaxPayloadBytes(NetWidth));
    }

    [Fact]
    public void AWidthOutOfRangeThrows()
    {
        foreach (int width in new[] { 0, -1, int.MinValue, ushort.MaxValue + 1, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, width));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageFragmenter.ChunkCount(10, width));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageFragmenter.Fragment(1, 0, new byte[10], width));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MessageFragmenter.TryReadChunk(new byte[] { 1, 0, 0, 0, 1, 9 }, width, out _, out _, out _, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageFragmenter.MaxPayloadBytes(width));
        }

        // Both ends of the range are widths.
        Assert.Equal(1, new MessageReassembler(Slot, 1).ChunkPayloadBytes);
        Assert.Equal(ushort.MaxValue, new MessageReassembler(Slot, ushort.MaxValue).ChunkPayloadBytes);
        Assert.Equal(3, MessageFragmenter.Fragment(1, 0, new byte[3], 1).Length);
        Assert.Single(MessageFragmenter.Fragment(1, 0, new byte[ushort.MaxValue], ushort.MaxValue));
    }
}
