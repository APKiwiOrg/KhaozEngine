using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Local prediction at client phase offsets 0, 1 and 3 subticks against both heads, with the consumer's approved
/// walk 2 and run 5 m/s and the engine default 6 and 12 m/s in separate rows. The mover walks, turns, stops, turns in
/// place, is teleported, runs and stops on a fixed tick script while the observer stands by. Every presentation event
/// is compared with <see cref="LocalOracle"/>, and one state datagram is delivered stale with an older movement ack.
/// </summary>
public sealed class DeltaPredictionPhaseTests
{
    public const int TeleportTick = 40;
    public static readonly Vector3 Destination = new(4f, 0f, -4f);

    /// <summary>The consumer's approved starting pace, Grimhollow continuous movement O5.</summary>
    public static MoveTuning Consumer => MoveTuning.Default with { WalkSpeed = 2f, RunSpeed = 5f };

    public static TheoryData<bool, int, bool> Phases()
    {
        var data = new TheoryData<bool, int, bool>();
        foreach (bool sharded in new[] { false, true })
            foreach (int phase in new[] { 0, 1, 3 })
                foreach (bool consumer in new[] { true, false })
                    data.Add(sharded, phase, consumer);
        return data;
    }

    /// <summary>The mover's command for server tick <paramref name="tick"/>. Forward is the input axis's Y.</summary>
    public static MoveCommand Script(int tick) => tick switch
    {
        < 10 => MoveCommand.Idle,
        < 20 => new MoveCommand(Vector2.UnitY, run: false, cameraYaw: 0f),
        < 30 => new MoveCommand(Vector2.UnitY, run: false, cameraYaw: MathF.PI / 2f, jump: false, faceCamera: true),
        < 35 => new MoveCommand(Vector2.Zero, run: false, cameraYaw: MathF.PI / 2f),
        < 45 => new MoveCommand(Vector2.Zero, run: false, cameraYaw: MathF.PI, jump: false, faceCamera: true),
        < 55 => new MoveCommand(Vector2.UnitY, run: true, cameraYaw: 0f),
        _ => MoveCommand.Idle,
    };

    /// <summary>Teleport offset plus six seconds at the configured maximum pace bounds every coordinate.</summary>
    public static float PositionTolerance(MoveTuning tuning)
    {
        float maxCoordinate = 4f + (6f * tuning.RunSpeed);
        return 180f * (MathF.BitIncrement(maxCoordinate) - maxCoordinate);
    }

    public static float HeadingTolerance =>
        (MovementState.FacingYawQuantum / 2f) + (MathF.BitIncrement(MathF.PI) - MathF.PI);

    /// <summary>Applies the script's teleport before <see cref="TeleportTick"/>, keeping height and heading.</summary>
    internal static void ScheduleTeleport(DeltaReliabilityRig rig) => rig.At(TeleportTick, () =>
    {
        int slot = rig[RigRole.Mover].Slot;
        Assert.True(rig.Host.TryGetPlayerState(slot, out PlayerMoveState state));
        state.Position = new Vector3(Destination.X, state.Position.Y, Destination.Z);
        rig.Host.SetPlayerState(slot, state, teleport: true);
    });

    /// <summary>The server tick that consumes a command submitted at <paramref name="subtick"/>: inputs precede a
    /// coincident server tick, and the queue drains one command per tick in order.</summary>
    public static int ConsumedTick(int subtick) =>
        (subtick + DeltaFaultSchedule.SubticksPerServerTick - 1) / DeltaFaultSchedule.SubticksPerServerTick;

