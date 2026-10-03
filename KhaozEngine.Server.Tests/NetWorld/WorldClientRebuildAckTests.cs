using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Format 2 acceptance on a real <see cref="WorldClient"/>: only an accepted projection ingests, once, with its own
/// movement ack, while stale, duplicate and ambiguous datagrams change nothing. Routine acks coalesce after the drain
/// and repeat while idle, and keyframe acks travel reliably. Sequence wrap and epoch retirement use seeded writers.
/// </summary>
public class WorldClientRebuildAckTests
{
    private static readonly Vector3 LocalSpawn = new(5f, 0f, 5f);
    private static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    private static RebuildSource SourceWithAvatarAndRemote()
    {
        var source = new RebuildSource();
        source.Spawn(RebuildSource.Owner, -1, 0, LocalSpawn);
        source.Spawn(2, 16, 1, new Vector3(7f, 0f, 7f));
        return source;
    }

    private static int RoutineAcks(ScriptedRebuildServer s) =>
        s.Tap.Controls(RebuildControlKind.Acknowledge).Count(c => c.Reliability == ClientTap.Unreliable);

    private static ReplicationPacketId LastRoutineAck(ScriptedRebuildServer s)
    {
        RebuildClientControl ack = s.Tap.Controls(RebuildControlKind.Acknowledge)
            .Last(c => c.Reliability == ClientTap.Unreliable).Control;
        return new ReplicationPacketId(ack.Epoch, ack.SnapshotSequence);
    }

    [Fact]
    public void AcceptedFrameIngestsExactlyOnceWithItsMovementAck()
    {
        var s = new ScriptedRebuildServer();
        RebuildSource source = SourceWithAvatarAndRemote();
        s.Establish(source, 5);
        int[] seqs = Enumerable.Range(0, 4).Select(_ => s.Client.SendInput(Forward)).ToArray();
        Assert.Equal(4, s.Client.RebuildDiagnosticsForTest.PendingPredictionCommands);
        WorldClientRebuildDiagnostics before = s.Client.RebuildDiagnosticsForTest;
        source.MovementAck = seqs[1];
        source.SetPad(2, 16, 2);
        ReplicationDeltaPacket delta = source.Build();
        byte[] frame = source.DeltaFrame(delta);

        s.Deliver(frame);

        WorldClientRebuildDiagnostics after = s.Client.RebuildDiagnosticsForTest;
        Assert.Equal(before.IngestCount + 1, after.IngestCount);
        Assert.Equal(before.AcceptedCount + 1, after.AcceptedCount);
        Assert.Equal(seqs[1], after.LastMovementAck);
        Assert.Equal(2, after.PendingPredictionCommands);
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(new PadState { Length = 16, Fill = 2 }, pad);
        Assert.True(s.Client.DeltaRebuildForTest!.TryGetRetainedForTest(delta.Id, out ReplicationProjection retained));
        ProjectionDump.AssertEqual(ProjectionDump.Of(source.Retained(delta.Id)), ProjectionDump.Of(retained));
        Vector3 predicted = s.Client.LocalPredictedState.Position;

        s.Deliver(frame);

        Assert.Equal(after, s.Client.RebuildDiagnosticsForTest);
        Assert.Equal(predicted, s.Client.LocalPredictedState.Position);
    }

    [Fact]
    public void StaleFrameCannotPrunePredictionOrAddSamples()
    {
        var s = new ScriptedRebuildServer();
        RebuildSource source = SourceWithAvatarAndRemote();
        s.Establish(source, 5);
        int[] seqs = Enumerable.Range(0, 3).Select(_ => s.Client.SendInput(Forward)).ToArray();
        int samplesAtKeyframe = s.Client.ViewForTest.PresentationArraysForTest().Count();
        source.MovementAck = seqs[0];
        source.Move(2, new Vector3(7f, 0f, 8f));
        byte[] older = source.DeltaFrame(source.Build());
        source.MovementAck = seqs[1];
        source.Move(2, new Vector3(7f, 0f, 9f));
        byte[] newer = source.DeltaFrame(source.Build());

        s.Deliver(newer);

        WorldClientRebuildDiagnostics before = s.Client.RebuildDiagnosticsForTest;
        Assert.Equal(seqs[1], before.LastMovementAck);
        Assert.Equal(1, before.PendingPredictionCommands);
        int samples = s.Client.ViewForTest.PresentationArraysForTest().Count();
        Assert.True(samples > samplesAtKeyframe, "an accepted ingest records samples");
        int publications = s.Client.DeltaRebuildForTest!.PublicationCountForTest;
        Vector3 predicted = s.Client.LocalPredictedState.Position;
        Assert.True(s.Client.TryGetComponent(2, out ReplicatedPosition remote));

        s.Deliver(older);
        s.Deliver(newer);

        Assert.Equal(before, s.Client.RebuildDiagnosticsForTest);
        Assert.Equal(samples, s.Client.ViewForTest.PresentationArraysForTest().Count());
        Assert.Equal(publications, s.Client.DeltaRebuildForTest!.PublicationCountForTest);
        Assert.Equal(predicted, s.Client.LocalPredictedState.Position);
        Assert.True(s.Client.TryGetComponent(2, out ReplicatedPosition after));
        Assert.Equal(remote.Value, after.Value);
        Assert.Equal(new Vector3(7f, 0f, 9f), after.Value);
    }

