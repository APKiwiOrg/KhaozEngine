using System;

namespace KhaozEngine.NetWorld;

public static partial class MoveProtocol
{
    // Ticked server frame: [serverTick:long(8)][localNetId:long(8)][ackSeq:int(4)][snapshot bytes...]. The plain header
    // behind the serving Tick's server tick, served only to a slot that sent ClientControlKind.ServerTickCapable.
    private const int TickedFrameHeader = 8 + FrameHeader;

    /// <summary>Prepends the ticked per-client header (the serving tick's server tick, the receiver's own net id and its
    /// last-acked move seq) to a snapshot or delta body. The frame of <see cref="ServerFrameKind.TickedSnapshot"/> and
    /// <see cref="ServerFrameKind.TickedDelta"/>.</summary>
    public static byte[] EncodeTickedSnapshotFrame(long serverTick, long localNetId, int ackSeq, byte[] snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var b = new byte[TickedFrameHeader + snapshot.Length];
        WriteTickedSnapshotHeader(b, serverTick, localNetId, ackSeq);
        snapshot.CopyTo(b.AsSpan(TickedFrameHeader));
        return b;
    }

    internal const int TickedDeltaPrefixLength = 1 + TickedFrameHeader;

    // Both bodies begin at the same offset. A plain frame starts eight bytes later, so the host can choose the
    // negotiated kind after the body completes, as it does for owning frames.
    internal const int PlainDeltaPrefixOffset = TickedFrameHeader - FrameHeader;

    internal static void WriteLegacyDeltaPrefix(Span<byte> frame, long? serverTick, long localNetId, int ackSeq)
    {
        if (serverTick is long tick)
        {
            frame[0] = (byte)ServerFrameKind.TickedDelta;
            WriteTickedSnapshotHeader(frame[1..], tick, localNetId, ackSeq);
        }
        else
        {
            frame[0] = (byte)ServerFrameKind.Delta;
            WriteSnapshotHeader(frame[1..], localNetId, ackSeq);
        }
    }

    private static void WriteSnapshotHeader(Span<byte> destination, long localNetId, int ackSeq)
    {
        BitConverter.TryWriteBytes(destination, localNetId);
        BitConverter.TryWriteBytes(destination[8..], ackSeq);
    }

    private static void WriteTickedSnapshotHeader(Span<byte> destination, long serverTick, long localNetId, int ackSeq)
    {
        BitConverter.TryWriteBytes(destination, serverTick);
        WriteSnapshotHeader(destination[8..], localNetId, ackSeq);
    }

    /// <summary>Splits a ticked server frame into its header and the replication body. False below the 20-byte header,
    /// with <c>-1</c> for each header field and an empty body.</summary>
    public static bool TryDecodeTickedSnapshotFrame(ReadOnlySpan<byte> data, out long serverTick, out long localNetId,
        out int ackSeq, out byte[] snapshot)
    {
        if (TryReadTickedSnapshotHeader(data, out serverTick, out localNetId, out ackSeq))
        {
            snapshot = data[TickedFrameHeader..].ToArray();
            return true;
        }
        snapshot = Array.Empty<byte>();
        return false;
    }

    private static bool TryReadTickedSnapshotHeader(ReadOnlySpan<byte> data, out long serverTick, out long localNetId,
        out int ackSeq)
    {
        if (data.Length >= TickedFrameHeader)
        {
            serverTick = BitConverter.ToInt64(data[..8]);
            return TryReadSnapshotHeader(data[8..], out localNetId, out ackSeq);
        }
        serverTick = -1;
        localNetId = -1;
        ackSeq = -1;
        return false;
    }
}
