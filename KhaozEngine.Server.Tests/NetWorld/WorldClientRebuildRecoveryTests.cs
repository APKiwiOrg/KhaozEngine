using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Format 2 recovery on a real <see cref="WorldClient"/>: missing baselines keep the published world and request
/// repair on the interval, retired chunks cannot restart an assembly, ignored traffic never refreshes valid-state
/// liveness or the recovery deadline, a restart reject reconnects through backoff with fresh receivers, and the
/// replacement offer contract. Also the client rows of the repair contract table.
/// </summary>
public class WorldClientRebuildRecoveryTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    private static int Count(ScriptedRebuildServer s, RebuildControlKind kind) => s.Tap.Controls(kind).Count;

    [Fact]
    public void MissingBaselineKeepsPublishedStateAndRequestsRepair()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 16, 1, new Vector3(7f, 0f, 7f));
        s.Establish(source, 5);
        source.SetPad(2, 16, 2);
        ReplicationDeltaPacket accepted = source.Build();
        byte[] acceptedFrame = source.DeltaFrame(accepted);
        s.Deliver(acceptedFrame);
        uint latest = accepted.Id.Sequence;
        byte[] miss = DeltaFrame.With(acceptedFrame, snapshot: latest + 10, baseline: latest + 5);
        WorldClientRebuildDiagnostics before = s.Client.RebuildDiagnosticsForTest;
        int publications = s.Client.DeltaRebuildForTest!.PublicationCountForTest;
        Assert.True(s.Client.DeltaRebuildForTest.TryGetRetainedForTest(accepted.Id, out ReplicationProjection published));

        s.Deliver(miss);

        // Nothing ingested or pruned. The only change is the 19-byte repair request now being the largest send.
        Assert.Equal(before with { MaxTransportPayloadBytes = 1 + RebuildProtocol.RepairBytes },
            s.Client.RebuildDiagnosticsForTest);
        Assert.Equal(publications, s.Client.DeltaRebuildForTest.PublicationCountForTest);
        Assert.Equal(accepted.Id, s.Client.DeltaRebuildForTest.LatestAcceptedId);
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(2, pad.Fill);
        Assert.Equal(published.Entities.Keys.Order(), s.Client.ViewForTest.Entities.Keys.Order());
        Assert.True(s.Client.RebuildStreamForTest!.RecoveryActive);
        (RebuildClientControl repair, NetChannelReliability reliability) = Assert.Single(s.Tap.Controls(RebuildControlKind.Repair));
        Assert.Equal(new RebuildClientControl(RebuildControlKind.Repair, 5, latest, latest + 5), repair);
        Assert.Equal(ClientTap.Reliable, reliability);

        s.Deliver(miss, dt: ScriptedRebuildServer.Dt);
        s.Pumps(28);

        Assert.Equal(1, Count(s, RebuildControlKind.Repair));
        Assert.All(s.Tap.Controls(RebuildControlKind.Acknowledge).Skip(1),
            ack => Assert.Equal(latest, ack.Control.SnapshotSequence));
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);

        s.Pump();

        Assert.Equal(2, Count(s, RebuildControlKind.Repair));
        source.SetPad(2, 16, 3);
        s.Deliver(source.DeltaFrame(source.Build()));
        Assert.False(s.Client.RebuildStreamForTest.RecoveryActive);
        Assert.Equal(before.IngestCount + 1, s.Client.RebuildDiagnosticsForTest.IngestCount);
    }

    [Fact]
    public void IgnoredDatagramsDoNotRefreshValidStateDeadline()
    {
        var s = new ScriptedRebuildServer(disconnectTimeout: 0.5f);
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        s.Establish(source, 5);
        byte[] accepted = source.DeltaFrame(source.Build());
        s.Deliver(accepted);

        for (int i = 0; i < 14; i++) s.Deliver(accepted, dt: ScriptedRebuildServer.Dt);
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
        for (int i = 0; i < 2; i++) s.Deliver(accepted, dt: ScriptedRebuildServer.Dt);

        Assert.Equal(WorldConnectionState.Disconnected, s.Client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, s.Client.DisconnectReason);
    }

    [Fact]
    public void IgnoredDatagramsDoNotExtendTheRecoveryDeadline()
    {
        var s = new ScriptedRebuildServer(disconnectTimeout: 0.5f);
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        s.Establish(source, 5);
        ReplicationDeltaPacket accepted = source.Build();
        byte[] acceptedFrame = source.DeltaFrame(accepted);
        s.Deliver(acceptedFrame);
        byte[] miss = DeltaFrame.With(acceptedFrame, snapshot: accepted.Id.Sequence + 10, baseline: accepted.Id.Sequence + 5);
        s.Deliver(miss);
        Assert.True(s.Client.RebuildStreamForTest!.RecoveryActive);

        for (int tick = 1; tick < 90; tick++)
        {
            s.Send(acceptedFrame, ClientTap.Unreliable);
            s.Deliver(miss, dt: ScriptedRebuildServer.Dt);
        }
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
        s.Deliver(miss, dt: ScriptedRebuildServer.Dt);

        Assert.Equal(WorldConnectionState.Disconnected, s.Client.ConnectionState);
        Assert.Equal(DisconnectReason.ReplicationRecoveryFailed, s.Client.DisconnectReason);
        int sent = s.Tap.Sent.Count;
        s.Pump();
        Assert.Equal(1, s.LeftCount);
        Assert.Equal(sent, s.Tap.Sent.Count);
    }

    [Fact]
    public void NegotiationWithoutKeyframeFailsTypedAtTick90()
    {
        var s = new ScriptedRebuildServer(disconnectTimeout: 0.5f);
        s.Offer(5);
        s.Pump(0f);
        Assert.True(s.Client.RebuildStreamForTest!.RecoveryActive);

        s.Pumps(89);
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
        s.Pump();

        Assert.Equal(DisconnectReason.ReplicationRecoveryFailed, s.Client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Disconnected, s.Client.ConnectionState);
    }

    [Fact]
    public void OldChunkCannotRestartAssembly()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 2000, 1);
        s.Offer(5);
        s.Pump(0f);
        source.Start(5);
        ReplicationDeltaPacket key5 = source.Build(keyframe: true);
        List<byte[]> chunks5 = source.ChunkFrames(key5);
        foreach (byte[] frame in chunks5) s.Send(frame);
        s.Pump(0f);
        Assert.Equal(key5.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        var repair = new RebuildSource();
        repair.Spawn(2, 2000, 2);
        repair.Start(6);
        ReplicationDeltaPacket key6 = repair.Build(keyframe: true);
        List<byte[]> chunks6 = repair.ChunkFrames(key6);
        Assert.True(chunks6.Count >= 3);

        s.Send(chunks6[0]);
        s.Send(chunks6[1]);
        s.Pump(0f);
        RebuildClientStream stream = s.Client.RebuildStreamForTest!;
        Assert.True(stream.AssemblyPending);
        Assert.Equal(6UL, s.Client.ReplicationSelection.Epoch);
        s.Send(chunks5[0]);
        s.Pump(0f);

        Assert.True(stream.AssemblyPending);
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(1, pad.Fill);
        foreach (byte[] frame in chunks6.Skip(2)) s.Send(frame);
        s.Pump(0f);
        Assert.Equal(key6.Id, s.Client.DeltaRebuildForTest.LatestAcceptedId);
        int publications = s.Client.DeltaRebuildForTest.PublicationCountForTest;

        s.Send(chunks5[0]);
        s.Send(chunks6[0]);
        s.Pump(0f);

        Assert.False(stream.AssemblyPending);
        Assert.Equal(publications, s.Client.DeltaRebuildForTest.PublicationCountForTest);
        Assert.True(s.Client.TryGetComponent(2, out pad));
        Assert.Equal(2, pad.Fill);
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RestartRejectReconnectsWithFreshView(RebuildHostKind kind)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, reconnecting: true);
        rig.Steps(6);
        WorldClient client = rig.Client;
        ReplicationSelection old = client.ReplicationSelection;
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, old.Mode);
        World oldWorld = client.WorldForTest;
        ClientReplicationView oldView = client.ViewForTest;
        ClientDeltaRebuild oldRebuild = client.DeltaRebuildForTest!;
        var transitions = new List<(WorldConnectionState State, DisconnectReason Reason)>();
        client.ConnectionStateChanged += state => transitions.Add((state, client.DisconnectReason));

        rig.Transport.Limit = 280;
        rig.Step();
        Assert.Equal((WorldConnectionState.Reconnecting, DisconnectReason.ReplicationRestart), transitions.First());
        rig.Transport.Limit = 1400;
        rig.Steps(12);

        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.Equal(2, rig.Taps.Count);
        Assert.NotSame(oldWorld, client.WorldForTest);
        Assert.NotSame(oldView, client.ViewForTest);
        Assert.NotSame(oldRebuild, client.DeltaRebuildForTest);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, client.ReplicationSelection.Mode);
        Assert.True(client.ReplicationSelection.Epoch > old.Epoch, "a fresh epoch from the server allocator");
        Assert.Equal(client.ReplicationSelection.Epoch, client.DeltaRebuildForTest!.LatestAcceptedId?.Epoch);
        Assert.True(client.RebuildDiagnosticsForTest.AcceptedCount >= 1);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ReplacementOfferAdoptsNewWidthAndKeepsPublishedWorld(RebuildHostKind kind)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, limit: 512);
        long pad = rig.SpawnPad(2000, 1);
        rig.Steps(8);
        WorldClient client = rig.Client;
        ulong oldEpoch = client.ReplicationSelection.Epoch;
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, client.ReplicationSelection.Mode);
        Assert.Equal(489, client.RebuildStreamForTest!.ReassemblyWidth);
        Assert.True(client.RebuildDiagnosticsForTest.AcceptedCount >= 2);
        List<(long Id, Vector3 Position)> published = Published(client);
        Assert.True(client.TryGetComponent(pad, out PadState before));
        Assert.Equal(1, before.Fill);
        int oldEpochAcks = rig.Tap.Controls(RebuildControlKind.Acknowledge).Count(a => a.Control.Epoch == oldEpoch);

        rig.Transport.Limit = 400;
        rig.SetPad(pad, 2000, 2);
        ulong newEpoch = 0;
        bool publishedNew = false;
        for (int step = 0; step < 6 && !publishedNew; step++)
        {
            rig.Step();
            client.AdvancePresentation(RebuildHost.Dt);
            if (step == 0)
            {
                newEpoch = client.ReplicationSelection.Epoch;
                Assert.True(newEpoch > oldEpoch, "the replacement offer's epoch is adopted");
                Assert.Equal(377, client.RebuildStreamForTest!.ReassemblyWidth);
            }
            publishedNew = client.DeltaRebuildForTest!.LatestAcceptedId?.Epoch == newEpoch;
            if (publishedNew) break;
            Assert.Equal(published, Published(client));
            Assert.True(client.TryGetComponent(pad, out PadState held));
            Assert.Equal(before, held);
        }

        Assert.True(publishedNew, "the new keyframe publishes");
        (RebuildClientControl accept, NetChannelReliability reliability) =
            Assert.Single(rig.Tap.Controls(RebuildControlKind.Accept), a => a.Control.Epoch == newEpoch);
        Assert.Equal(ClientTap.Reliable, reliability);
        Assert.Equal(newEpoch, accept.Epoch);
        Assert.Equal(oldEpochAcks, rig.Tap.Controls(RebuildControlKind.Acknowledge).Count(a => a.Control.Epoch == oldEpoch));
        ReplicationPacketId latest = client.DeltaRebuildForTest!.LatestAcceptedId!.Value;
        Assert.True(client.DeltaRebuildForTest.TryGetRetainedForTest(latest, out ReplicationProjection clientProjection));
        Assert.True(rig.Host.Writer!.TryGetRetainedProjectionForTest(0, latest, out ReplicationProjection serverProjection));
        ProjectionDump.AssertEqual(ProjectionDump.Of(serverProjection), ProjectionDump.Of(clientProjection));
        Assert.True(client.TryGetComponent(pad, out PadState after));
        Assert.Equal(new PadState { Length = 2000, Fill = 2 }, after);
        Assert.Equal(clientProjection.Entities.Keys.Order(), client.ViewForTest.Entities.Keys.Order());
    }

    public static TheoryData<string> BeyondLimits => new() { "packet cap", "infeasible width", "keyframe bytes" };

    [Theory]
    [MemberData(nameof(BeyondLimits))]
    public void ReplacementOfferBeyondLimitsRefusesWithPolicy(string limit)
    {
        var s = new ScriptedRebuildServer(limit == "keyframe bytes"
            ? new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxKeyframeBytes = 32 * 1024 } }
            : null);
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        s.Establish(source, 5);
        byte[] replacement = RebuildProtocol.EncodeModeOffer(RebuildProtocol.SelectedOffer(
            new ReplicationStreamOptions().StreamLimits(), limit == "packet cap" ? 600 : 512, 4, 6));
        if (limit == "infeasible width") System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(22), 100);

        s.Send(replacement);
        s.Pump(0f);

        Assert.Equal(DisconnectReason.ReplicationPolicyRefused, s.Client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Disconnected, s.Client.ConnectionState);
        Assert.Equal(1, Count(s, RebuildControlKind.Accept));
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(1, pad.Fill);
        s.Pump();
        Assert.Equal(1, s.LeftCount);
    }

    public static TheoryData<string> NonIncreasing => new() { "lower epoch", "equal epoch other cap", "mode 0 on live stream", "exact duplicate" };

    [Theory]
    [MemberData(nameof(NonIncreasing))]
    public void NonIncreasingReplacementEpochIsIncompatible(string offer)
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        ReplicationDeltaPacket key = s.Establish(source, 5);
        ReplicationSelection selection = s.Client.ReplicationSelection;

        switch (offer)
        {
            case "lower epoch": s.Offer(4); break;
            case "equal epoch other cap": s.Offer(5, packetCap: 400); break;
            case "mode 0 on live stream":
                s.Send(RebuildProtocol.EncodeModeOffer(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.DisabledServerPolicy)));
                break;
            default: s.Offer(5); break;
        }
        s.Pump(0f);

        Assert.Equal(1, Count(s, RebuildControlKind.Accept));
        Assert.Equal(selection, s.Client.ReplicationSelection);
        Assert.Equal(key.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        if (offer == "exact duplicate")
        {
            Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
            Assert.Equal(DisconnectReason.None, s.Client.DisconnectReason);
        }
        else
        {
            Assert.Equal(DisconnectReason.IncompatibleVersion, s.Client.DisconnectReason);
            Assert.Equal(WorldConnectionState.Disconnected, s.Client.ConnectionState);
        }
    }

    /// <summary>The one stream action a client trigger produced.</summary>
    public enum ClientAction
    {
        Ignore,
        RepairRequest,
        AdoptReplacement,
        IncompatibleDisconnect,
        CapacityDisconnect,
        PolicyDisconnect,
    }

    public static TheoryData<string, ClientAction> ClientRows => new()
    {
        { "malformed body", ClientAction.IncompatibleDisconnect },
        { "cannot retain within limits", ClientAction.CapacityDisconnect },
        { "replacement outside limits", ClientAction.PolicyDisconnect },
        { "replacement with infeasible width", ClientAction.PolicyDisconnect },
        { "valid greater replacement", ClientAction.AdoptReplacement },
        { "missing baseline", ClientAction.RepairRequest },
        { "half-range sequence", ClientAction.Ignore },
    };

    [Theory]
    [MemberData(nameof(ClientRows))]
    public void ClientRepairRowsMapToOneAction(string row, ClientAction expected)
    {
        bool capacity = row == "cannot retain within limits";
        var s = new ScriptedRebuildServer(capacity
            ? new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxEntities = 2 } }
            : null);
        var source = new RebuildSource();
        foreach (long id in new long[] { 2, 3, 4 }) source.Spawn(id, 16, 1);
        byte[]? frame = null;
        if (!capacity)
        {
            s.Establish(source, 5);
            source.SetPad(2, 16, 2);
            ReplicationDeltaPacket accepted = source.Build();
            frame = source.DeltaFrame(accepted);
            s.Deliver(frame);
        }
        int accepts = Count(s, RebuildControlKind.Accept);
        int repairs = Count(s, RebuildControlKind.Repair);
        uint latest = s.Client.DeltaRebuildForTest!.LatestAcceptedId?.Sequence ?? 0;

        switch (row)
        {
            case "malformed body": s.Deliver(DeltaFrame.With(frame!, snapshot: latest + 1, flags: 0x02)); break;
            case "cannot retain within limits":
                s.Offer(5);
                s.Pump(0f);
                accepts = Count(s, RebuildControlKind.Accept);
                source.Start(5);
                foreach (byte[] chunk in source.ChunkFrames(source.Build(keyframe: true))) s.Send(chunk);
                s.Pump(0f);
                break;
            case "replacement outside limits":
                s.Send(RebuildProtocol.EncodeModeOffer(RebuildProtocol.SelectedOffer(s.Stream.StreamLimits(), 600, 4, 6)));
                s.Pump(0f);
                break;
            case "replacement with infeasible width":
                byte[] infeasible = RebuildProtocol.EncodeModeOffer(RebuildProtocol.SelectedOffer(s.Stream.StreamLimits(), 512, 4, 6));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(infeasible.AsSpan(22), 100);
                s.Send(infeasible);
                s.Pump(0f);
                break;
            case "valid greater replacement": s.Offer(6); s.Pump(0f); break;
            case "missing baseline": s.Deliver(DeltaFrame.With(frame!, snapshot: latest + 10, baseline: latest + 5)); break;
            default: s.Deliver(DeltaFrame.With(frame!, snapshot: latest + 0x80000000u)); break;
        }

        var actions = new List<ClientAction>();
        if (s.Client.DisconnectReason == DisconnectReason.IncompatibleVersion) actions.Add(ClientAction.IncompatibleDisconnect);
        if (s.Client.DisconnectReason == DisconnectReason.ReplicationCapacityExceeded) actions.Add(ClientAction.CapacityDisconnect);
        if (s.Client.DisconnectReason == DisconnectReason.ReplicationPolicyRefused) actions.Add(ClientAction.PolicyDisconnect);
        if (Count(s, RebuildControlKind.Accept) > accepts) actions.Add(ClientAction.AdoptReplacement);
        if (Count(s, RebuildControlKind.Repair) > repairs) actions.Add(ClientAction.RepairRequest);
        if (actions.Count == 0) actions.Add(ClientAction.Ignore);
        Assert.Equal(expected, Assert.Single(actions));
        bool terminal = expected is ClientAction.IncompatibleDisconnect or ClientAction.CapacityDisconnect
            or ClientAction.PolicyDisconnect;
        Assert.Equal(terminal, s.Client.ConnectionState == WorldConnectionState.Disconnected);
        if (!capacity) Assert.True(s.Client.TryGetComponent(2, out PadState pad) && pad.Fill == 2, "published state kept");
    }

    private static List<(long Id, Vector3 Position)> Published(WorldClient client) =>
        client.Snapshot().Select(e => (e.Id.Value, e.Position)).OrderBy(e => e.Item1).ToList();
}
