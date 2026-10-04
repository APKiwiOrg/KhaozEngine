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
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), serverTick);
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), localNetId);
        BitConverter.TryWriteBytes(b.AsSpan(16, 4), ackSeq);
        snapshot.CopyTo(b.AsSpan(TickedFrameHeader));
        return b;
    }

    /// <summary>Splits a ticked server frame into its header and the replication body. False below the 20-byte header,
    /// with <c>-1</c> for each header field and an empty body.</summary>
    public static bool TryDecodeTickedSnapshotFrame(ReadOnlySpan<byte> data, out long serverTick, out long localNetId,
        out int ackSeq, out byte[] snapshot)
    {
        if (data.Length >= TickedFrameHeader)
        {
            serverTick = BitConverter.ToInt64(data.Slice(0, 8));
            localNetId = BitConverter.ToInt64(data.Slice(8, 8));
            ackSeq = BitConverter.ToInt32(data.Slice(16, 4));
            snapshot = data.Slice(TickedFrameHeader).ToArray();
            return true;
        }
        serverTick = -1;
        localNetId = -1;
        ackSeq = -1;
        snapshot = Array.Empty<byte>();
        return false;
    }
}
