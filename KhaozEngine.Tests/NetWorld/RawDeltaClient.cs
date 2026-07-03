using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A minimal delta-aware client for server-focused tests: joins, optionally advertises
/// <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/>, applies whichever frame kind the server serves (a full
/// <see cref="MoveProtocol.ServerFrameKind.Snapshot"/> or an AoI <see cref="MoveProtocol.ServerFrameKind.Delta"/>),
/// and acks each applied delta. This is the wire behaviour <see cref="WorldClient"/> implements, distilled for
/// asserting the SERVER's serve path (delta vs full, version skew, on-the-wire bandwidth) without prediction.
/// </summary>
internal sealed class RawDeltaClient
{
    private readonly NetClient net;
    private readonly bool advertiseDelta;
    private bool helloSent;
    private int moveSeq;

    public RawDeltaClient(INetTransport transport, ReplicationRegistry registry, bool advertiseDelta = true)
    {
        net = new NetClient(transport);
        this.advertiseDelta = advertiseDelta;
        View = new ClientReplicationView(registry);
    }

    public World World { get; } = new();
    public ClientReplicationView View { get; }
    public int LocalNetId { get; private set; } = -1;
    public bool Joined { get; private set; }
    public int DeltaFramesApplied { get; private set; }
    public int SnapshotFramesApplied { get; private set; }
    public int AcksSent { get; private set; }
    public long TotalDeltaBytes { get; private set; }
    public long TotalSnapshotBytes { get; private set; }

    public void SendMove(in MoveCommand cmd) =>
        net.Send(MoveProtocol.EncodeMove(moveSeq++, cmd), NetChannelReliability.ReliableOrdered);

    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    if (advertiseDelta && !helloSent)
                    {
                        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable),
                            NetChannelReliability.ReliableOrdered);
                        helloSent = true;
                    }
                    break;
                case ClientSessionEventKind.Data:
                    OnFrame(ev.Data);
                    break;
            }
        }
    }

    private void OnFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        if (kind == MoveProtocol.ServerFrameKind.Snapshot)
        {
            if (!MoveProtocol.TryDecodeSnapshotFrame(payload, out int ln, out _, out byte[] snap)) return;
            View.TryApply(World, snap, out _);
            LocalNetId = ln;
            SnapshotFramesApplied++;
            TotalSnapshotBytes += data.Length;
        }
        else if (kind == MoveProtocol.ServerFrameKind.Delta)
        {
            if (!MoveProtocol.TryDecodeSnapshotFrame(payload, out int ln, out _, out byte[] delta)) return;
            View.ApplyDelta(World, delta);
            LocalNetId = ln;
            DeltaFramesApplied++;
            TotalDeltaBytes += data.Length;
            net.Send(MoveProtocol.EncodeReplicationAck(View.LastAppliedSeq), NetChannelReliability.ReliableOrdered);
            AcksSent++;
        }
    }

    public bool TryPos(int netId, out Vector3 pos)
    {
        if (View.TryGetEntity(netId, out Entity e) && World.IsAlive(e) && World.TryGet(e, out ReplicatedPosition rp))
        {
            pos = rp.Value;
            return true;
        }
        pos = default;
        return false;
    }
}