    [Fact]
    public void AckCoalescesAfterDrainAndRepeatsWhenIdle()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        s.Establish(source, 5);
        Assert.Equal(0, RoutineAcks(s));
        source.SetPad(2, 16, 2);
        ReplicationDeltaPacket first = source.Build();
        source.SetPad(2, 16, 3);
        ReplicationDeltaPacket second = source.Build();

        s.Send(source.DeltaFrame(first), ClientTap.Unreliable);
        s.Send(source.DeltaFrame(second), ClientTap.Unreliable);
        s.Pump();

        Assert.Equal(3, s.Client.RebuildDiagnosticsForTest.AcceptedCount);
        Assert.Equal(1, RoutineAcks(s));
        Assert.Equal(second.Id, LastRoutineAck(s));
        for (int idle = 1; idle <= 3; idle++)
        {
            s.Pump();
            Assert.Equal(1 + idle, RoutineAcks(s));
            Assert.Equal(second.Id, LastRoutineAck(s));
        }
        s.Pump(0f);
        Assert.Equal(4, RoutineAcks(s));
        s.Pump(ScriptedRebuildServer.Dt / 2f);
        Assert.Equal(4, RoutineAcks(s));
        s.Pump(ScriptedRebuildServer.Dt / 2f);
        Assert.Equal(5, RoutineAcks(s));
        Assert.All(s.Tap.Sent.Where(p => p.Reliability == ClientTap.Unreliable),
            p => Assert.Equal(RebuildProtocol.AckBytes, p.Payload.Length));
    }

    [Fact]
    public void KeyframeAckIsReliable()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 2000, 1);
        s.Offer(5);
        s.Pump(0f);
        (RebuildClientControl accept, NetChannelReliability acceptReliability) =
            Assert.Single(s.Tap.Controls(RebuildControlKind.Accept));
        Assert.Equal(5UL, accept.Epoch);
        Assert.Equal(ClientTap.Reliable, acceptReliability);
        source.Start(5);
        ReplicationDeltaPacket key = source.Build(keyframe: true);

        foreach (byte[] frame in source.ChunkFrames(key)) s.Send(frame);
        s.Pump(0f);

        (RebuildClientControl keyframeAck, NetChannelReliability reliability) =
            Assert.Single(s.Tap.Controls(RebuildControlKind.Acknowledge));
        Assert.Equal(key.Id, new ReplicationPacketId(keyframeAck.Epoch, keyframeAck.SnapshotSequence));
        Assert.Equal(NetChannelReliability.ReliableOrdered, reliability);
        Assert.False(s.Client.RebuildStreamForTest!.RecoveryActive);

        s.Pump();

        Assert.Equal(1, RoutineAcks(s));
        Assert.Equal(key.Id, LastRoutineAck(s));
        Assert.Equal(1 + RebuildProtocol.AckBytes, s.Client.RebuildDiagnosticsForTest.MaxTransportPayloadBytes);
    }

    [Fact]
    public void SeededWrapAndRetiredEpochNeverReingest()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 16, 1);
        s.Offer(5);
        s.Pump(0f);
        source.Start(5, nextSequence: uint.MaxValue - 1);
        ReplicationDeltaPacket key = source.Build(keyframe: true);
        foreach (byte[] frame in source.ChunkFrames(key)) s.Send(frame);
        s.Pump(0f);
        source.Ack(key.Id);
        Assert.Equal(new ReplicationPacketId(5, uint.MaxValue - 1), key.Id);

        byte[][] wrapped = new byte[3][];
        for (int i = 0; i < wrapped.Length; i++)
        {
            source.SetPad(2, 16, (byte)(10 + i));
            ReplicationDeltaPacket delta = source.Build();
            wrapped[i] = source.DeltaFrame(delta);
            s.Deliver(wrapped[i]);
            Assert.Equal(delta.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        }
        Assert.Equal(new uint[] { uint.MaxValue, 0, 1 }, wrapped.Select(f => DeltaSequence(f)).ToArray());
        WorldClientRebuildDiagnostics wrappedDiagnostics = s.Client.RebuildDiagnosticsForTest;
        Assert.Equal(4, wrappedDiagnostics.AcceptedCount);

        s.Deliver(wrapped[0]);
        s.Deliver(DeltaFrame.With(wrapped[2], snapshot: 1u + 0x80000000u));

        Assert.Equal(wrappedDiagnostics, s.Client.RebuildDiagnosticsForTest);
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
        Assert.Null(s.Client.RebuildStreamForTest!.Failure);

        var repair = new RebuildSource();
        repair.Spawn(2, 16, 20);
        repair.Start(6);
        ReplicationDeltaPacket key6 = repair.Build(keyframe: true);
        foreach (byte[] frame in repair.ChunkFrames(key6)) s.Send(frame);
        s.Pump(0f);
        Assert.Equal(key6.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        Assert.Equal(6UL, s.Client.ReplicationSelection.Epoch);
        WorldClientRebuildDiagnostics repaired = s.Client.RebuildDiagnosticsForTest;

        source.SetPad(2, 16, 99);
        s.Deliver(source.DeltaFrame(source.Build()));
        foreach (byte[] frame in source.ChunkFrames(key)) s.Send(frame);
        s.Pump(0f);

        Assert.Equal(repaired, s.Client.RebuildDiagnosticsForTest);
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(20, pad.Fill);
        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
    }

    private static uint DeltaSequence(byte[] frame) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(23));
}
