using System;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    // The view's sample history cap (ClientReplicationView.MaxHistorySamples): the timeline holds no more history
    // than the samples it brackets alongside.
    private const int ServerTickHistory = 600;

    // Ticked ingests stamped on the presentation clock, evaluated at the remote render time. Null unless
    // WorldClientConfig.ReceiveServerTick, which is also what gates every member below.
    private readonly ServerTickTimeline? serverTicks;

    /// <summary>The server tick of the newest ticked frame ingested this session, or <c>-1</c> before one arrives and
    /// after a new connection attempt starts. Always <c>-1</c> without <see cref="WorldClientConfig.ReceiveServerTick"/>
    /// and against a server that predates the ticked frames. Updated by <see cref="Poll"/>.</summary>
    public long LatestServerTick { get; private set; } = -1;

    /// <summary>The fractional server tick remote bodies are drawn at, or <c>-1</c> while unknown. With
    /// <see cref="WorldClientConfig.InterpolateRemotes"/> it is the ingested ticks bracketed at the remote render time
    /// by the rule the remote samples follow: the oldest tick before the oldest ingest, the newest at or past the newest
    /// (a starved stream holds it), else the lerp by the true ingest times. Without it remotes draw the newest sample,
    /// so this is <see cref="LatestServerTick"/>. Updated by <see cref="AdvancePresentation(float)"/>, and <c>-1</c> under the
    /// same conditions as <see cref="LatestServerTick"/>.</summary>
    public double RemoteRenderTick { get; private set; } = -1;

    private static ServerTickTimeline? StartServerTickTimeline(WorldClientConfig config)
    {
        if (!config.ReceiveServerTick) return null;
        if (config.RequestUnreliableDeltaReplication)
            throw new ArgumentException(
                "ReceiveServerTick cannot be combined with RequestUnreliableDeltaReplication: format 2 frames carry no server tick.",
                nameof(config));
        return new ServerTickTimeline(ServerTickHistory);
    }

    // Sent on every (re)join after DeltaCapable, since a reconnect lands on a fresh server slot.
    private void SendServerTickHello()
    {
        if (serverTicks is null) return;
        SendToServer(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.ServerTickCapable),
            NetChannelReliability.ReliableOrdered);
    }

    // The header of a served snapshot or delta, plain or ticked. A plain frame has no tick (-1).
    private static bool TryDecodeServedFrame(MoveProtocol.ServerFrameKind kind, byte[] data, out long serverTick,
        out long localNetId, out int ackSeq, out byte[] body)
    {
        if (kind is MoveProtocol.ServerFrameKind.TickedSnapshot or MoveProtocol.ServerFrameKind.TickedDelta)
            return MoveProtocol.TryDecodeTickedSnapshotFrame(data, out serverTick, out localNetId, out ackSeq, out body);
        serverTick = -1;
        return MoveProtocol.TryDecodeSnapshotFrame(data, out localNetId, out ackSeq, out body);
    }

    // Called by IngestServerState once per applied snapshot or delta. Ingests collapsed into one Poll share a stamp,
    // and the timeline keeps the newest tick for it, as the view keeps the newest sample.
    private void RecordServerTick(long serverTick)
    {
        if (serverTicks is null || serverTick < 0) return;
        LatestServerTick = serverTick;
        if (interpolateRemotes) serverTicks.Record(presentationClock, serverTick);
    }

    // Called by AdvancePresentation after the remotes are placed, at the same render time.
    private void PresentServerTick()
    {
        if (serverTicks is null) return;
        RemoteRenderTick = interpolateRemotes
            ? serverTicks.At(presentationClock - interpolationDelaySeconds)
            : LatestServerTick;
    }

    // A new attempt joins a fresh server slot with a fresh view: no tick is known until its first ticked frame.
    private void ForgetServerTick()
    {
        LatestServerTick = -1;
        RemoteRenderTick = -1;
        serverTicks?.Clear();
    }
}
