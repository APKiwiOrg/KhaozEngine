using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Tests.NetWorld;

internal sealed partial class PlayerRangeMovementTestRig
{
    public const float FrameSeconds = TickSeconds / 4;
    // Separate clocks deliberately put snapshot reconciliation halfway through a predicted tick.
    float clientAccumulator = TickSeconds / 2;
    float serverAccumulator;
    public int LoopbackTicks { get; private set; }
    public int MidPresentationReconciles { get; private set; }
    public float MaximumPollDisplacement { get; private set; }

    public void SetSpeedScale(float scale)
    {
        if (Sharded is { } sharded) sharded.SetSpeedScale(PlayerRef.Slot(0), scale);
        else Server.SetSpeedScale(PlayerRef.Slot(0), scale);
    }

    public void BeforeTick(Action<float> handler)
    {
        if (Sharded is { } sharded) sharded.OnBeforeTick += handler;
        else Server.OnBeforeTick += handler;
    }

    public void Arrived(Action<MovementCommitmentResult> handler)
    {
        if (Sharded is { } sharded) sharded.MovementCommitmentArrived += handler;
        else Server.MovementCommitmentArrived += handler;
    }

    public void Ended(Action<MovementCommitmentResult> handler)
    {
        if (Sharded is { } sharded) sharded.MovementCommitmentEnded += handler;
        else Server.MovementCommitmentEnded += handler;
    }

    public void PollAndTick()
    {
        if (Sharded is { } sharded) { sharded.Poll(); sharded.Tick(TickSeconds); }
        else { Server.Poll(); Server.Tick(TickSeconds); }
        LoopbackTicks++;
    }

    public void LoopbackFrame(in MoveCommand command)
    {
        downstream?.Advance();
        clientAccumulator += FrameSeconds;
        while (clientAccumulator >= TickSeconds)
        {
            clientAccumulator -= TickSeconds;
            int sequence = Client.SendInput(command);
            if (sequence >= 0) LastSubmittedSequence = sequence;
        }
        serverAccumulator += FrameSeconds;
        while (serverAccumulator >= TickSeconds)
        {
            serverAccumulator -= TickSeconds;
            PollAndTick();
        }
        int acknowledged = LastAcknowledgedSequence;
        bool joined = Client.Joined;
        Vector3 before = Client.LocalRenderState.Position;
        Client.Poll(FrameSeconds);
        if (LastAcknowledgedSequence > acknowledged && clientAccumulator > 0 && clientAccumulator < TickSeconds)
            MidPresentationReconciles++;
        if (joined) MaximumPollDisplacement = MathF.Max(MaximumPollDisplacement,
            Vector3.Distance(before, Client.LocalRenderState.Position));
        Client.AdvancePresentation(FrameSeconds, clientAccumulator);
    }
}

// A fixed downstream delay over the real in-memory transport. The test advances integer render frames,
// never wall time. Holding every send preserves the reliable order, including handshake and game messages.
internal sealed class DelayedLoopbackTransport(INetTransport inner, int delayFrames) : INetTransport
{
    readonly Queue<(int Release, NetConnectionId Target, byte[] Payload, NetChannelReliability Channel)> held = new();
    int frame;
    public void Advance()
    {
        frame++;
        while (held.TryPeek(out var next) && next.Release <= frame)
        {
            held.Dequeue();
            inner.Send(next.Target, next.Payload, next.Channel);
        }
    }
    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
        held.Enqueue((frame + delayFrames, target, payload.ToArray(), reliability));
    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent value) => inner.TryDequeueEvent(out value);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}
