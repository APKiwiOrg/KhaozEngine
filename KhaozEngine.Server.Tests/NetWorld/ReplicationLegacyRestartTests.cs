using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The host lifecycle around legacy sequence exhaustion on each host: at the next boundary admission closes, every
/// session the shared writer serves is ended with the restart token, the reset waits for each real <c>Left</c>, and
/// fresh clients start from capture 1, baseline -1 and epochs above the old high-water. Epoch allocator exhaustion
/// keeps admission closed. Runs over the in-memory hub, whose server endpoint raises the Disconnected event a
/// server-side disconnect produces, so every leave is a real one.
/// </summary>
public class ReplicationLegacyRestartTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ExhaustionDisconnectsEveryAffectedSessionBeforeReset(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        RawRebuildClient legacy = rig.ConnectLegacy(out _);
        V2Client v2 = rig.ConnectV2(out _);
        SnapshotOnlyClient snapshot = rig.ConnectSnapshot();
        rig.Steps(8);
        Assert.NotEmpty(v2.Deltas);
        Assert.NotEmpty(LegacyDeltas(legacy));
        Assert.Equal(3, rig.Host.PlayerCount);

        rig.Exhaust();
        Assert.Equal(int.MaxValue, LegacyDeltas(legacy)[^1].Snapshot);
        int snapshotFrames = snapshot.Frames;

        // The next boundary, before any capture: both writer sessions end, the snapshot session is not the writer's.
        rig.TickOnly();

        Assert.Equal(ReplicationFailure.RestartToken, legacy.RejectReason);
        Assert.Equal(ReplicationFailure.RestartToken, v2.RejectReason);
        Assert.Null(snapshot.RejectReason);
        Assert.True(snapshot.Frames > snapshotFrames, "a snapshot session keeps being served");
        Assert.Equal(2, rig.Host.RestartPending);
        Assert.Equal(3, rig.Host.PlayerCount);
        Assert.True(rig.Writer.LegacySequenceExhausted);
        Assert.Equal(int.MaxValue, rig.Writer.CurrentSeq);
        Assert.True(rig.Writer.LegacySlotCount > 0, "no reset before the sessions ended");

        // Their Left events drain in Poll, then the serve pass resets the writer and opens capture 1.
        rig.Step();

        Assert.Equal(0, rig.Host.RestartPending);
        Assert.Equal(1, rig.Host.PlayerCount);
        Assert.False(rig.Writer.LegacySequenceExhausted);
        Assert.Equal(1, rig.Writer.CurrentSeq);
        Assert.Equal(0, rig.Writer.LegacySlotCount);
        Assert.True(snapshot.Joined);
        Assert.Null(snapshot.RejectReason);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void JoinsAreRejectedWithRestartWhileClosed(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        RawRebuildClient legacy = rig.ConnectLegacy(out NetConnectionId legacyConnection);
        rig.Steps(4);
        rig.Transport.HoldLeaveOf(legacyConnection);
        // Connected on the exhaustion tick, so its Hello reaches the first Poll after it.
        RawRebuildClient early = rig.ConnectLegacy(out _);

        rig.Exhaust();
        Assert.False(early.Joined);
        Assert.Null(early.RejectReason);

        rig.Step();

        Assert.Equal(ReplicationFailure.RestartToken, early.RejectReason);
        Assert.False(early.Joined);
        Assert.Equal(ReplicationFailure.RestartToken, legacy.RejectReason);
        Assert.Equal(1, rig.Host.RestartPending);

        RawRebuildClient during = rig.ConnectLegacy(out _);
        rig.Steps(3);

        Assert.Equal(ReplicationFailure.RestartToken, during.RejectReason);
        Assert.False(during.Joined);
        ConnectRefusal refusal = ConnectRefusal.Read(during.RejectReason!);
        Assert.Equal(DisconnectReason.ReplicationRestart, refusal.Reason);
        Assert.Equal(ConnectRefusal.RetryRule.Backoff, refusal.Retry);
        Assert.Equal(1, rig.Host.PlayerCount);

        rig.Transport.ReleaseLeaves();
        rig.Step();
        Assert.Equal(0, rig.Host.RestartPending);
        Assert.Equal(1, rig.Writer.CurrentSeq);

        RawRebuildClient after = rig.ConnectLegacy(out _);
        rig.Steps(3);

        Assert.True(after.Joined);
        Assert.Null(after.RejectReason);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ResetWaitsForEveryLeft(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        RawRebuildClient legacy = rig.ConnectLegacy(out _);
        V2Client v2 = rig.ConnectV2(out NetConnectionId v2Connection);
        rig.Steps(8);
        Assert.NotEmpty(v2.Deltas);
        rig.Transport.HoldLeaveOf(v2Connection);
        rig.Exhaust();
        int sendsBefore = rig.Transport.Sends.Count;

        rig.Step();
        AssertWaiting();
        rig.Steps(3);
        AssertWaiting();

        List<(NetConnectionId Target, byte[] Payload)> toV2 =
            rig.Transport.Sends.Skip(sendsBefore).Where(s => s.Target == v2Connection).ToList();
        Assert.Contains(toV2, s => s.Payload[0] == (byte)SessionOpcode.Reject);
        Assert.DoesNotContain(toV2, s => s.Payload[0] == (byte)SessionOpcode.Data);

        rig.Transport.ReleaseLeaves();
        rig.Step();

        Assert.Equal(0, rig.Host.RestartPending);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.False(rig.Writer.LegacySequenceExhausted);
        Assert.Equal(1, rig.Writer.CurrentSeq);

        void AssertWaiting()
        {
            Assert.Equal(ReplicationFailure.RestartToken, legacy.RejectReason);
            Assert.Equal(ReplicationFailure.RestartToken, v2.RejectReason);
            Assert.Equal(1, rig.Host.RestartPending);
            Assert.Equal(1, rig.Host.PlayerCount);
            Assert.True(rig.Writer.LegacySequenceExhausted);
            Assert.Equal(int.MaxValue, rig.Writer.CurrentSeq);
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void FreshClientsGetCaptureOneAndBaselineMinusOne(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        RawRebuildClient old = rig.ConnectLegacy(out _);
        rig.Steps(4);
        rig.Exhaust();

        rig.Step();

        Assert.Equal(ReplicationFailure.RestartToken, old.RejectReason);
        Assert.Equal(1, rig.Writer.CurrentSeq);
        Assert.Equal(0, rig.Host.PlayerCount);

        RawRebuildClient fresh = rig.ConnectLegacy(out _);
        int seqAtFirstDelta = 0;
        for (int i = 0; i < 8 && LegacyDeltas(fresh).Count == 0; i++)
        {
            rig.Step();
            seqAtFirstDelta = rig.Writer.CurrentSeq;
        }

        List<LegacyDeltaHeader> deltas = LegacyDeltas(fresh);
        Assert.NotEmpty(deltas);
        Assert.Equal(-1, deltas[0].Baseline);
        Assert.Equal(seqAtFirstDelta, deltas[0].Snapshot);
        Assert.InRange(deltas[0].Snapshot, 2, 9);
        rig.Steps(2);
        deltas = LegacyDeltas(fresh);
        for (int i = 1; i < deltas.Count; i++) Assert.Equal(deltas[i - 1].Snapshot, deltas[i].Baseline);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void FreshV2EpochsExceedOldHighWater(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        rig.Host.SeedEpochs(40);
        V2Client old = rig.ConnectV2(out _);
        rig.Steps(8);
        Assert.Equal(41UL, Assert.Single(old.Offers).Offer.Epoch);
        Assert.NotEmpty(old.Deltas);
        rig.Exhaust();
        ulong highWater = rig.Host.EpochHighWater;

        rig.Step();

        Assert.Equal(ReplicationFailure.RestartToken, old.RejectReason);
        Assert.Equal(1, rig.Writer.CurrentSeq);
        Assert.Equal(highWater, rig.Host.EpochHighWater);

        V2Client fresh = rig.ConnectV2(out _);
        rig.Steps(8);

        Assert.True(fresh.Joined);
        ReplicationModeOffer offer = Assert.Single(fresh.Offers).Offer;
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Assert.Equal(highWater + 1, offer.Epoch);
        Assert.Equal(highWater + 1, Assert.Single(fresh.Keyframes).Id.Epoch);
        Assert.NotEmpty(fresh.Deltas);
        Assert.All(fresh.Deltas, d => Assert.Equal(highWater + 1, d.Id.Epoch));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EpochAllocatorExhaustionKeepsAdmissionClosed(RebuildHostKind kind)
    {
        RestartRig rig = RestartRig.Create(kind);
        rig.Host.SeedEpochs(ulong.MaxValue - 1);
        V2Client last = rig.ConnectV2(out _);
        rig.Steps(8);
        Assert.Equal(ulong.MaxValue, Assert.Single(last.Offers).Offer.Epoch);
        Assert.NotEmpty(last.Deltas);

        RawRebuildClient late = rig.ConnectLegacy(out _);
        rig.Steps(3);

        Assert.Equal(ReplicationFailure.RestartToken, late.RejectReason);
        Assert.False(late.Joined);
        Assert.Null(last.RejectReason);

        rig.Exhaust();
        rig.Step();

        Assert.Equal(ReplicationFailure.RestartToken, last.RejectReason);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Equal(1, rig.Writer.CurrentSeq);

        RawRebuildClient freshLegacy = rig.ConnectLegacy(out _);
        V2Client freshV2 = rig.ConnectV2(out _);
        rig.Steps(4);

        Assert.Equal(ReplicationFailure.RestartToken, freshLegacy.RejectReason);
        Assert.Equal(ReplicationFailure.RestartToken, freshV2.RejectReason);
        Assert.False(freshLegacy.Joined);
        Assert.False(freshV2.Joined);
        Assert.Equal(0, rig.Host.PlayerCount);
        Assert.Equal(ulong.MaxValue, rig.Host.EpochHighWater);
    }

    private static List<LegacyDeltaHeader> LegacyDeltas(RawRebuildClient client) => client.Frames
        .Where(f => f.Kind == MoveProtocol.ServerFrameKind.Delta)
        .Select(f =>
        {
            Assert.True(MoveProtocol.TryDecodeSnapshotFrame(f.Payload, out _, out _, out byte[] body), "frame decodes");
            return LegacyDeltaWire.ReadHeader(body);
        })
        .ToList();
}

/// <summary>A host over a <see cref="RestartTransport"/> with legacy, format 2 and snapshot-only raw clients, stepped
/// together one host tick at a time.</summary>
internal sealed class RestartRig
{
    private readonly List<Action> polls = new();
    private int connections;

    private RestartRig(RestartTransport transport, RebuildHost host)
    {
        Transport = transport;
        Host = host;
    }

    public RestartTransport Transport { get; }
    public RebuildHost Host { get; }
    public AoiDeltaReplicator Writer => Host.Writer!;

    public static RestartRig Create(RebuildHostKind kind)
    {
        var transport = new RestartTransport();
        return new RestartRig(transport, RebuildHost.Create(kind, transport, allowUnreliable: true));
    }

    public RawRebuildClient ConnectLegacy(out NetConnectionId connection)
    {
        var client = new RawRebuildClient(Next(out connection), requestRebuild: false);
        polls.Add(client.Poll);
        return client;
    }

    public V2Client ConnectV2(out NetConnectionId connection)
    {
        var client = new V2Client(Next(out connection), requestRebuild: true);
        polls.Add(client.Poll);
        return client;
    }

    public SnapshotOnlyClient ConnectSnapshot()
    {
        var client = new SnapshotOnlyClient(Next(out _));
        polls.Add(client.Poll);
        return client;
    }

    public void Step()
    {
        Host.Poll();
        TickOnly();
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++) Step();
    }

    /// <summary>A host tick with no Poll before it, so no Left can drain.</summary>
    public void TickOnly()
    {
        Host.Tick();
        foreach (Action poll in polls) poll();
    }

    /// <summary>Seeds the shared writer one below the last sequence and steps once, so the final sequence is captured
    /// and served.</summary>
    public void Exhaust()
    {
        Writer.SeedLegacySequenceForTest(int.MaxValue - 1);
        Step();
        Assert.True(Writer.LegacySequenceExhausted);
        Assert.Equal(int.MaxValue, Writer.CurrentSeq);
    }

    // The hub numbers its clients 1, 2, 3 in creation order.
    private INetTransport Next(out NetConnectionId connection)
    {
        connection = new NetConnectionId(++connections);
        return Transport.Hub.CreateClient();
    }
}

/// <summary>A server transport over the in-memory hub with known limits that records every send by connection and can
/// hold back the Disconnected event of chosen connections, so a test controls when each Left reaches the
/// host.</summary>
internal sealed class RestartTransport : INetTransport
{
    private readonly INetTransport inner;
    private readonly HashSet<NetConnectionId> heldLeaves = new();
    private readonly List<NetEvent> held = new();
    private readonly Queue<NetEvent> released = new();

    public RestartTransport()
    {
        Hub = new InMemoryTransportHub();
        inner = Hub.Server;
    }

    public InMemoryTransportHub Hub { get; }
    public List<(NetConnectionId Target, byte[] Payload)> Sends { get; } = new();

    public void HoldLeaveOf(NetConnectionId connection) => heldLeaves.Add(connection);

    /// <summary>Delivers every held Disconnected event at the next dequeue and holds nothing further.</summary>
    public void ReleaseLeaves()
    {
        foreach (NetEvent ev in held) released.Enqueue(ev);
        held.Clear();
        heldLeaves.Clear();
    }

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) => 1400;

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        Sends.Add((target, payload.ToArray()));
        inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (released.TryDequeue(out ev)) return true;
        while (inner.TryDequeueEvent(out ev))
        {
            if (ev.Type == NetEventType.Disconnected && heldLeaves.Contains(ev.Connection))
            {
                held.Add(ev);
                continue;
            }
            return true;
        }
        return false;
    }

    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) =>
        inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}

/// <summary>A raw client that joins and advertises nothing, so the host serves it full snapshots, never the shared
/// delta writer.</summary>
internal sealed class SnapshotOnlyClient
{
    private readonly NetClient net;

    public SnapshotOnlyClient(INetTransport transport) => net = new NetClient(transport, TestHandshake.Wire());

    public bool Joined { get; private set; }
    public string? RejectReason { get; private set; }
    public int Frames { get; private set; }

    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    break;
                case ClientSessionEventKind.Rejected:
                    RejectReason = ev.RejectReason;
                    break;
                case ClientSessionEventKind.Data:
                    Frames++;
                    break;
            }
        }
    }
}
