using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Frames each legacy body and records the largest request of the current serve pass.</summary>
internal struct LegacyDeltaFrameSender(long passOrdinal)
{
    private int largestFrame;

    internal AoiDeltaReplicator.ServeFrame Build(AoiDeltaReplicator writer, int slot, World world,
        IReadOnlySet<long> interest, long netId)
    {
        int prefixLength = SessionFrame.FrameLength(MoveProtocol.TickedDeltaPrefixLength);
        AoiDeltaReplicator.ServeFrame frame = writer.BorrowForServe(slot, world, interest, netId, prefixLength);
        largestFrame = Math.Max(largestFrame, frame.Bytes.Length);
        return frame;
    }

    internal void Complete(AoiDeltaReplicator? writer) => writer?.CompleteLegacyServePass(passOrdinal, largestFrame);

    internal static void Send(NetServer net, AoiDeltaReplicator writer, int slot, Span<byte> frame,
        long netId, int ack, long? serverTick)
    {
        LegacyDeltaAllocationProbe? probe = writer.LegacyAllocationsForTests;
        long phaseStart = LegacyDeltaAllocationProbe.Start(probe);
        if (!serverTick.HasValue) frame = frame[MoveProtocol.PlainDeltaPrefixOffset..];
        frame[0] = (byte)SessionOpcode.Data;
        MoveProtocol.WriteLegacyDeltaPrefix(frame[1..], serverTick, netId, ack);
        probe?.Record(LegacyDeltaPhase.Framing, phaseStart);

        phaseStart = LegacyDeltaAllocationProbe.Start(probe);
        net.SendFramedDataTo(slot, frame, NetChannelReliability.ReliableOrdered);
        probe?.Record(LegacyDeltaPhase.Send, phaseStart);
    }
}
