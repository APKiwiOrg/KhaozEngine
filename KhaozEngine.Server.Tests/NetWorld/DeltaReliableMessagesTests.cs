using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Reliable game messages and notices keep their channel, order and payload while format 2 state is lost, reordered
/// and repaired, on both heads. A reliable delay overlaps the keyframe repair, so chunks queue behind a game message
/// on the reliable channel, and a game message of kind 5 shows the replication chunk kind never reaches the game
/// handler.
/// </summary>
public sealed class DeltaReliableMessagesTests
{
    private const int Phase = 1;
    private const int First = 10;
    private const int Last = 60;
    private const ushort ReliableKind = 200;
    private const ushort UnreliableKind = 300;
    private const ushort ClientReliableKind = 100;
    private const ushort ClientUnreliableKind = 101;
    private const ushort ChunkKindAsGameKind = 5;
    private const int GameKindFiveTick = 33;

    public static TheoryData<bool> Heads => new() { false, true };

    [Theory]
    [MemberData(nameof(Heads))]
    public void ReliableNoticesAndGameEventsSurviveDeltaFaults(bool sharded)
    {
        // Game frames go reliable then unreliable each tick, so the reliable one sent at tick t is ordinal
        // 2 (t - 10) + 1 in both directions. The one sent at tick 40 waits seven subticks, holding the repair keyframe
        // and every later reliable frame behind it. States sent at ticks 13 and 14 are lost, the one sent at tick 20
        // is reordered behind 21, and routine acks 7 to 37 are lost, which forces the keyframe repair at tick 40.
        int reliableAt40 = (2 * (40 - First)) + 1;
        var faults = new List<DeltaFault>
        {
            DeltaFaultSchedule.DropState(DeltaFaultSchedule.StateOrdinalSentAt(13)),
            DeltaFaultSchedule.DropState(DeltaFaultSchedule.StateOrdinalSentAt(14)),
            DeltaFaultSchedule.DelayState(DeltaFaultSchedule.StateOrdinalSentAt(20), DeltaFaultSchedule.Subtick(20) + 7),
            new(reliableAt40, FaultDirection.ServerToClient, FaultFrameKind.GameMessage, FaultAction.Delay,
                DeltaFaultSchedule.Subtick(40) + 7),
            new(reliableAt40, FaultDirection.ClientToServer, FaultFrameKind.ClientGameMessage, FaultAction.Delay,
                DeltaFaultSchedule.Subtick(40) + 7),
        };
        faults.AddRange(DeltaFaultSchedule.DropRoutineAcks(7, 37));
        var rig = new DeltaReliabilityRig(sharded, Phase, MoveTuning.Default, faults,
            new DeltaRigOptions { InputStartTick = 2 });
        RigClient mover = rig[RigRole.Mover];
        var sentToClient = new List<(ushort Kind, string Payload)>();
        var sentUnreliable = new List<(ushort Kind, string Payload)>();
        var sentToServer = new List<(ushort Kind, string Payload)>();
        var sentServerUnreliable = new List<(ushort Kind, string Payload)>();
        var notices = new List<string>();
        for (int tick = First; tick <= Last; tick++)
        {
            int t = tick;
            rig.At(t, () =>
            {
                ushort kind = t == GameKindFiveTick ? ChunkKindAsGameKind : ReliableKind;
                Send(rig, mover.Slot, kind, $"reliable {t}", NetChannelReliability.ReliableOrdered, sentToClient);
                Send(rig, mover.Slot, UnreliableKind, $"unreliable {t}", NetChannelReliability.UnreliableSequenced,
                    sentUnreliable);
                Assert.True(mover.Client.SendGameMessage(ClientReliableKind, Encoding.UTF8.GetBytes($"up {t}"),
                    NetChannelReliability.ReliableOrdered));
                sentToServer.Add((ClientReliableKind, $"up {t}"));
                Assert.True(mover.Client.SendGameMessage(ClientUnreliableKind, Encoding.UTF8.GetBytes($"up~ {t}"),
                    NetChannelReliability.UnreliableSequenced));
                sentServerUnreliable.Add((ClientUnreliableKind, $"up~ {t}"));
                if (t % 5 == 0)
                {
                    rig.Host.BroadcastNotice(new ServerNotice(ServerNoticeKind.Custom, $"notice {t}"));
                    notices.Add($"notice {t}");
                }
            });
        }
        var received = new List<(ushort Kind, string Payload)>();
        var receivedNotices = new List<string>();
        var serverReceived = new List<(ushort Kind, string Payload)>();
        mover.Client.GameMessageReceived += (kind, payload) => received.Add((kind, Encoding.UTF8.GetString(payload)));
        mover.Client.NoticeReceived += notice => receivedNotices.Add(notice.Message);
        rig.Host.GameMessage += (slot, kind, payload) =>
        {
            if (slot == mover.Slot) serverReceived.Add((kind, Encoding.UTF8.GetString(payload)));
        };
        rig.Run();

        // Once-only, in order, intact, on the channel the sender chose.
        Assert.Equal(sentToClient, received.Where(m => m.Kind != UnreliableKind));
        Assert.Equal(sentUnreliable, received.Where(m => m.Kind == UnreliableKind));
        Assert.Equal(notices, receivedNotices);
        Assert.Equal(sentToServer, serverReceived.Where(m => m.Kind == ClientReliableKind));
        Assert.Equal(sentServerUnreliable, serverReceived.Where(m => m.Kind == ClientUnreliableKind));
        AssertChannels(mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.GameMessage));
        AssertChannels(mover.Upstream!.Sends.Where(s => s.Kind == FaultFrameKind.ClientGameMessage));
        Assert.All(mover.Downstream.Sends.Where(s => s.Kind == FaultFrameKind.Notice),
            s => Assert.Equal(NetChannelReliability.ReliableOrdered, s.Reliability));

