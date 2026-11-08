using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.NetWorld;
using KhaozEngine.Netcode;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.NetWorld.PlayerRangeMovementTestRig;

namespace KhaozEngine.Tests.NetWorld;

public class GroundPursuitLoopbackTests(ITestOutputHelper output)
{
    const float Speed = 4;
    const float StopDistance = 0.1f;
    const float Recovery = 0.3f;
    const int DelayFrames = 8;
    const float Delay = DelayFrames * FrameSeconds;
    // Both paths can travel in different directions while one steering basis is stale. The age includes
    // downstream latency and one tick of input phase. Each swept path can retain a 1 cm clearance gap.
    const float Clearance = 0.01f;
    const float CorrectionBound = 2 * Speed * (Delay + TickSeconds) + 2 * Clearance;
    const float RenderBound = CorrectionBound + Speed * TickSeconds;
    const float PositionPrecision = 0.00001f;
    static PredictionSettings Presentation => PredictionSettings.Default with { TickSeconds = TickSeconds };
    static readonly PlayerRef Actor = PlayerRef.Slot(0);
    static MoveCommand Fighting => new(-Vector2.UnitX, true, MathF.PI, jump: true, faceCamera: true);

    // #1658. A phase-locked or authority-only proof misses stale corner steering in the rendered avatar.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingTargetAroundARealCornerHasBoundedRenderedCorrectionAndAStableArrival(bool sharded)
    {
        using var rig = new PlayerRangeMovementTestRig(wallAndBox: true, sharded, DelayFrames, Presentation);
        var pursuit = new GroundPursuit(rig.Navigation);
        var limits = new GroundPursuitSettings(10, 20, StopDistance);
        uint sequence = 0;
        int ticks = 0, arrivalTick = -1, endedTick = -1;
        int startReconciles = rig.MidPresentationReconciles;
        Vector3 stopped = default;
        ReachTarget target = default;
        bool terminal = false, crossedCorner = false, sawTravelDespiteRoot = false, sawGroundReplay = false;
        var arrivals = new List<MovementCommitmentResult>();
        var ends = new List<MovementCommitmentResult>();
        rig.SetSpeedScale(0); // Root suppresses ordinary travel, never this server-owned speed.
        rig.Arrived(result =>
        {
            arrivals.Add(result);
            arrivalTick = rig.LoopbackTicks;
            stopped = result.Position;
            Assert.True(ReachGeometry.Within(Shape(rig.Authority), target, StopDistance));
            Assert.Equal(Recovery, rig.Authority.Move.Commitment.RecoveryRemaining);
            Assert.Empty(ends);
        });
        rig.Ended(result => { ends.Add(result); endedTick = rig.LoopbackTicks; });
        rig.BeforeTick(dt =>
        {
            if (terminal) return;
            ticks++;
            target = ReachTarget.Capsule(new(new(1.5f, 0.75f, -0.5f + MathF.Min(ticks, 20) * 0.01f), 0.15f, 0.75f));
            var state = rig.Authority;
            if (sequence == 0)
            {
                var ready = pursuit.Admit(state.Move, Tuning, target, limits, rig.Context);
                Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
                sequence = rig.Admin.BeginMovementCommitment(Actor,
                    MovementCommitmentRequest.ForGroundTravel(ready.Direction, Speed, ready.RemainingPathLength,
                        recoverySeconds: Recovery, timeoutSeconds: 6));
            }
            else
            {
                var update = pursuit.Tick(state.Move, Tuning, target, dt, rig.Context);
                Assert.True(update.Status is GroundPursuitStatus.Travelling or GroundPursuitStatus.Arrived,
                    $"Pursuit {update.Status}/{update.Detail} at {state.Position}");
                if (update.Status == GroundPursuitStatus.Travelling)
                    rig.Admin.SteerMovementCommitment(Actor, sequence, update.Direction, update.RemainingPathLength);
                else
                {
                    terminal = true;
                    rig.Admin.EndMovementCommitment(Actor, sequence, MovementCommitmentStopReason.Arrived);
                }
            }
        });

        float maxCorrection = 0, maxRenderError = 0;
        Vector3 previous = rig.Authority.Position;
        for (int frame = 0; frame < 960; frame++)
        {
            int beforeTick = rig.LoopbackTicks;
            rig.LoopbackFrame(!terminal && rig.Client.LocalPredictedState.Move.Commitment.IsActive ? Fighting : MoveCommand.Idle);
            var state = rig.Authority;
            Vector3 rendered = rig.Client.LocalRenderState.Position;
            var predicted = rig.Client.LocalPredictedState.Move;
            sawGroundReplay |= predicted.Commitment.Sequence == sequence && predicted.Commitment.IsActive
                && predicted.Commitment.Kind == MovementCommitmentKind.Ground && rig.Client.PendingPredictionCommands > 0;
            if (state.Move.Commitment.IsActive)
            {
                Assert.Equal(sequence, state.Move.Commitment.Sequence);
                float heading = CharacterMovement.FacingYawOf(state.Move.Commitment.Direction);
                Assert.InRange(MathF.Abs(state.Move.FacingYaw - heading), 0, MovementState.FacingYawQuantum);
            }
            maxCorrection = MathF.Max(maxCorrection, rig.Client.NetStats.LastCorrectionMeters);
            maxRenderError = MathF.Max(maxRenderError, Vector3.Distance(rendered, state.Position));
            if (beforeTick == rig.LoopbackTicks) continue;
            float stride = Vector2.Distance(new(previous.X, previous.Z), new(state.Position.X, state.Position.Z));
            Assert.True(stride <= Speed * TickSeconds + PositionPrecision, $"Stride {stride}");
            sawTravelDespiteRoot |= stride > Clearance;
            Assert.True(state.Grounded);
            Assert.Equal(0, state.Move.VerticalVelocity);
            if (previous.X < 0 && state.Position.X >= 0)
            {
                crossedCorner = true;
                Assert.True(previous.Z >= 0.4f && state.Position.Z >= 0.4f);
            }
            rig.AssertClear(state);
            if (arrivalTick >= 0) Assert.Equal(stopped, state.Position);
            if (frame >= 840)
                Assert.InRange(Vector3.Distance(stopped, rendered), 0, PositionPrecision);
            previous = state.Position;
        }
        output.WriteLine($"sharded={sharded}, correction={maxCorrection:G9}/{CorrectionBound:G9} m, " +
            $"render error={maxRenderError:G9}/{RenderBound:G9} m, poll displacement={rig.MaximumPollDisplacement:G9} m");
        Assert.True(rig.MidPresentationReconciles > startReconciles);
        Assert.True(crossedCorner);
        Assert.True(sawTravelDespiteRoot);
        Assert.True(sawGroundReplay, "The owner must replay unacknowledged input with carried ground controls.");
        Assert.InRange(rig.MaximumPollDisplacement, 0, PositionPrecision);
        Assert.True(maxCorrection > Clearance, "The fixture must exercise a real correction.");
        Assert.InRange(maxCorrection, 0, CorrectionBound);
        Assert.InRange(maxRenderError, 0, RenderBound);
        Assert.Equal(sequence, Assert.Single(arrivals).Sequence);
        Assert.Equal(sequence, Assert.Single(ends).Sequence);
        Assert.Equal(MovementCommitmentEndReason.Arrived, ends[0].Reason);
        Assert.InRange((endedTick - arrivalTick + 1) * TickSeconds, Recovery, Recovery + TickSeconds);
        Assert.Equal(stopped, rig.Client.LocalPredictedState.Position);
        Assert.InRange(Vector3.Distance(stopped, rig.Client.LocalRenderState.Position), 0, PositionPrecision);
        Assert.False(rig.Client.LocalPredictedState.Move.Commitment.IsActive);
        if (rig.Sharded is { } multi)
        {
            Assert.True(rig.Host.TryGetPlayerNetId(0, out long id));
            Assert.True(multi.Host.TryGetOwner(id, out var owner, out _));
            Assert.NotEqual(multi.Host.CellFor(-1.5f, 0).Coord, owner.Coord);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTargetEndsThePursuitWithoutAnArrival(bool sharded)
    {
        using var rig = new PlayerRangeMovementTestRig(sharded: sharded, prediction: Presentation);
        var pursuit = new GroundPursuit(rig.Navigation);
        uint sequence = 0;
        int ticks = 0;
        bool terminal = false;
        var arrived = new List<MovementCommitmentResult>();
        var ended = new List<MovementCommitmentResult>();
        rig.Arrived(arrived.Add);
        rig.Ended(ended.Add);
        rig.BeforeTick(dt =>
        {
            if (terminal) return;
            ticks++;
            if (sequence == 0)
            {
                var ready = pursuit.Admit(rig.Authority.Move, Tuning, ReachTarget.Point(new(1.75f, 0.75f, 0)),
                    new(4, 4, StopDistance), rig.Context);
                Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
                sequence = rig.Admin.BeginMovementCommitment(Actor,
                    MovementCommitmentRequest.ForGroundTravel(ready.Direction, Speed, ready.RemainingPathLength));
                return;
            }
            var update = pursuit.Tick(rig.Authority.Move, Tuning, null, dt, rig.Context);
            Assert.Equal(GroundPursuitStatus.TargetLost, update.Status);
            terminal = true;
            rig.Admin.EndMovementCommitment(Actor, sequence, MovementCommitmentStopReason.TargetLost);
        });
        for (int frame = 0; frame < 160; frame++) rig.LoopbackFrame(MoveCommand.Idle);
        Assert.Empty(arrived);
        Assert.Equal(sequence, Assert.Single(ended).Sequence);
        Assert.Equal(MovementCommitmentEndReason.TargetLost, ended[0].Reason);
        Assert.InRange(Vector3.Distance(rig.Authority.Position, rig.Client.LocalRenderState.Position), 0, PositionPrecision);
        Assert.False(rig.Client.LocalPredictedState.Move.Commitment.IsActive);
    }

    // #1658. Exhausting a replicated distance is a hold, never client-authored arrival.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastFractionalBudgetCannotOvershootOrReleaseControl(bool sharded)
    {
        using var rig = new PlayerRangeMovementTestRig(sharded: sharded, snapshotDelayFrames: DelayFrames, prediction: Presentation);
        rig.SetSpeedScale(0);
        Vector3 start = rig.Authority.Position;
        uint sequence = 0;
        var arrivals = new List<MovementCommitmentResult>();
        var ended = new List<MovementCommitmentResult>();
        rig.Arrived(arrivals.Add);
        rig.Ended(ended.Add);
        rig.BeforeTick(_ =>
        {
            if (sequence == 0) sequence = rig.Admin.BeginMovementCommitment(Actor,
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, Speed, 0.35f, timeoutSeconds: 5));
        });
        bool sawPredictedHold = false;
        for (int frame = 0; frame < 160; frame++)
        {
            rig.LoopbackFrame(rig.Client.LocalPredictedState.Move.Commitment.IsActive ? Fighting : MoveCommand.Idle);
            float authoritativeTravel = rig.Authority.Position.X - start.X;
            float predictedTravel = rig.Client.LocalPredictedState.Position.X - start.X;
            Assert.InRange(authoritativeTravel, 0, 0.35f + PositionPrecision);
            Assert.InRange(predictedTravel, 0, 0.35f + PositionPrecision);
            Assert.InRange(rig.Client.LocalRenderState.Position.X - start.X, -PositionPrecision, 0.35f + PositionPrecision);
            var commitment = rig.Client.LocalPredictedState.Move.Commitment;
            sawPredictedHold |= commitment.Sequence == sequence && commitment.IsActive && commitment.DistanceRemaining == 0;
        }
        Assert.True(sawPredictedHold);
        Assert.Equal(0.35f, rig.Authority.Position.X - start.X, 5);
        Assert.Equal(0, rig.Authority.Move.Commitment.DistanceRemaining);
        Assert.True(rig.Authority.Move.Commitment.IsActive);
        Assert.Empty(arrivals);
        Assert.Empty(ended);
        Assert.Equal(rig.Authority.Position, rig.Client.LocalPredictedState.Position);
    }

    // #1652. The fine percentage must affect real ordinary prediction, while input alone cannot start a commitment.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FineOrdinarySpeedReachesTheRenderedClientWithoutAuthoringGroundTravel(bool sharded)
    {
        using var rig = new PlayerRangeMovementTestRig(sharded: sharded, prediction: Presentation);
        rig.SetSpeedScale(1.1f);
        for (int frame = 0; frame < 16; frame++) rig.LoopbackFrame(MoveCommand.Idle);
        Assert.Equal(1.099609375f, rig.Client.LocalRenderState.Move.SpeedScale);
        Vector3 start = rig.Authority.Position;
        int acknowledged = rig.LastAcknowledgedSequence;
        for (int frame = 0; frame < 32; frame++)
        {
            rig.LoopbackFrame(new(Vector2.UnitX, false, 0));
            Assert.False(rig.Authority.Move.Commitment.IsActive);
            Assert.False(rig.Client.LocalPredictedState.Move.Commitment.IsActive);
        }
        int consumed = rig.LastAcknowledgedSequence - acknowledged;
        Assert.Equal(8, consumed);
        float expected = consumed * TickSeconds * 6.59765625f; // 6 m/s * independently specified fine 10 percent scale.
        Assert.InRange(MathF.Abs(rig.Authority.Position.X - start.X - expected), 0, PositionPrecision);
        for (int frame = 0; frame < 160; frame++) rig.LoopbackFrame(MoveCommand.Idle);
        Assert.InRange(MathF.Abs(rig.Client.LocalRenderState.Position.X - start.X - expected), 0, PositionPrecision);
        Assert.Equal(0u, rig.Authority.Move.Commitment.Sequence);
        Assert.Equal(0u, rig.Client.LocalPredictedState.Move.Commitment.Sequence);
    }
}
