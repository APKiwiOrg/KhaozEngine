using System;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

[Collection("AllocSensitive")]
public sealed class TileSnapshotReceiveTests
{
    [Fact]
    public void BorrowedSnapshotStripsPaddingAndDoesNotAllocate()
    {
        byte[] paddedFrame = TileProtocol.EncodeSnapshotFrame(73, 12, 48, [41, 83]);
        byte[] envelope = new byte[paddedFrame.Length + 3];
        paddedFrame.CopyTo(envelope, 2);
        ReadOnlyMemory<byte> frame = envelope.AsMemory(2, paddedFrame.Length);
        ReadOnlyMemory<byte> body = default;

        bool matched = true;
        void Decode() =>
            matched &= TileProtocol.TryBorrowSnapshotFrame(frame, out long id, out int seq, out long tick, out body)
                && id == 73 && seq == 12 && tick == 48;
        Decode();
        AllocationMeasurement.NoPerCallAllocation("Borrowed tile snapshot header", () =>
        {
            for (int i = 0; i < 32; i++) Decode();
        });
        Assert.True(matched);
        Assert.Equal(new byte[] { 41, 83 }, body.ToArray());

        Assert.True(TileProtocol.TryDecodeSnapshotFrame(frame.Span, out _, out _, out _, out byte[] owned));
        Array.Clear(envelope);
        Assert.Equal(new byte[] { 41, 83 }, owned);
    }
}