    [Theory]
    [MemberData(nameof(Phases))]
    public void PredictionWalkTurnStopTeleportAtEachPhase(bool sharded, int phase, bool consumer)
    {
        MoveTuning tuning = consumer ? Consumer : MoveTuning.Default;
        // The datagram sent at tick 15 is released six subticks later, at the first poll after tick 16's state, alone
        // in that poll on every phase.
        const int StaleTick = 15;
        const int StaleRelease = (4 * StaleTick) + 6;
        var faults = new[] { DeltaFaultSchedule.DelayState(DeltaFaultSchedule.StateOrdinalSentAt(StaleTick), StaleRelease) };
        var rig = new DeltaReliabilityRig(sharded, phase, tuning, faults, new DeltaRigOptions { MoverScript = Script });
        RigClient mover = rig[RigRole.Mover];
        PlayerMoveState basis = default;
        rig.At(8, () => Assert.True(rig.Host.TryGetPlayerState(mover.Slot, out basis)));
        ScheduleTeleport(rig);
        rig.Run();

        float tol = PositionTolerance(tuning);
        float deadZone = PredictionSettings.Default.CorrectionDeadZone;
        List<DeltaSubmitRow> submits = rig.Trace.SubmitsOf(mover.Index).ToList();
        List<DeltaIngestRow> ingests = rig.Trace.IngestsOf(mover.Index).ToList();
        List<DeltaFrameRow> rows = rig.Trace.FramesOf(mover.Index).ToList();
        Assert.Equal(180 - 8, submits.Count);
        Assert.Equal(Enumerable.Range(0, submits.Count), submits.Select(s => s.Seq));
        DeltaIngestRow teleportIngest = ingests.First(i => i.SourceTick >= TeleportTick);
        Assert.Equal(TeleportTick, teleportIngest.SourceTick);
        var oracle = new LocalOracle(tuning, DeltaFaultSchedule.TickSeconds, DeltaFaultSchedule.FrameSeconds, basis,
            submits.Select(s => (s.Frame, s.Command, ConsumedTick(s.Subtick))).ToList(), TeleportTick, Destination,
            teleportIngest.Frame);

        // The movement ack bundled with each ingest is the last command the queue model says that tick consumed.
        foreach (DeltaIngestRow ingest in ingests.Where(i => i.Frame > submits[0].Frame))
        {
            int expectedAck = submits.Where(s => ConsumedTick(s.Subtick) <= ingest.SourceTick).Select(s => s.Seq)
                .DefaultIfEmpty(-1).Max();
            DeltaFrameRow at = rows[ingest.Frame];
            if (at.IngestsThisPoll == 1) Assert.Equal(expectedAck, at.MovementAck);
        }

        int firstSubmit = submits[0].Frame;
        int nextIngestAfterTeleport = ingests.First(i => i.Frame > teleportIngest.Frame).Frame;
        for (int f = firstSubmit; f < rows.Count; f++)
        {
            DeltaFrameRow row = rows[f];
            LocalExpectation expected = oracle.At(f);
            PlayerMoveState predicted = row.Predicted!.Value;
            Assert.True(Vector3.Distance(expected.Predicted.Position, predicted.Position) <= tol,
                $"frame {f}: predicted {predicted.Position}, oracle {expected.Predicted.Position}");
            Assert.True(DeltaReliabilityTrace.HeadingDelta(expected.Predicted.Move.FacingYaw, predicted.Move.FacingYaw)
                <= HeadingTolerance, $"frame {f}: heading {predicted.Move.FacingYaw}, oracle {expected.Predicted.Move.FacingYaw}");
            Assert.Equal(expected.Predicted.Grounded, predicted.Grounded);
            Assert.Equal(expected.Predicted.Swimming, predicted.Swimming);
            Vector3 rendered = row.Rendered!.Value;
            Assert.True(Vector3.Distance(expected.Rendered, rendered) <= tol + deadZone,
                $"frame {f}: rendered {rendered}, oracle {expected.Rendered}");
            Assert.True(DeltaReliabilityTrace.HeadingDelta(expected.Predicted.Move.FacingYaw, row.Heading) <= HeadingTolerance);
            Assert.Equal(expected.Predicted.Grounded, row.Grounded);
            bool teleportError = f >= teleportIngest.Frame && f < nextIngestAfterTeleport;
            if (!teleportError)
                Assert.True(row.ReconcileError <= tol, $"frame {f}: reconcile error {row.ReconcileError}");
            Assert.Equal(f >= teleportIngest.Frame ? 2u : 1u, row.LocalTeleportEpoch);
        }

        // The teleport cuts on its first presentation event.
        Vector3 cut = rows[teleportIngest.Frame].Rendered!.Value;
        Assert.True(Vector2.Distance(new Vector2(cut.X, cut.Z), new Vector2(Destination.X, Destination.Z)) <= tol);

        // Motion is visible at the first presentation event after each moving submission, before the server tick
        // that consumes it on a nonzero phase.
        foreach (DeltaSubmitRow s in submits.Where(s => s.Command.Move != Vector2.Zero))
        {
            float speed = s.Command.Run ? tuning.RunSpeed : tuning.WalkSpeed;
            Vector3 before = rows[s.Frame - 1].Predicted!.Value.Position, after = rows[s.Frame].Predicted!.Value.Position;
            Assert.True(Vector3.Distance(before, after) >= 0.9f * speed * DeltaFaultSchedule.TickSeconds,
                $"frame {s.Frame}: no predicted motion");
            Assert.True(Vector3.Distance(rows[s.Frame - 1].Rendered!.Value, rows[s.Frame].Rendered!.Value) > 0f);
            // On a nonzero phase that motion is prediction alone: no state acknowledging the command has arrived yet.
            if (phase != 0)
                Assert.True(rows[s.Frame].MovementAck < s.Seq, $"frame {s.Frame}: command {s.Seq} already acknowledged");
        }

        // The stale datagram carries an older movement ack and changes nothing at its delivery.
        FaultForward stale = Assert.Single(mover.Downstream.Forwards, f => f.Faulted);
        DeltaFrameRow staleRow = rows.First(r => r.Subtick >= stale.Subtick);
        Assert.Equal(1, staleRow.DeliveredThisPoll);
        Assert.True(BitConverter.ToInt32(stale.Payload, 10) < staleRow.MovementAck);
        DeltaReliabilityAcceptanceTests.AssertNothingIngested(staleRow, rows[staleRow.Frame - 1]);
        Assert.True(staleRow.PendingBeforePoll > 0 || phase == 0);
        rig.Trace.AssertMonotonicAccepted(mover.Index);

        // The observer watches the walk without faults: every presentation event matches the remote oracle, and the
        // schedule pins no hold frame.
        (List<int> oracleHolds, List<int> actualHolds) = DeltaRemotePresentationTests.AssertAgainstOracle(rig,
            RigRole.Observer, rig.NetIdOf(RigRole.Mover), tol);
        Assert.Empty(oracleHolds);
        Assert.Empty(actualHolds);
    }
}
