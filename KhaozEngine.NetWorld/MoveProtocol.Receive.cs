using System;

namespace KhaozEngine.NetWorld;

public static partial class MoveProtocol
{
    // The world heads keep the frame in scope through synchronous dispatch and apply.
    // The public decode methods separately return arrays that their callers can keep.
    internal static bool TryBorrowServerFrame(ReadOnlyMemory<byte> data, out ServerFrameKind kind,
        out ReadOnlyMemory<byte> payload)
    {
        bool recognized = TryReadServerFrameKind(data.Span, out kind);
        payload = recognized ? data[1..] : default;
        return recognized;
    }

    internal static bool TryBorrowServedFrame(ServerFrameKind kind, ReadOnlyMemory<byte> data, out long serverTick,
        out long localNetId, out int ackSeq, out ReadOnlyMemory<byte> body)
    {
        if (kind is ServerFrameKind.TickedSnapshot or ServerFrameKind.TickedDelta)
        {
            bool valid = TryReadTickedSnapshotHeader(data.Span, out serverTick, out localNetId, out ackSeq);
            body = valid ? data[TickedFrameHeader..] : default;
            return valid;
        }

        serverTick = -1;
        bool present = TryReadSnapshotHeader(data.Span, out localNetId, out ackSeq);
        body = present ? data[FrameHeader..] : default;
        return present;
    }
}
