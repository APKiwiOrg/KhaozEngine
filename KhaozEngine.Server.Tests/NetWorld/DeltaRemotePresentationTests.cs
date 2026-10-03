using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Remote presentation at client phase offsets 0, 1 and 3 subticks against both heads. The mover runs the prediction
/// script while the observer loses the state sent at tick 22, receives the one sent at tick 23 eight subticks late,
/// and at tick 26 sees its reported limit fall from 512 to 400, so the server renegotiates and runs one keyframe
/// repair. Accepted ids and reconstructed positions are checked at each accept, then every presentation event against
/// <see cref="RemoteOracle"/> and the pinned hold frames.
/// </summary>
public sealed class DeltaRemotePresentationTests
{
    /// <summary>
    /// Hold frames derived by hand from the schedule. The observer polls state built at tick k on frame 2k at phases
    /// 0 and 1 and on frame 2k - 1 at phase 3, where the first frame is at subtick phase. A sample ingested on frame n is
    /// stamped n frames of 1/60 s, and frame j renders at (j + 1 - 4) frames, the two-tick delay being four frames. So
    /// frame j holds when j - 3 exceeds the newest ingest frame. The only gap longer than the delay is tick 21 to tick
    /// 24 (tick 22 lost, tick 23 stale on arrival): newest ingest frame 42, next 48 at phases 0 and 1, and 41 then 47
    /// at phase 3. The repair gap from tick 25 to the keyframe frozen at tick 27 is exactly the delay and never holds,
    /// and the teleport cut leaves a single sample ahead of render time.
    /// </summary>
    public static IReadOnlyList<int> PinnedHoldFrames(int phase) => phase == 3 ? new[] { 45, 46 } : new[] { 46, 47 };

    /// <summary>The server ticks whose state the observer ingests, from the schedule: the legacy snapshot at tick 1,
    /// the keyframe at tick 3, every datagram from tick 4 except 22 and 23, no state at 26 while the replacement offer
    /// is answered, the repair keyframe frozen at 27, then every datagram.</summary>
    public static IEnumerable<int> ExpectedIngestTicks(int lastTick) => new[] { 1, 3 }
        .Concat(Enumerable.Range(4, lastTick - 3).Where(t => t is not (22 or 23 or 26)));

    [Theory]
    [MemberData(nameof(DeltaPredictionPhaseTests.Phases), MemberType = typeof(DeltaPredictionPhaseTests))]
    public void RemoteWalkerAndTurnerRenderAtEachPhase(bool sharded, int phase, bool consumer)
    {
        MoveTuning tuning = consumer ? DeltaPredictionPhaseTests.Consumer : MoveTuning.Default;
        DeltaFault[] faults =
        {
            DeltaFaultSchedule.DropState(DeltaFaultSchedule.StateOrdinalSentAt(22)),
            DeltaFaultSchedule.DelayState(DeltaFaultSchedule.StateOrdinalSentAt(23),
                DeltaFaultSchedule.Subtick(23) + DeltaFaultSchedule.MaxOrdinaryDelaySubticks),
        };
        var rig = new DeltaReliabilityRig(sharded, phase, tuning, faults, new DeltaRigOptions
        {
            MoverScript = DeltaPredictionPhaseTests.Script,
            FaultedClient = RigRole.Observer,
        });
        rig.At(26, () => rig.SetServerLimit(RigRole.Observer, 400));
        DeltaPredictionPhaseTests.ScheduleTeleport(rig);
        // Reconstructed position at each accept: the client's public read equals the authoritative position the
        // accepted state was built from, before presentation touches it.
        long moverId = 0;
        int seen = 0;
        rig.AfterPoll += (c, _) =>
        {
            if (c.Role != RigRole.Observer) return;
            List<DeltaIngestRow> all = rig.Trace.IngestsOf(c.Index).ToList();
            if (all.Count == seen) return;
            seen = all.Count;
            if (moverId == 0) moverId = rig.NetIdOf(RigRole.Mover);
            Assert.True(rig.TryGetTruth(RigRole.Observer, all[^1].SourceTick, moverId, out RemoteSample truth));
            Assert.True(c.Client.TryGetComponent(moverId, out ReplicatedPosition position));
            Assert.Equal(truth.Position, position.Value);
            Assert.True(c.Client.TryGetComponent(moverId, out MovementState movement));
            Assert.Equal(truth.FacingYawQ, movement.FacingYawQ);
        };
        rig.Run();

        RigClient observer = rig[RigRole.Observer];
        List<DeltaIngestRow> ingests = rig.Trace.IngestsOf(observer.Index).ToList();
        List<DeltaFrameRow> rows = rig.Trace.FramesOf(observer.Index).ToList();
        int lastTick = 179;
        Assert.Equal(ExpectedIngestTicks(lastTick), ingests.Select(i => i.SourceTick));
        Assert.Equal(new[] { 3, 27 }, ingests.Where(i => i.Keyframe).Select(i => i.SourceTick));
        Assert.True(ingests.Single(i => i.SourceTick == 27).Id!.Value.Epoch > ingests.Single(i => i.SourceTick == 25).Id!.Value.Epoch);
        Assert.Equal(377, observer.Client.RebuildStreamForTest!.ReassemblyWidth);
        FaultSend offer = observer.Downstream.Sends.Last(s => s.Kind == FaultFrameKind.ReplicationMode);
        Assert.Equal(DeltaFaultSchedule.Subtick(26), offer.Subtick);
        Assert.All(observer.Downstream.Sends.Where(s => s.Subtick >= DeltaFaultSchedule.Subtick(26)
            && s.Kind is FaultFrameKind.RebuildDelta or FaultFrameKind.KeyframeChunk), s => Assert.True(s.Payload.Length <= 400));

        Assert.Equal(ingests.Count, seen);
        foreach (DeltaIngestRow ingest in ingests)
            Assert.Equal(phase == 3 ? (2 * ingest.SourceTick) - 1 : 2 * ingest.SourceTick, ingest.Frame);

        // Every presentation event against the oracle and the pinned hold frames.
        (List<int> oracleHolds, List<int> actualHolds) = AssertAgainstOracle(rig, RigRole.Observer, moverId,
            DeltaPredictionPhaseTests.PositionTolerance(tuning));
        Assert.Equal(PinnedHoldFrames(phase), oracleHolds);
        Assert.Equal(PinnedHoldFrames(phase), actualHolds);

        // The remote really walked, turned and was cut: its rendered heading and position changed over the run.
        List<DeltaRemoteRow> track = rig.Trace.RemotesOf(observer.Index, moverId).ToList();
        Assert.True(track.Select(r => r.Heading).Distinct().Count() >= 3);
        Assert.True(Vector3.Distance(track[0].Position, track[^1].Position) > 1f);
        rig.Trace.AssertMonotonicAccepted(observer.Index);
        rig.Trace.AssertMaxima(observer.Index, new DeltaRebuildOptions(), 512);
        Assert.Equal(WorldConnectionState.Connected, observer.Client.ConnectionState);
    }

