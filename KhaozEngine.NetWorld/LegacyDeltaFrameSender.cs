using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Owns the exclusive tick scope for writing and sending borrowed legacy delta frames.</summary>
internal sealed class LegacyDeltaFrameSender
{
    private int ticking;
    private int largestFrame;

    internal TickScope EnterTick(AoiDeltaReplicator? writer)
    {
        if (writer is null) return default;
        if (Interlocked.Exchange(ref ticking, 1) != 0)
            throw new InvalidOperationException("A legacy delta authority tick is already in progress.");
        largestFrame = 0;
        return new TickScope(this, writer);
    }

    internal Span<byte> Build(AoiDeltaReplicator writer, int slot, World world, IReadOnlySet<long> interest, long netId)
    {
        int prefixLength = SessionFrame.FrameLength(MoveProtocol.TickedDeltaPrefixLength);
        Span<byte> frame = writer.BorrowForServe(slot, world, interest, netId, prefixLength);
        largestFrame = Math.Max(largestFrame, frame.Length);
        return frame;
    }

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

    internal readonly ref struct TickScope
    {
        private readonly LegacyDeltaFrameSender? sender;
        private readonly AoiDeltaReplicator? writer;

        internal TickScope(LegacyDeltaFrameSender sender, AoiDeltaReplicator writer)
        {
            this.sender = sender;
            this.writer = writer;
        }

        public void Dispose()
        {
            if (sender is null) return;
            try { writer!.ReleaseOversizeServeBuffer(sender.largestFrame); }
            finally { Volatile.Write(ref sender.ticking, 0); }
        }
    }
}
