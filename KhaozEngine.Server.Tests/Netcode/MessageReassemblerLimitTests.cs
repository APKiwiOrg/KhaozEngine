using System;
using System.Runtime.InteropServices;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The bounded <see cref="MessageReassembler"/> constructor. A transmission whose declared chunk count cannot fit the
/// assembled-byte limit is refused on its first chunk, a final chunk that would overflow is refused before it is
/// copied, and the backing buffer never grows past the limit, spare capacity included. The two-argument constructor
/// keeps the format cap and four partial assemblies, and the chunk wire is unchanged. Joins <c>AllocSensitive</c>
/// because it reads <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
[Collection("AllocSensitive")]
public class MessageReassemblerLimitTests
{
    const int Slot = 2;

    // The agreed keyframe chunk width at the default 512-byte transport cap.
    const int Width = 489;

    const int Limit = 65536;

    static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 13 + (i >> 8) * 5 + 3);
        return bytes;
    }

    static MessageReassembler Bounded(int partials = MessageReassembler.MaxPartialAssemblies) =>
        new(Slot, Width, Limit, partials);

    // A well formed first chunk that claims more chunks than the limit can hold.
    static byte[] LyingFirstChunk(byte streamId, ushort sequence, byte count)
    {
        var chunk = new byte[MessageFragmenter.HeaderBytes + Width];
        chunk[0] = streamId;
        chunk[1] = (byte)sequence;
        chunk[2] = (byte)(sequence >> 8);
        chunk[3] = 0;
        chunk[4] = count;
        return chunk;
    }

    [Fact]
    public void BoundedAssemblyRejectsBeforeExcessCopy()
    {
        MessageReassembler bounded = Bounded();
        Assert.Equal(65536, bounded.MaxAssembledBytes);

        // 255 full chunks are 124,695 bytes, so the first chunk already proves the transmission cannot fit.
        Assert.False(bounded.TryComplete(LyingFirstChunk(1, 1, 255), out _, out string? reason));
        Assert.Equal("ke:fragment-payload-limit", reason);
        Assert.Equal(MessageReassembler.PayloadLimitExceeded, reason);
        Assert.Equal(0, bounded.PartialAssemblyCount);

        // The refusal holds no state and allocates nothing.
        byte[] lying = LyingFirstChunk(1, 2, 255);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = bounded.TryComplete(lying, out _, out reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(accepted);
        Assert.Equal(MessageReassembler.PayloadLimitExceeded, reason);
        Assert.Equal(0, allocated);

        // 135 chunks can hold as little as 65,527 bytes, so the transmission opens, but 135 full chunks are 66,015.
        // The final chunk is refused before it is copied or grows the buffer, and the assembly is discarded.
        byte[][] over = MessageFragmenter.Fragment(streamId: 3, sequence: 9, Payload(135 * Width), Width);
        Assert.Equal(135, over.Length);
        for (int i = 0; i < over.Length - 1; i++)
        {
            Assert.False(bounded.TryComplete(over[i], out _, out string? pending));
            Assert.Null(pending);
        }
        Assert.Equal(1, bounded.PartialAssemblyCount);
        before = GC.GetAllocatedBytesForCurrentThread();
        accepted = bounded.TryComplete(over[^1], out ReadOnlyMemory<byte> assembled, out reason);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(accepted);
        Assert.Equal(MessageReassembler.PayloadLimitExceeded, reason);
        Assert.True(assembled.IsEmpty);
        Assert.Equal(0, allocated);
        Assert.Equal(0, bounded.PartialAssemblyCount);
        Assert.Equal(0, bounded.EvictedAssemblies);

        // Nothing of that assembly survives, so a repeat of its final chunk has nothing to join.
        Assert.False(bounded.TryComplete(over[^1], out _, out reason));
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);

        // A single chunk transmission is held to the same limit.
        var small = new MessageReassembler(Slot, Width, maxAssembledBytes: 100, maxPartialAssemblies: 1);
        byte[][] single = MessageFragmenter.Fragment(streamId: 1, sequence: 1, Payload(101), Width);
        Assert.False(small.TryComplete(Assert.Single(single), out _, out reason));
        Assert.Equal(MessageReassembler.PayloadLimitExceeded, reason);
        single = MessageFragmenter.Fragment(streamId: 1, sequence: 2, Payload(100), Width);
        Assert.True(small.TryComplete(Assert.Single(single), out assembled, out reason));
        Assert.Null(reason);
        Assert.Equal(Payload(100), assembled.ToArray());
    }

    [Fact]
    public void BackingGrowthStopsAtTheLimitIncludingSpareCapacity()
    {
        // 134 full chunks and a 4-byte tail. Doubling past 62,592 would reach 66,015 bytes of backing for the
        // declared count, which is past the limit even though the payload fits.
        foreach (int length in new[] { 65530, Limit })
        {
            MessageReassembler bounded = Bounded();
            byte[] payload = Payload(length);
            byte[][] chunks = MessageFragmenter.Fragment(streamId: 4, sequence: 1, payload, Width);
            Assert.Equal(135, chunks.Length);

            ReadOnlyMemory<byte> assembled = default;
            for (int i = 0; i < chunks.Length; i++)
            {
                bool complete = bounded.TryComplete(chunks[i], out assembled, out string? reason);
                Assert.Null(reason);
                Assert.Equal(i == chunks.Length - 1, complete);
            }

            Assert.Equal(payload, assembled.ToArray());
            Assert.True(MemoryMarshal.TryGetArray(assembled, out ArraySegment<byte> backing));
            Assert.True(backing.Array!.Length <= Limit, $"backing {backing.Array.Length} exceeds {Limit}");
        }
    }

    [Fact]
    public void SingleAssemblyLimitPreservesDefaultConstructor()
    {
        MessageReassembler bounded = Bounded(partials: 1);
        Assert.Equal(65536, bounded.MaxAssembledBytes);
        Assert.Equal(1, bounded.PartialAssemblyLimit);
        Assert.Equal(Width, bounded.ChunkPayloadBytes);
        Assert.Equal(Slot, bounded.Slot);

        byte[][] one = MessageFragmenter.Fragment(streamId: 1, sequence: 1, Payload(1200), Width);
        byte[][] two = MessageFragmenter.Fragment(streamId: 2, sequence: 1, Payload(1200), Width);
        Assert.False(bounded.TryComplete(one[0], out _, out string? reason));
        Assert.Null(reason);
        Assert.False(bounded.TryComplete(two[0], out _, out reason));
        Assert.Null(reason);
        Assert.Equal(1, bounded.PartialAssemblyCount);
        Assert.Equal(1, bounded.EvictedAssemblies);

        Assert.False(bounded.TryComplete(one[1], out _, out reason));
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);
        Assert.False(bounded.TryComplete(two[1], out _, out reason));
        Assert.Null(reason);
        Assert.True(bounded.TryComplete(two[2], out ReadOnlyMemory<byte> assembled, out reason));
        Assert.Null(reason);
        Assert.Equal(Payload(1200), assembled.ToArray());

        // The two-argument constructor keeps the format cap and the default partial limit, so the largest payload
        // the format carries at this width still assembles and four streams stay open at once.
        var defaults = new MessageReassembler(Slot, Width);
        Assert.Equal(MessageFragmenter.MaxPayloadBytes(Width), defaults.MaxAssembledBytes);
        Assert.Equal(MessageReassembler.MaxPartialAssemblies, defaults.PartialAssemblyLimit);
        Assert.Equal(4, defaults.PartialAssemblyLimit);

        byte[] largest = Payload(MessageFragmenter.MaxPayloadBytes(Width));
        byte[][] chunks = MessageFragmenter.Fragment(streamId: 5, sequence: 1, largest, Width);
        Assert.Equal(MessageFragmenter.MaxChunks, chunks.Length);
        for (int i = 0; i < chunks.Length; i++)
        {
            bool complete = defaults.TryComplete(chunks[i], out assembled, out reason);
            Assert.Null(reason);
            Assert.Equal(i == chunks.Length - 1, complete);
        }
        Assert.Equal(largest, assembled.ToArray());

        for (int stream = 1; stream <= 4; stream++)
            Assert.False(defaults.TryComplete(
                MessageFragmenter.Fragment((byte)stream, 2, Payload(1200), Width)[0], out _, out _));
        Assert.Equal(4, defaults.PartialAssemblyCount);
        Assert.Equal(0, defaults.EvictedAssemblies);
    }

    [Fact]
    public void BoundedConstructorRefusesOutOfRangeBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, 0, Limit, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, ushort.MaxValue + 1, Limit, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, Width, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, Width, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MessageReassembler(Slot, Width, MessageFragmenter.MaxPayloadBytes(Width) + 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, Width, Limit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReassembler(Slot, Width, Limit, 257));

        // One partial per possible stream id is the most that can ever be occupied, and it is accepted.
        Assert.Equal(256, new MessageReassembler(Slot, Width, Limit, 256).PartialAssemblyLimit);
        Assert.Equal(MessageFragmenter.MaxPayloadBytes(Width),
            new MessageReassembler(Slot, Width, MessageFragmenter.MaxPayloadBytes(Width), 1).MaxAssembledBytes);
    }

    [Fact]
    public void BoundedChunksKeepTheirWireBytesAndDropResetRules()
    {
        // Independent wire fixture at the bounded width: one full 489-byte body and an 11-byte tail.
        byte[] payload = Payload(500);
        byte[][] emitted = MessageFragmenter.Fragment(streamId: 1, sequence: 0x1234, payload, Width);
        Assert.Equal(2, emitted.Length);
        byte[][] expected = { new byte[MessageFragmenter.HeaderBytes + Width], new byte[MessageFragmenter.HeaderBytes + 11] };
        new byte[] { 1, 0x34, 0x12, 0, 2 }.CopyTo(expected[0], 0);
        payload.AsSpan(0, Width).CopyTo(expected[0].AsSpan(5));
        new byte[] { 1, 0x34, 0x12, 1, 2 }.CopyTo(expected[1], 0);
        payload.AsSpan(Width, 11).CopyTo(expected[1].AsSpan(5));
        Assert.Equal(expected[0], emitted[0]);
        Assert.Equal(expected[1], emitted[1]);

        MessageReassembler bounded = Bounded();
        Assert.False(bounded.TryComplete(expected[0], out _, out string? reason));
        Assert.Null(reason);
        Assert.True(bounded.TryComplete(expected[1], out byte streamId, out ReadOnlyMemory<byte> assembled, out reason));
        Assert.Null(reason);
        Assert.Equal(1, streamId);
        Assert.Equal(payload, assembled.ToArray());

        // A restart at a new sequence replaces the assembly in progress and is not an eviction.
        byte[][] abandoned = MessageFragmenter.Fragment(streamId: 1, sequence: 1, Payload(1200), Width);
        byte[][] restarted = MessageFragmenter.Fragment(streamId: 1, sequence: 2, Payload(1200), Width);
        Assert.False(bounded.TryComplete(abandoned[0], out _, out reason));
        Assert.False(bounded.TryComplete(abandoned[1], out _, out reason));
        Assert.False(bounded.TryComplete(restarted[0], out _, out reason));
        Assert.Null(reason);
        Assert.Equal(1, bounded.PartialAssemblyCount);
        Assert.Equal(0, bounded.EvictedAssemblies);

        // Teardown clears every partial for this slot only, and a reconnect starts clean.
        Assert.False(bounded.DropConnection(Slot + 1));
        Assert.Equal(1, bounded.PartialAssemblyCount);
        Assert.True(bounded.DropConnection(Slot));
        Assert.Equal(0, bounded.PartialAssemblyCount);
        Assert.False(bounded.TryComplete(restarted[1], out _, out reason));
        Assert.Equal(MessageReassembler.OutOfSequenceChunk, reason);
        Assert.False(bounded.DropConnection(Slot));
    }
}