    /// <summary>Feeds each of the observer's ingests to a <see cref="RemoteOracle"/> with the authoritative sample it
    /// was built from, then checks every presentation event: a frame before any sample has no remote, every later one
    /// renders the remote at the oracle's position within <paramref name="tolerance"/> with its exact heading, flags
    /// and hold. Returns the oracle's and the client's held frames.</summary>
    internal static (List<int> Oracle, List<int> Actual) AssertAgainstOracle(DeltaReliabilityRig rig, RigRole observer,
        long remoteId, float tolerance)
    {
        int index = rig[observer].Index;
        List<DeltaIngestRow> ingests = rig.Trace.IngestsOf(index).ToList();
        var oracle = new RemoteOracle(new WorldClientConfig().InterpolationDelayTicks, DeltaFaultSchedule.TickSeconds);
        var oracleHolds = new List<int>();
        var actualHolds = new List<int>();
        int cursor = 0;
        foreach (DeltaFrameRow row in rig.Trace.FramesOf(index))
        {
            for (; cursor < ingests.Count && ingests[cursor].Frame == row.Frame; cursor++)
            {
                Assert.True(rig.TryGetTruth(observer, ingests[cursor].SourceTick, remoteId, out RemoteSample sample));
                oracle.Ingest(ingests[cursor].Stamp, sample);
            }
            DeltaRemoteRow[] remote = rig.Trace.RemotesOf(index, remoteId).Where(r => r.Frame == row.Frame).ToArray();
            if (!oracle.HasSamples)
            {
                Assert.Empty(remote);
                continue;
            }
            DeltaRemoteRow actual = Assert.Single(remote);
            RemoteExpectation expected = oracle.Render(row.ClockAfter);
            Assert.True(Vector3.Distance(expected.Position, actual.Position) <= tolerance,
                $"frame {row.Frame}: rendered {actual.Position}, oracle {expected.Position}");
            Assert.Equal(MovementState.DecodeFacingYaw(expected.FacingYawQ), actual.Heading);
            Assert.Equal(expected.Grounded, actual.Grounded);
            Assert.Equal(expected.Swimming, actual.Swimming);
            if (expected.Held) oracleHolds.Add(row.Frame);
            if (actual.Held) actualHolds.Add(row.Frame);
        }
        Assert.Equal(ingests.Count, cursor);
        return (oracleHolds, actualHolds);
    }
}
