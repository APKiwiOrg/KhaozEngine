using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class GroundMovementCommitmentControlTests
{
    const float Dt = MovementHoldRig.Dt;

    // #1658. Admission must refuse before supersession so the existing move keeps its control.
    [Theory]
    [InlineData(MovementHoldHead.Flat, 0)]
    [InlineData(MovementHoldHead.Sharded, 0)]
    [InlineData(MovementHoldHead.Flat, 1)]
    [InlineData(MovementHoldHead.Sharded, 1)]
    [InlineData(MovementHoldHead.Flat, 2)]
    [InlineData(MovementHoldHead.Sharded, 2)]
    public void Unsupported_begin_ends_its_own_sequence_without_superseding(MovementHoldHead head, int unsupported)
    {
        using var rig = new MovementHoldRig(head);
        using var seat = rig.JoinClient("ground");
        PlayerRef target = PlayerRef.Slot(seat.Slot);
        uint active = rig.Admin.BeginMovementCommitment(target,
            new MovementCommitmentRequest(Vector2.UnitX, 4, 5, 1, 5));
        rig.Step();
        var ended = new List<MovementCommitmentResult>();
        Ended(rig, ended.Add);
        uint refused = 0;
        Before(rig, _ =>
        {
            if (refused != 0) return;
            var state = rig.Authority(seat.Slot);
            state.Move.Grounded = unsupported != 0;
            state.Move.Swimming = unsupported == 1;
            state.Move.WaterExcursion = unsupported == 2 ? WaterExcursionState.AirborneFromWater : default;
            if (unsupported == 0) state.Position = new Vector3(0, 10, 0);
            rig.Server.SetPlayerState(seat.Slot, state);
            refused = rig.Admin.BeginMovementCommitment(target,
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitY, 30, 10));
        });

        rig.Step();

        Assert.Equal(active, rig.Authority(seat.Slot).Move.Commitment.Sequence);
        var result = Assert.Single(ended);
        Assert.Equal(refused, result.Sequence);
        Assert.Equal(MovementCommitmentEndReason.UnsupportedState, result.Reason);
        rig.Step();
        Assert.Single(ended);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat, false, false)]
    [InlineData(MovementHoldHead.Sharded, false, false)]
    [InlineData(MovementHoldHead.Flat, true, false)]
    [InlineData(MovementHoldHead.Sharded, true, false)]
    [InlineData(MovementHoldHead.Flat, false, true)]
    [InlineData(MovementHoldHead.Sharded, false, true)]
    public void Same_hook_begin_and_steer_control_preparation_and_the_next_eligible_step(
        MovementHoldHead head, bool preparation, bool hold)
    {
        using var rig = new MovementHoldRig(head);
        using var seat = rig.JoinClient("ground");
        Vector3 start = rig.Authority(seat.Slot).Position;
        uint sequence = 0;
        Before(rig, _ =>
        {
            if (sequence != 0) return;
            sequence = rig.Admin.BeginMovementCommitment(PlayerRef.Slot(seat.Slot),
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 10,
                    preparationSeconds: preparation ? 2 * Dt : 0));
            rig.Admin.SteerMovementCommitment(PlayerRef.Slot(seat.Slot), sequence, new Vector2(0, 2), hold ? 0 : 2);
            Assert.False(rig.Authority(seat.Slot).Move.Commitment.IsActive);
        });

        rig.Step();
        var state = rig.Authority(seat.Slot);
        Assert.Equal(sequence, state.Move.Commitment.Sequence);
        Assert.Equal(Vector2.UnitY, state.Move.Commitment.Direction);
        if (preparation)
        {
            Assert.Equal(MovementCommitmentPhase.Preparing, state.Move.Commitment.Phase);
            Assert.Equal(start, state.Position);
            rig.Step();
            state = rig.Authority(seat.Slot);
        }
        Assert.Equal(MovementCommitmentPhase.Travelling, state.Move.Commitment.Phase);
        Assert.InRange(MathF.Abs(state.Position.X - start.X), 0, 1e-5f);
        if (hold)
        {
            Assert.Equal(start, state.Position);
            Assert.Equal(0, state.Move.Commitment.DistanceRemaining);
            rig.Step();
            Assert.Equal(start, rig.Authority(seat.Slot).Position);
            Assert.True(rig.Authority(seat.Slot).Move.Commitment.IsActive);
        }
        else
        {
            Assert.InRange(state.Position.Z - start.Z, 0.99f, 1.01f);
            Assert.InRange(state.Move.Commitment.DistanceRemaining, 0.99f, 1.01f);
        }
    }

    // #1658. An admin end can become terminal before the step observer captures its before state.
    [Theory]
    [InlineData(MovementHoldHead.Flat, 0f)]
    [InlineData(MovementHoldHead.Sharded, 0f)]
    [InlineData(MovementHoldHead.Flat, 0.15f)]
    [InlineData(MovementHoldHead.Sharded, 0.15f)]
    public void Arrival_precedes_exactly_one_end_and_repeated_controls_cannot_restart_recovery(
        MovementHoldHead head, float recovery)
    {
        using var rig = new MovementHoldRig(head);
        using var seat = rig.JoinClient("ground");
        var target = PlayerRef.Slot(seat.Slot);
        uint sequence = rig.Admin.BeginMovementCommitment(target,
            MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 10, recoverySeconds: recovery));
        rig.Step();
        var arrived = new List<MovementCommitmentResult>();
        var ended = new List<MovementCommitmentResult>();
        var order = new List<string>();
        Arrived(rig, result => { arrived.Add(result); order.Add("arrival"); });
        Ended(rig, result => { ended.Add(result); order.Add("end"); });
        Vector3 stopped = rig.Authority(seat.Slot).Position;
        Before(rig, _ =>
        {
            rig.Admin.EndMovementCommitment(target, sequence, MovementCommitmentStopReason.Arrived);
            rig.Admin.EndMovementCommitment(target, sequence, MovementCommitmentStopReason.Arrived);
            rig.Admin.SteerMovementCommitment(target, sequence, Vector2.UnitY, 10);
            rig.Admin.EndMovementCommitment(target, sequence, MovementCommitmentStopReason.TargetLost);
        });

        rig.Step();
        Assert.Equal(sequence, Assert.Single(arrived).Sequence);
        Assert.Equal(stopped, arrived[0].Position);
        Assert.Equal(Vector2.UnitX, arrived[0].Direction);
        Assert.Equal(MovementCommitmentEndReason.Arrived, arrived[0].Reason);
        if (recovery > Dt)
        {
            Assert.Empty(ended);
            Assert.Equal(MovementCommitmentPhase.Recovering, rig.Authority(seat.Slot).Move.Commitment.Phase);
        }
        for (int i = 0; i < 8; i++) rig.Step();
        Assert.Single(arrived);
        Assert.Equal(sequence, Assert.Single(ended).Sequence);
        Assert.Equal(MovementCommitmentEndReason.Arrived, ended[0].Reason);
        Assert.Equal(new[] { "arrival", "end" }, order);
        Assert.Equal(stopped, rig.Authority(seat.Slot).Position);
    }

    public static TheoryData<MovementHoldHead, MovementCommitmentStopReason, MovementCommitmentEndReason, bool> Stops => new()
    {
        { MovementHoldHead.Flat, MovementCommitmentStopReason.TargetLost, MovementCommitmentEndReason.TargetLost, false },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.TargetLost, MovementCommitmentEndReason.TargetLost, false },
        { MovementHoldHead.Flat, MovementCommitmentStopReason.NoPath, MovementCommitmentEndReason.NoPath, false },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.NoPath, MovementCommitmentEndReason.NoPath, false },
        { MovementHoldHead.Flat, MovementCommitmentStopReason.TooLong, MovementCommitmentEndReason.TooLong, false },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.TooLong, MovementCommitmentEndReason.TooLong, false },
        { MovementHoldHead.Flat, MovementCommitmentStopReason.UnsupportedState, MovementCommitmentEndReason.UnsupportedState, false },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.UnsupportedState, MovementCommitmentEndReason.UnsupportedState, false },
        { MovementHoldHead.Flat, MovementCommitmentStopReason.Cancelled, MovementCommitmentEndReason.Aborted, false },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.Cancelled, MovementCommitmentEndReason.Aborted, false },
        { MovementHoldHead.Flat, MovementCommitmentStopReason.Arrived, MovementCommitmentEndReason.Arrived, true },
        { MovementHoldHead.Sharded, MovementCommitmentStopReason.Arrived, MovementCommitmentEndReason.Arrived, true },
    };

    [Theory]
    [MemberData(nameof(Stops))]
    public void Same_hook_begin_and_end_publish_the_mapped_terminal_result_once(
        MovementHoldHead head, MovementCommitmentStopReason reason, MovementCommitmentEndReason expected, bool arrival)
    {
        using var rig = new MovementHoldRig(head);
        using var seat = rig.JoinClient("ground");
        var ended = new List<MovementCommitmentResult>();
        var arrived = new List<MovementCommitmentResult>();
        Ended(rig, ended.Add);
        Arrived(rig, arrived.Add);
        var start = rig.Authority(seat.Slot).Position;
        uint sequence = 0;
        Before(rig, _ =>
        {
            if (sequence == 0) sequence = rig.Admin.BeginMovementCommitment(PlayerRef.Slot(seat.Slot),
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 10));
            rig.Admin.EndMovementCommitment(PlayerRef.Slot(seat.Slot), sequence, reason);
        });
        for (int i = 0; i < 3; i++) rig.Step();
        Assert.Equal(sequence, Assert.Single(ended).Sequence);
        Assert.Equal(expected, ended[0].Reason);
        Assert.Equal(arrival ? 1 : 0, arrived.Count);
        Assert.Equal(start, rig.Authority(seat.Slot).Position);
    }

    [Fact]
    public void Sharded_short_frame_applies_controls_without_spending_timers_or_duplicating_outcomes()
    {
        using var rig = new MovementHoldRig(MovementHoldHead.Sharded);
        using var seat = rig.JoinClient("ground");
        var target = PlayerRef.Slot(seat.Slot);
        uint sequence = 0;
        bool arrive = false;
        Before(rig, _ =>
        {
            if (sequence == 0) sequence = rig.Admin.BeginMovementCommitment(target,
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 10,
                    preparationSeconds: 2 * Dt, recoverySeconds: 2 * Dt, timeoutSeconds: 3));
            rig.Admin.SteerMovementCommitment(target, sequence, Vector2.UnitY, 2);
            if (arrive) rig.Admin.EndMovementCommitment(target, sequence, MovementCommitmentStopReason.Arrived);
        });
        var arrivals = new List<MovementCommitmentResult>();
        var ends = new List<MovementCommitmentResult>();
        Arrived(rig, arrivals.Add);
        Ended(rig, ends.Add);
        Vector3 start = rig.Authority(seat.Slot).Position;
        rig.Server.Tick(Dt / 4);
        var queued = rig.Authority(seat.Slot);
        Assert.Equal(start, queued.Position);
        Assert.Equal(2 * Dt, queued.Move.Commitment.PreparationRemaining);
        Assert.Equal(3, queued.Move.Commitment.TimeoutRemaining);
        Assert.Equal(2, queued.Move.Commitment.DistanceRemaining);
        Assert.Equal(Vector2.UnitY, queued.Move.Commitment.Direction);
        Assert.Empty(arrivals);
        Assert.Empty(ends);
        rig.Server.Tick(3 * Dt / 4);
        Assert.True(rig.Authority(seat.Slot).Move.Commitment.PreparationRemaining < 2 * Dt);
        arrive = true;
        rig.Server.Tick(Dt / 4);
        Assert.Single(arrivals);
        Assert.Empty(ends);
        Assert.Equal(2 * Dt, rig.Authority(seat.Slot).Move.Commitment.RecoveryRemaining);
        rig.Server.Tick(3 * Dt / 4);
        Assert.Empty(ends);
        rig.Step();
        Assert.Single(ends);
        rig.Step();
        Assert.Single(arrivals);
        Assert.Single(ends);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Supersession_and_handoff_reject_stale_controls_and_disconnect_ends_the_replacement_once(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head, cellSize: 4);
        using var seat = rig.JoinClient("ground");
        var target = PlayerRef.Account("ground");
        var request = MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 20);
        uint stale = rig.Admin.BeginMovementCommitment(target, request);
        var ended = new List<MovementCommitmentResult>();
        var arrived = new List<MovementCommitmentResult>();
        Ended(rig, ended.Add);
        Arrived(rig, arrived.Add);
        rig.Step();
        Assert.True(rig.Server.TryGetPlayerNetId(seat.Slot, out long netId));
        var originalCell = rig.Sharded?.Host.CellFor(0, 0).Coord;
        for (int i = 0; i < 5; i++) rig.Step();
        if (rig.Sharded is { } sharded)
        {
            Assert.True(sharded.Host.TryGetOwner(netId, out var owner, out _));
            Assert.NotEqual(originalCell, owner.Coord);
        }
        uint replacement = 0;
        Before(rig, _ =>
        {
            if (replacement == 0) replacement = rig.Admin.BeginMovementCommitment(target, request);
            rig.Admin.SteerMovementCommitment(target, stale, Vector2.UnitY, 0);
            rig.Admin.EndMovementCommitment(target, stale, MovementCommitmentStopReason.Arrived);
        });
        var before = rig.Authority(seat.Slot).Position;
        rig.Step();
        var state = rig.Authority(seat.Slot);
        Assert.Equal(replacement, state.Move.Commitment.Sequence);
        Assert.Equal(Vector2.UnitX, state.Move.Commitment.Direction);
        Assert.True(state.Position.X > before.X);
        Assert.Equal(before.Z, state.Position.Z);
        Assert.Equal(stale, Assert.Single(ended).Sequence);
        Assert.Equal(MovementCommitmentEndReason.Superseded, ended[0].Reason);
        Assert.Empty(arrived);
        rig.Server.Disconnect(seat.Slot);
        rig.Step();
        rig.Step();
        Assert.Equal(2, ended.Count);
        Assert.Equal(replacement, ended[1].Sequence);
        Assert.Equal(MovementCommitmentEndReason.Disconnected, ended[1].Reason);
        Assert.Empty(arrived);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat, false)]
    [InlineData(MovementHoldHead.Sharded, false)]
    [InlineData(MovementHoldHead.Flat, true)]
    [InlineData(MovementHoldHead.Sharded, true)]
    public void Inactive_or_ballistic_controls_and_absent_targets_have_no_outcomes(MovementHoldHead head, bool ballistic)
    {
        using var rig = new MovementHoldRig(head);
        using var seat = rig.JoinClient("ground");
        var target = PlayerRef.Slot(seat.Slot);
        uint sequence = ballistic ? rig.Admin.BeginMovementCommitment(target,
            new MovementCommitmentRequest(Vector2.UnitX, 4, 5, preparationSeconds: 1)) : 81;
        var ended = new List<MovementCommitmentResult>();
        var arrived = new List<MovementCommitmentResult>();
        Ended(rig, ended.Add);
        Arrived(rig, arrived.Add);
        Before(rig, _ =>
        {
            rig.Admin.SteerMovementCommitment(target, sequence, Vector2.UnitY, 0);
            rig.Admin.EndMovementCommitment(target, sequence, MovementCommitmentStopReason.Arrived);
            rig.Admin.SteerMovementCommitment(PlayerRef.Slot(99), sequence, Vector2.UnitY, 0);
            rig.Admin.EndMovementCommitment(PlayerRef.Account("absent"), sequence, MovementCommitmentStopReason.NoPath);
        });
        rig.Step();
        var current = rig.Authority(seat.Slot).Move.Commitment;
        Assert.Equal(ballistic ? sequence : 0u, current.Sequence);
        if (ballistic)
        {
            Assert.Equal(MovementCommitmentPhase.Preparing, current.Phase);
            Assert.Equal(Vector2.UnitX, current.Direction);
        }
        Assert.Empty(arrived);
        Assert.Empty(ended);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Invalid_controls_are_refused_before_queueing_even_for_absent_targets(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        var target = PlayerRef.Slot(99);
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.SteerMovementCommitment(target, 0, Vector2.UnitX, 1));
        Assert.Throws<ArgumentException>(() => rig.Admin.SteerMovementCommitment(target, 1, Vector2.Zero, 1));
        Assert.Throws<ArgumentException>(() => rig.Admin.SteerMovementCommitment(target, 1, new Vector2(float.NaN, 1), 1));
        Assert.Throws<ArgumentException>(() => rig.Admin.SteerMovementCommitment(target, 1, new Vector2(1, float.PositiveInfinity), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.SteerMovementCommitment(target, 1, Vector2.UnitX, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.SteerMovementCommitment(target, 1, Vector2.UnitX, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.SteerMovementCommitment(target, 1, Vector2.UnitX, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.EndMovementCommitment(target, 0, MovementCommitmentStopReason.Arrived));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.EndMovementCommitment(target, 1, MovementCommitmentStopReason.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Admin.EndMovementCommitment(target, 1, (MovementCommitmentStopReason)255));
        rig.Step();
    }

    // FM-D1. Clamping a fast authoritative ground move must retain the commitment anomaly exemption.
    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Fast_ground_travel_respects_bounds_without_raising_movement_anomalies(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head,
            antiCheat: new() { MaxCorrectionDistance = 0.001f, CorrectionStreak = 1 },
            bounds: new CircleBounds(Vector2.Zero, 1));
        using var seat = rig.JoinClient("ground");
        var flags = new List<SuspiciousActivity>();
        if (rig.Single is { } single) single.OnSuspiciousActivity += flags.Add;
        else rig.Sharded!.OnSuspiciousActivity += flags.Add;
        uint sequence = 0;
        Before(rig, _ =>
        {
            if (sequence != 0) return;
            sequence = rig.Admin.BeginMovementCommitment(PlayerRef.Slot(seat.Slot),
                MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 30, 20));
        });
        var start = rig.Authority(seat.Slot).Position;
        rig.Step();
        Assert.True(rig.Authority(seat.Slot).Position.X - start.X > rig.Tuning.WalkSpeed * Dt);
        for (int i = 0; i < 3; i++) rig.Step();
        Assert.InRange(rig.Authority(seat.Slot).Position.X, 0.99f, 1.001f);
        Assert.DoesNotContain(flags, flag => flag.Reason == SuspiciousReason.MovementCorrection);
    }

    // #1658. Control-state edits must not depend on placement admission while movement data is held.
    [Theory]
    [InlineData(false, MovementCommitmentStopReason.Arrived, false)]
    [InlineData(true, MovementCommitmentStopReason.Arrived, false)]
    [InlineData(false, MovementCommitmentStopReason.NoPath, false)]
    [InlineData(true, MovementCommitmentStopReason.NoPath, false)]
    [InlineData(false, MovementCommitmentStopReason.Arrived, true)]
    [InlineData(true, MovementCommitmentStopReason.Arrived, true)]
    public void Explicit_environment_holds_do_not_refuse_controls_or_duplicate_terminal_events(
        bool sharded, MovementCommitmentStopReason reason, bool abortRecovery)
    {
        using var single = sharded ? null : new ExplicitPlayerHeadsTests.Pair();
        using var multi = sharded ? new ExplicitShardedHeadsTests.Pair() : null;
        IAdminControllable admin = (IAdminControllable?)single?.Server ?? multi!.Server;
        var environment = single?.ServerEnvironment ?? multi!.Environment;
        void Pump(int ticks)
        {
            if (single is not null) single.Pump(ticks);
            else multi!.Pump(ticks);
        }
        PlayerMoveState Read()
        {
            bool found = single is not null ? single.Server.TryGetPlayerState(0, out var state)
                : multi!.Server.TryGetPlayerState(0, out state);
            Assert.True(found);
            return state;
        }
        Pump(6);
        var target = PlayerRef.Slot(0);
        var supported = Read();
        environment.Availability = MovementAvailability.Unresolved;
        uint sequence = admin.BeginMovementCommitment(target,
            MovementCommitmentRequest.ForGroundTravel(Vector2.UnitX, 2, 2, recoverySeconds: 0.3f));
        Pump(1);
        Assert.Equal(sequence, Read().Move.Commitment.Sequence);
        Assert.Equal(MovementCommitmentPhase.Preparing, Read().Move.Commitment.Phase);
        Assert.Equal(supported.Position, Read().Position);
        environment.Availability = MovementAvailability.Known;
        Pump(1);
        var moving = Read();
        Assert.Equal(MovementCommitmentPhase.Travelling, moving.Move.Commitment.Phase);
        var arrivals = new List<MovementCommitmentResult>();
        var ends = new List<MovementCommitmentResult>();
        bool end = false;
        void Control(float _)
        {
            if (end) admin.EndMovementCommitment(target, sequence, reason);
            else admin.SteerMovementCommitment(target, sequence, Vector2.UnitY, 1);
        }
        if (single is not null)
        {
            single.Server.OnBeforeTick += Control;
            single.Server.MovementCommitmentArrived += arrivals.Add;
            single.Server.MovementCommitmentEnded += ends.Add;
        }
        else
        {
            multi!.Server.OnBeforeTick += Control;
            multi.Server.MovementCommitmentArrived += arrivals.Add;
            multi.Server.MovementCommitmentEnded += ends.Add;
        }
        environment.Availability = MovementAvailability.Unresolved;
        Pump(1);
        var steered = Read();
        Assert.Equal(moving.Position, steered.Position);
        Assert.Equal(Vector2.UnitY, steered.Move.Commitment.Direction);
        Assert.Equal(1, steered.Move.Commitment.DistanceRemaining);
        Assert.Equal(moving.Move.Commitment.TimeoutRemaining, steered.Move.Commitment.TimeoutRemaining);
        end = true;
        Pump(3);
        bool arrived = reason == MovementCommitmentStopReason.Arrived;
        Assert.Equal(arrived ? 1 : 0, arrivals.Count);
        Assert.Equal(arrived ? 0 : 1, ends.Count);
        Assert.Equal(arrived ? MovementCommitmentPhase.Recovering : MovementCommitmentPhase.Aborted,
            Read().Move.Commitment.Phase);
        if (abortRecovery)
        {
            admin.AbortMovementCommitment(target, sequence);
            Pump(1);
            Assert.False(Read().Move.Commitment.IsActive);
            Assert.Single(ends);
        }
        environment.Availability = MovementAvailability.Known;
        Pump(8);
        Assert.Equal(arrived ? 1 : 0, arrivals.Count);
        Assert.Equal(sequence, Assert.Single(ends).Sequence);
        Assert.Equal(abortRecovery ? MovementCommitmentEndReason.Aborted
            : arrived ? MovementCommitmentEndReason.Arrived : MovementCommitmentEndReason.NoPath, ends[0].Reason);
    }

    static void Arrived(MovementHoldRig rig, Action<MovementCommitmentResult> action)
    {
        if (rig.Single is { } single) single.MovementCommitmentArrived += action;
        else rig.Sharded!.MovementCommitmentArrived += action;
    }

    static void Before(MovementHoldRig rig, Action<float> action)
    {
        if (rig.Single is { } single) single.OnBeforeTick += action;
        else rig.Sharded!.OnBeforeTick += action;
    }

    static void Ended(MovementHoldRig rig, Action<MovementCommitmentResult> action)
    {
        if (rig.Single is { } single) single.MovementCommitmentEnded += action;
        else rig.Sharded!.MovementCommitmentEnded += action;
    }
}
