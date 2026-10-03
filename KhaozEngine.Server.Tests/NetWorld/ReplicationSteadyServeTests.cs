using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Steady format 2 serving after the keyframe acknowledgement: one routine unreliable state send per viewer per cadence
/// tick, no extra allowance on short sharded frames, and one shared world capture per capture tick across legacy and
/// format 2 viewers.
/// </summary>
public class ReplicationSteadyServeTests
{
    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [MemberData(nameof(Hosts))]
    public void SteadyServeSendsOneStatePerCadenceTick(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        long pad = rig.SpawnPad(4, 0x21);
        V2Client client = rig.JoinSteady();
        AoiDeltaReplicator writer = rig.Writer;
        var deltas = new List<RigSend>();

        foreach (byte fill in new byte[] { 0x22, 0x23, 0x24 })
        {
            rig.SetPad(pad, 4, fill);
            long scans = writer.WorldScanCount;
            List<RigSend> sends = rig.Step();

            Assert.Equal(scans + 1, writer.WorldScanCount);
            RigSend delta = Assert.Single(sends, s => s.IsDelta);
            Assert.Equal(NetChannelReliability.UnreliableSequenced, delta.Reliability);
            Assert.True(Pad.Contains(delta.DeltaBody, 4, fill), "the tick's current state");
            Assert.DoesNotContain(sends, s => s.IsChunk || s.IsOffer || s.IsLegacy);
            deltas.Add(delta);
        }

        Assert.Equal(3, deltas.Count);
        Assert.Equal(new uint[] { 3, 4, 5 }, deltas.Select(d => RebuildWire.ReadHeader(d.DeltaBody).Snapshot));
    }

    [Fact]
    public void ShortShardedFramesSpendNoExtraAllowance()
    {
        V2Rig rig = V2Rig.Create(RebuildHostKind.Sharded);
        long pad = rig.SpawnPad(4, 0x31);
        rig.JoinSteady();
        var deltasPerFrame = new List<int>();

        for (int frame = 0; frame < 8; frame++)
        {
            rig.SetPad(pad, 4, (byte)(0x40 + frame));   // state changes every frame, served only on the cadence
            deltasPerFrame.Add(rig.Step(RebuildHost.Dt / 4f).Count(s => s.IsDelta));
        }

        Assert.Equal(new[] { 0, 0, 0, 1, 0, 0, 0, 1 }, deltasPerFrame);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void MixedLegacyAndV2ViewersShareOneCapture(RebuildHostKind kind)
    {
        V2Rig rig = V2Rig.Create(kind);
        long pad = rig.SpawnPad(4, 0x51);
        V2Client legacy = rig.Connect(requestRebuild: false);
        V2Client v2 = rig.Connect();
        rig.Steps(5);   // join and offer, keyframe, first routine delta
        Assert.Empty(legacy.Offers);
        Assert.Single(v2.Keyframes);
        Assert.Single(v2.Deltas);
        AoiDeltaReplicator writer = rig.Writer;

        foreach (byte fill in new byte[] { 0x52, 0x53, 0x54 })
        {
            rig.SetPad(pad, 4, fill);
            long scans = writer.WorldScanCount;
            int legacyBefore = legacy.LegacyFrames.Count;
            int deltasBefore = v2.Deltas.Count;

            rig.Step();

            Assert.Equal(scans + 1, writer.WorldScanCount);
            Assert.Equal(legacyBefore + 1, legacy.LegacyFrames.Count);
            Assert.Equal(MoveProtocol.ServerFrameKind.Delta, legacy.LegacyFrames[^1].Kind);
            Assert.Equal(deltasBefore + 1, v2.Deltas.Count);
            Assert.True(Pad.Contains(v2.Deltas[^1].Body, 4, fill));
        }
        Assert.Equal(0, v2.LegacyFramesAfterFirstOffer);
    }
}
