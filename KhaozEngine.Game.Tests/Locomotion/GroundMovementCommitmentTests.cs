using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion;

public class GroundMovementCommitmentTests
{
    const float Dt = 0.1f;
    static readonly MoveCommand Hostile = new(-Vector2.One, true, 2, jump: true, faceCamera: true);
    static readonly MoveTuning Tuning = MoveTuning.Default with { Gravity = 7, AirMomentum = true };
    static float Flat(float x, float z) => 0;
    static float Abyss(float x, float z) => -1000;
    static MoveState Standing(float x = 0, float y = 0) => new()
    {
        Position = new(x, y + Tuning.CapsuleHalfHeight, 0), Grounded = true,
        Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 3, 10),
    };

    [Theory]
    [InlineData(MovementControllerKind.Legacy, 0f)]
    [InlineData(MovementControllerKind.Legacy, 2f)]
    [InlineData(MovementControllerKind.Contact, 0f)]
    [InlineData(MovementControllerKind.Contact, 2f)]
    public void Last_step_spends_only_the_budget_then_holds_against_input(MovementControllerKind controller, float scale)
    {
        var state = Standing() with
        {
            SpeedScale = scale, JumpBufferRemaining = 1,
            Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 3, 0.25f),
        };
        MoveTuning tuning = Tuning with { Controller = controller };
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
        Assert.Equal(0.25f, state.Position.X);
        Assert.Equal(new Vector2(2.5f, 0), state.CommandedVelocity);
        Assert.Equal(0f, state.Position.Z);
        Assert.True(state.Grounded);
        Assert.Equal(0f, state.VerticalVelocity);
        Assert.Equal(0f, state.JumpBufferRemaining);
        Assert.Equal(-MathF.PI / 2, state.FacingYaw);
        Assert.Equal(scale, state.SpeedScale);
        Vector3 stopped = state.Position;
        for (int i = 0; i < 5; i++)
        {
            state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
            Assert.Equal(stopped, state.Position);
            Assert.Equal(0f, state.Commitment.DistanceRemaining);
            Assert.Equal(MovementCommitmentPhase.Travelling, state.Commitment.Phase);
            Assert.True(state.Commitment.IsActive);
            Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
            Assert.Equal(0, state.Commitment.ConsecutiveBlockedTicks);
        }
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void Real_flat_ground_spends_achieved_distance_and_never_overshoots(MovementControllerKind controller)
    {
        using var scene = Floor(SceneVariant.Box);
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing() with
        { Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 3, 0.25f) };
        float resolution = controller == MovementControllerKind.Legacy ? CharacterMovement.SweepTravelResolution
            : MathF.BitIncrement(0.25f) - 0.25f;
        for (int i = 0; i < 8; i++)
        {
            float before = state.Position.X;
            float budget = state.Commitment.DistanceRemaining;
            state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
            Assert.True(state.Grounded);
            Assert.True(state.Commitment.IsActive);
            Assert.InRange(state.Position.X, before, 0.2500001f);
            Assert.InRange(state.CommandedVelocity.Length() * Dt, 0, budget + 0.0000001f);
            float unspent = MathF.Max(0, budget - (state.Position.X - before));
            Assert.True(state.Commitment.DistanceRemaining == unspent ||
                state.Commitment.DistanceRemaining == 0 && unspent <= resolution);
        }
        Assert.Equal(0f, state.Commitment.DistanceRemaining);
        Assert.InRange(state.Position.X, 0.25f - resolution, 0.2500001f);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void Preparation_and_arrival_recovery_root_and_preserve_the_arrival_reason(MovementControllerKind controller)
    {
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing() with
        {
            Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 3, 1,
                preparationSeconds: 0.25f, recoverySeconds: 0.25f),
        };
        for (int i = 0; i < 2; i++)
        {
            state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
            Assert.Equal(0, state.Position.X);
            Assert.Equal(MovementCommitmentPhase.Preparing, state.Commitment.Phase);
        }
        state.Commitment = GroundTravelCommitment.Steer(state.Commitment, 1, Vector2.UnitY, 0.4f);
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
        Assert.Equal(MovementCommitmentPhase.Travelling, state.Commitment.Phase);
        Assert.InRange(state.Position.Z, 0.29999f, 0.30001f);
        Assert.Equal(-MathF.PI, state.FacingYaw, 5);
        state.Commitment = GroundTravelCommitment.End(state.Commitment, 1, MovementCommitmentEndReason.Arrived);
        Vector3 arrived = state.Position;
        for (int i = 0; i < 3; i++)
        {
            MovementCommitment recovery = state.Commitment;
            Assert.Equal(recovery, GroundTravelCommitment.Steer(recovery, 1, -Vector2.UnitX, 9));
            Assert.Equal(recovery, GroundTravelCommitment.End(recovery, 1, MovementCommitmentEndReason.Arrived));
            Assert.Equal(recovery, GroundTravelCommitment.End(recovery, 1, MovementCommitmentEndReason.TargetLost));
            state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
            Assert.Equal(arrived, state.Position);
            Assert.Equal(MovementCommitmentEndReason.Arrived, state.Commitment.EndReason);
        }
        Assert.Equal(MovementCommitmentPhase.Completed, state.Commitment.Phase);
        state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Flat, tuning);
        Assert.Equal(default, state.Commitment);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void Wall_grazes_count_consecutively_and_adequate_progress_resets_them(MovementControllerKind controller)
    {
        using var scene = Floor(SceneVariant.Box).Wall("wall", 0.5f, 0.2f);
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing(0.09f) with
        { Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 6, 10) };
        state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
        Assert.Equal(1, state.Commitment.ConsecutiveBlockedTicks);
        Assert.True(state.Commitment.IsActive);
        Assert.True(state.Position.X < 0.5f - tuning.CapsuleRadius - 0.0005f);
        state.Commitment = GroundTravelCommitment.Steer(state.Commitment, 1, new(1, 0.05f), 10);
        Assert.Equal(1, state.Commitment.ConsecutiveBlockedTicks);
        state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
        Assert.Equal(2, state.Commitment.ConsecutiveBlockedTicks);
        state.Commitment = GroundTravelCommitment.Steer(state.Commitment, 1, new(1, 0.2f), 10);
        state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
        Assert.Equal(0, state.Commitment.ConsecutiveBlockedTicks);
        Assert.True(state.Commitment.IsActive);
        state.Commitment = GroundTravelCommitment.Steer(state.Commitment, 1, Vector2.UnitX, 10);
        for (int i = 1; i <= 3; i++)
        {
            MoveState basis = state;
            state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
            MoveState replay = CharacterMovement.Step(basis, Hostile, Dt, Abyss, tuning, world: scene.World);
            Assert.Equal(state, replay);
            Assert.Equal(i, state.Commitment.ConsecutiveBlockedTicks);
            Assert.Equal(i < 3, state.Commitment.IsActive);
        }
        Assert.Equal(MovementCommitmentEndReason.Blocked, state.Commitment.EndReason);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy, 1f)]
    [InlineData(MovementControllerKind.Contact, 1f)]
    [InlineData(MovementControllerKind.Contact, 1000f)]
    public void A_sub_resolution_residual_holds_instead_of_becoming_blocked(MovementControllerKind controller, float x)
    {
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing(x) with
        { Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 1.3f, 0.1f) };
        for (int i = 0; i < 8; i++)
            state = CharacterMovement.Step(state, Hostile, 0.03f, Flat, tuning);
        float ulp = MathF.BitIncrement(x + 0.1f) - (x + 0.1f);
        Assert.InRange(MathF.Abs(state.Position.X - (x + 0.1f)), 0, ulp / 2);
        Assert.Equal(0f, state.Commitment.DistanceRemaining);
        Assert.Equal(MovementCommitmentPhase.Travelling, state.Commitment.Phase);
        Assert.Equal(0, state.Commitment.ConsecutiveBlockedTicks);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
        // A resolvable budget beside that residual must still move.
        float before = state.Position.X;
        state.Commitment = GroundTravelCommitment.Steer(state.Commitment, 1, Vector2.UnitX, 2 * ulp);
        state = CharacterMovement.Step(state, Hostile, 0.03f, Flat, tuning);
        Assert.True(state.Position.X > before);
        Assert.Equal(0f, state.Commitment.DistanceRemaining);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy, 1)]
    [InlineData(MovementControllerKind.Legacy, -1)]
    [InlineData(MovementControllerKind.Contact, 1)]
    [InlineData(MovementControllerKind.Contact, -1)]
    public void A_tiny_budget_uses_only_the_precision_of_its_travel_axes(MovementControllerKind controller, int sign)
    {
        float spacing = sign > 0 ? MathF.BitIncrement(1) - 1 : 1 - MathF.BitDecrement(1);
        var state = Standing(1) with
        {
            Position = new(1, Tuning.CapsuleHalfHeight, 1000),
            Commitment = MovementCommitment.ForGroundTravel(1, sign * Vector2.UnitX, 1, 0.75f * spacing),
        };
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, Tuning with { Controller = controller });
        Assert.True(sign * (state.Position.X - 1) > 0);
        Assert.Equal(1000, state.Position.Z);
        Assert.Equal(0f, state.Commitment.DistanceRemaining);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy, false)]
    [InlineData(MovementControllerKind.Legacy, true)]
    [InlineData(MovementControllerKind.Contact, false)]
    [InlineData(MovementControllerKind.Contact, true)]
    public void Half_spacing_budget_obeys_the_controllers_round_to_even(MovementControllerKind controller, bool odd)
    {
        float x = odd ? MathF.BitIncrement(1) : 1;
        float next = MathF.BitIncrement(x);
        var state = Standing(x) with
        { Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 1, (next - x) / 2) };
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, Tuning with { Controller = controller });
        Assert.Equal(odd ? next : x, state.Position.X);
        Assert.Equal(0f, state.Commitment.DistanceRemaining);
        Assert.True(state.Commitment.IsActive);
        Assert.Equal(0, state.Commitment.ConsecutiveBlockedTicks);
    }

    public static IEnumerable<object[]> GroundBoundaries()
    {
        foreach (MovementControllerKind controller in new[] { MovementControllerKind.Legacy, MovementControllerKind.Contact })
            foreach (string boundary in new[] { "step-up", "step-down", "slope" })
                for (int phase = 0; phase < 10; phase++)
                    yield return [controller, boundary, phase * 0.01f];
    }

    [Theory]
    [MemberData(nameof(GroundBoundaries))]
    public void Real_steps_and_slopes_keep_support_across_start_offsets(MovementControllerKind controller,
        string boundary, float offset)
    {
        using var scene = boundary == "slope" ? Ramp(SceneVariant.Box, 0.5f) : Doorstep(SceneVariant.Box, 0.18f);
        bool up = boundary == "step-up";
        float x = up ? -0.65f - offset : 0.65f + offset;
        float y = up ? 0 : boundary == "slope" ? 0.25f * x : 0.18f;
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing(x, y) with
        { Commitment = MovementCommitment.ForGroundTravel(1, up ? Vector2.UnitX : -Vector2.UnitX, 3, 2) };
        for (int tick = 0; tick < 18; tick++)
        {
            state = CharacterMovement.Step(state, Hostile, 1f / 30, Abyss, tuning, world: scene.World);
            Assert.True(state.Grounded, $"{controller} {boundary} offset {offset:R} tick {tick} {state.Position}");
            Assert.Equal(MovementCommitmentPhase.Travelling, state.Commitment.Phase);
        }
        Assert.True(up ? state.Position.X > 0.8f : state.Position.X < -0.8f);
        Assert.InRange(MathF.Abs(state.Position.Y - tuning.CapsuleHalfHeight - (up ? 0.18f : 0)), 0, 0.001f);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void Leaving_real_support_aborts_and_keeps_ordinary_gravity(MovementControllerKind controller)
    {
        using var scene = new FootSupportScene(SceneVariant.Box).Flat("ledge", -4, 0, -5, 5, 0);
        MoveTuning tuning = Tuning with { Controller = controller };
        var state = Standing(-0.05f) with
        { Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 10, 5) };
        state = CharacterMovement.Step(state, Hostile, Dt, Abyss, tuning, world: scene.World);
        Assert.False(state.Grounded);
        Assert.Equal(MovementCommitmentEndReason.UnsupportedState, state.Commitment.EndReason);
        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
        Assert.InRange(state.VerticalVelocity, -7 * Dt - 0.00001f, -float.Epsilon);
        float height = state.Position.Y;
        float velocity = state.VerticalVelocity;
        state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Abyss, tuning);
        Assert.Equal(velocity - 7 * Dt, state.VerticalVelocity, 5);
        Assert.True(state.Position.Y < height);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy, MovementCommitmentPhase.Preparing)]
    [InlineData(MovementControllerKind.Legacy, MovementCommitmentPhase.Travelling)]
    [InlineData(MovementControllerKind.Legacy, MovementCommitmentPhase.Recovering)]
    [InlineData(MovementControllerKind.Contact, MovementCommitmentPhase.Preparing)]
    [InlineData(MovementControllerKind.Contact, MovementCommitmentPhase.Travelling)]
    [InlineData(MovementControllerKind.Contact, MovementCommitmentPhase.Recovering)]
    public void Carried_unsupported_ground_state_aborts_without_replacing_vertical_motion(MovementControllerKind controller,
        MovementCommitmentPhase phase)
    {
        var state = Standing() with { Grounded = false, VerticalVelocity = -2 };
        state.Commitment = state.Commitment with
        {
            Phase = phase, PreparationRemaining = 1, RecoveryRemaining = 1,
            EndReason = phase == MovementCommitmentPhase.Recovering ? MovementCommitmentEndReason.Arrived : default,
        };
        state = CharacterMovement.Step(state, Hostile, Dt, Abyss, Tuning with { Controller = controller });
        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(phase == MovementCommitmentPhase.Recovering ? MovementCommitmentEndReason.Arrived
            : MovementCommitmentEndReason.UnsupportedState, state.Commitment.EndReason);
        Assert.Equal(-2 - 7 * Dt, state.VerticalVelocity, 5);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy, MovementCommitmentPhase.Preparing)]
    [InlineData(MovementControllerKind.Contact, MovementCommitmentPhase.Travelling)]
    public void Timeout_releases_control_even_while_holding(MovementControllerKind controller, MovementCommitmentPhase phase)
    {
        var state = Standing();
        state.Commitment = state.Commitment with
        { Phase = phase, DistanceRemaining = 0, PreparationRemaining = 1, RecoveryRemaining = 1, TimeoutRemaining = Dt };
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, Tuning with { Controller = controller });
        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.TimedOut, state.Commitment.EndReason);
        Assert.Equal(Vector2.Zero, state.HorizontalVelocity);
    }

    // #1658. Arrival completes travel, so its timeout cannot truncate ground recovery. Ballistic is unchanged.
    [Theory]
    [InlineData(MovementControllerKind.Legacy, true)]
    [InlineData(MovementControllerKind.Contact, true)]
    [InlineData(MovementControllerKind.Legacy, false)]
    [InlineData(MovementControllerKind.Contact, false)]
    public void Arrival_recovery_ignores_travel_timeout_but_ballistic_recovery_keeps_it(
        MovementControllerKind controller, bool ground)
    {
        var tuning = Tuning with { Controller = controller };
        var state = Standing();
        state.Commitment = ground
            ? MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 3, 10, recoverySeconds: 3 * Dt, timeoutSeconds: 2 * Dt)
            : new MovementCommitment(1, Vector2.UnitX, 3, 5, 7, 0, 3 * Dt, 2 * Dt)
                with { Phase = MovementCommitmentPhase.Recovering };
        state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Flat, tuning);
        if (ground)
            state.Commitment = GroundTravelCommitment.End(state.Commitment, 1, MovementCommitmentEndReason.Arrived);
        var arrived = state.Position;
        state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
        if (!ground)
        {
            Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
            Assert.Equal(MovementCommitmentEndReason.TimedOut, state.Commitment.EndReason);
            return;
        }
        Assert.Equal(MovementCommitmentPhase.Recovering, state.Commitment.Phase);
        Assert.True(state.Commitment.RecoveryRemaining > 0);
        Assert.Equal(MovementCommitmentEndReason.Arrived, state.Commitment.EndReason);
        for (int i = 0; i < 4 && state.Commitment.IsActive; i++)
        {
            state = CharacterMovement.Step(state, Hostile, Dt, Flat, tuning);
            Assert.Equal(arrived, state.Position);
        }
        Assert.Equal(MovementCommitmentPhase.Completed, state.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.Arrived, state.Commitment.EndReason);
    }

    [Theory]
    [InlineData(0.099f, 3, MovementCommitmentPhase.Aborted)]
    [InlineData(0.1f, 0, MovementCommitmentPhase.Travelling)]
    public void Exactly_ten_percent_progress_is_adequate(float achieved, byte blocked, MovementCommitmentPhase phase)
    {
        MovementCommitment movement = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 1, 10) with
        { Phase = MovementCommitmentPhase.Travelling, ConsecutiveBlockedTicks = 2 };
        MovementCommitment result = CharacterMovement.FinishCommitmentTick(movement, false, true,
            Vector3.Zero, new(achieved, 0, 0), 1);
        Assert.Equal(blocked, result.ConsecutiveBlockedTicks);
        Assert.Equal(phase, result.Phase);
    }

    [Theory]
    [InlineData(MovementCommitmentPhase.Preparing)]
    [InlineData(MovementCommitmentPhase.Travelling)]
    public void Steer_reaims_and_accepts_zero_budget_without_resetting_stalls(MovementCommitmentPhase phase)
    {
        MovementCommitment movement = Standing().Commitment with { Phase = phase, ConsecutiveBlockedTicks = 2 };
        Assert.Equal(movement, GroundTravelCommitment.Steer(movement, 2, Vector2.UnitY, 0));
        MovementCommitment steered = GroundTravelCommitment.Steer(movement, 1, new(float.MaxValue, float.MaxValue), 0);
        Assert.InRange(steered.Direction.X, 0.70710f, 0.70711f);
        Assert.InRange(steered.Direction.Y, 0.70710f, 0.70711f);
        Assert.Equal(0, steered.DistanceRemaining);
        Assert.Equal(2, steered.ConsecutiveBlockedTicks);
        Assert.Equal(movement.TimeoutRemaining, steered.TimeoutRemaining);
        MoveState held = CharacterMovement.Step(Standing() with { Commitment = steered }, Hostile, Dt, Flat, Tuning);
        Assert.Equal(2, held.Commitment.ConsecutiveBlockedTicks);
        Assert.Equal(Vector2.Zero, held.HorizontalVelocity);
    }

    [Theory]
    [InlineData(MovementCommitmentEndReason.Arrived, 0f, MovementCommitmentPhase.Completed)]
    [InlineData(MovementCommitmentEndReason.Arrived, 1f, MovementCommitmentPhase.Recovering)]
    [InlineData(MovementCommitmentEndReason.Blocked, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.TimedOut, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.EnteredWater, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.Aborted, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.Teleported, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.Superseded, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.Disconnected, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.TargetLost, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.NoPath, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.TooLong, 1f, MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentEndReason.UnsupportedState, 1f, MovementCommitmentPhase.Aborted)]
    public void End_matches_sequence_and_retains_its_terminal_reason(MovementCommitmentEndReason reason,
        float recovery, MovementCommitmentPhase expected)
    {
        foreach (MovementCommitmentPhase phase in new[] { MovementCommitmentPhase.Preparing, MovementCommitmentPhase.Travelling })
        {
            MovementCommitment movement = Standing().Commitment with { Phase = phase, RecoveryRemaining = recovery };
            MovementCommitment recovering = movement with { Phase = MovementCommitmentPhase.Recovering };
            Assert.Equal(recovering, GroundTravelCommitment.End(recovering, 1, reason));
            Assert.Equal(movement, GroundTravelCommitment.End(movement, 2, reason));
            MovementCommitment ended = GroundTravelCommitment.End(movement, 1, reason);
            Assert.Equal(expected, ended.Phase);
            Assert.Equal(reason, ended.EndReason);
            Assert.Equal(0, ended.DistanceRemaining);
        }
    }

    [Theory]
    [InlineData(MovementCommitmentPhase.None)]
    [InlineData(MovementCommitmentPhase.Completed)]
    [InlineData(MovementCommitmentPhase.Aborted)]
    [InlineData(MovementCommitmentPhase.Recovering)]
    public void Inapplicable_states_ignore_steer_and_end(MovementCommitmentPhase phase)
    {
        MovementCommitment movement = Standing().Commitment with { Phase = phase };
        Assert.Equal(movement, GroundTravelCommitment.Steer(movement, 1, Vector2.UnitY, 3));
        Assert.Equal(movement, GroundTravelCommitment.End(movement, 1, MovementCommitmentEndReason.Arrived));
        MovementCommitment ballistic = new(1, Vector2.UnitX, 3, 5, 0, 5);
        Assert.Equal(ballistic, GroundTravelCommitment.Steer(ballistic, 1, Vector2.UnitY, 3));
        Assert.Equal(ballistic, GroundTravelCommitment.End(ballistic, 1, MovementCommitmentEndReason.Arrived));
    }

    [Fact]
    public void Invalid_control_values_are_refused_even_for_stale_state()
    {
        MovementCommitment movement = Standing().Commitment;
        Assert.Throws<ArgumentException>(() => GroundTravelCommitment.Steer(movement, 2, Vector2.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.Steer(movement, 2, Vector2.UnitX, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.Steer(movement, 2, Vector2.UnitX, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.Steer(movement, 0, Vector2.UnitX, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.End(movement, 0, MovementCommitmentEndReason.Arrived));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.End(movement, 2, MovementCommitmentEndReason.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.End(movement, 2, MovementCommitmentEndReason.Landed));
        Assert.Throws<ArgumentOutOfRangeException>(() => GroundTravelCommitment.End(movement, 2, (MovementCommitmentEndReason)255));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementCommitment.ForGroundTravel(0, Vector2.UnitX, 3, 1));
    }
}