        // The repair ran inside the window, its chunks queued behind the delayed reliable game message, and a game
        // message of kind 5 reached the handler exactly once while no chunk did.
        List<FaultForward> forwards = mover.Downstream.Forwards;
        int delayed = forwards.FindIndex(f => f.Kind == FaultFrameKind.GameMessage && f.Ordinal == reliableAt40);
        int chunk = forwards.FindIndex(f => f.Kind == FaultFrameKind.KeyframeChunk && f.SentSubtick == DeltaFaultSchedule.Subtick(40));
        Assert.True(delayed >= 0 && chunk > delayed, "the repair chunk waits behind the delayed reliable game message");
        Assert.Equal(DeltaFaultSchedule.Subtick(40) + 7, forwards[chunk].Subtick);
        Assert.Contains(DeltaReliabilityAcceptanceTests.IdOf(mover.Downstream.Sends.First(s => s.Kind == FaultFrameKind.KeyframeChunk
            && s.Subtick == DeltaFaultSchedule.Subtick(40))), mover.Accepted);
        Assert.Single(received, m => m.Kind == ChunkKindAsGameKind);
        Assert.Equal(sentToClient.Count + sentUnreliable.Count, received.Count);
        Assert.Contains(mover.Downstream.Sends, s => s.Fault is { Action: FaultAction.Drop });
        rig.Trace.AssertMonotonicAccepted(mover.Index);
        Assert.Equal(WorldConnectionState.Connected, mover.Client.ConnectionState);
    }

    private static void Send(DeltaReliabilityRig rig, int slot, ushort kind, string payload,
        NetChannelReliability reliability, List<(ushort, string)> sent)
    {
        rig.Host.SendGameMessageTo(slot, kind, Encoding.UTF8.GetBytes(payload), reliability);
        sent.Add((kind, payload));
    }

    // Reliable and unreliable frames alternate, exactly as each was sent.
    private static void AssertChannels(IEnumerable<FaultSend> frames)
    {
        List<FaultSend> list = frames.ToList();
        Assert.Equal(2 * (Last - First + 1), list.Count);
        for (int i = 0; i < list.Count; i++)
            Assert.Equal(i % 2 == 0 ? NetChannelReliability.ReliableOrdered : NetChannelReliability.UnreliableSequenced,
                list[i].Reliability);
    }
}
