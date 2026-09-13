using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementCommitmentTests
{
    private const float Dt = 1f / 60f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        CapsuleHalfHeight = 0.9f,
        CapsuleRadius = 0.35f,
    };

    [Fact]
    public void Hostile_input_cannot_steer_jump_or_turn_an_active_commitment()
    {
        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = true,
            FacingYaw = 0.25f,
            Commitment = new MovementCommitment(17u, Vector2.UnitX, 9f, 7f, 0f, 4f),
        };
        var hostile = new MoveCommand(new Vector2(-1f, -1f), run: true, cameraYaw: -2f,
            jump: true, faceCamera: true);

        for (int i = 0; i < 20; i++)
            state = CharacterMovement.Step(state, hostile, Dt, Flat, Tuning);

        Assert.True(state.Position.X > 2f, state.Position.ToString());
        Assert.InRange(MathF.Abs(state.Position.Z), 0f, 1e-4f);
        Assert.Equal(0.25f, state.FacingYaw);
        Assert.Equal(MovementCommitmentPhase.Airborne, state.Commitment.Phase);
        Assert.Equal(Vector2.UnitX, state.Commitment.Direction);
    }

    [Fact]
    public void Preparation_is_rooted_then_launches_in_the_latched_direction()
    {
        var state = new MoveState
        {
            Position = new Vector3(3f, Tuning.CapsuleHalfHeight, -4f),
            Grounded = true,
            FacingYaw = 0.7f,
            Commitment = new MovementCommitment(3u, -Vector2.UnitY, 6f, 5f, 3f * Dt, 4f),
        };
        MoveCommand hostile = new(Vector2.UnitX, run: true, cameraYaw: -1.5f, jump: true, faceCamera: true);

        MoveState first = CharacterMovement.Step(state, hostile, Dt, Flat, Tuning);
        MoveState second = CharacterMovement.Step(first, hostile, Dt, Flat, Tuning);

        Assert.Equal(state.Position, first.Position);
        Assert.Equal(state.Position, second.Position);
        Assert.Equal(MovementCommitmentPhase.Preparing, second.Commitment.Phase);

        MoveState launched = CharacterMovement.Step(second, hostile, Dt, Flat, Tuning);
        Assert.Equal(MovementCommitmentPhase.Airborne, launched.Commitment.Phase);
        Assert.True(launched.Position.Z < state.Position.Z, launched.Position.ToString());
        Assert.True(launched.VerticalVelocity > 0f);
        Assert.Equal(0.7f, launched.FacingYaw);
    }

    [Fact]
    public void Real_swept_collision_blocks_the_leap_and_it_finishes_on_landing()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        Vector3[] wall =
        {
            new(1.25f, -2f, -4f), new(1.25f, -2f, 4f),
            new(1.25f, 5f, 4f), new(1.25f, 5f, -4f),
        };
        world.AddStatic(new TriangleMeshShape(wall, new[] { 0, 1, 2, 0, 2, 3 }), Pose.At(Vector3.Zero));
        world.Step(Dt);

        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = true,
            Commitment = new MovementCommitment(9u, Vector2.UnitX, 22f, 7f, 0f, 5f),
        };

        int terminalTicks = 0;
        for (int i = 0; i < 300; i++)
        {
            state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Flat, Tuning, world: world);
            Assert.True(state.Position.X < 1.25f, $"tunnelled through wall at tick {i}: {state.Position}");
            if (state.Commitment.Phase is MovementCommitmentPhase.Completed or MovementCommitmentPhase.Aborted)
            {
                terminalTicks++;
                Assert.Equal(MovementCommitmentEndReason.Landed, state.Commitment.EndReason);
                break;
            }
        }

        Assert.Equal(1, terminalTicks);
        Assert.True(state.Grounded);
    }

    [Fact]
    public void Default_commitment_state_leaves_normal_movement_unchanged()
    {
        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = true,
            FacingYaw = 0.1f,
        };
        MoveCommand command = new(new Vector2(0.5f, 1f), run: true, cameraYaw: 0.4f);

        MoveState actual = CharacterMovement.Step(state, command, Dt, Flat, Tuning);
        MoveState expected = CharacterMovement.Step(state with { Commitment = default }, command, Dt, Flat, Tuning);

        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.VerticalVelocity, actual.VerticalVelocity);
        Assert.Equal(expected.FacingYaw, actual.FacingYaw);
        Assert.Equal(MovementCommitmentPhase.None, actual.Commitment.Phase);
    }

    [Fact]
    public void A_launch_blocked_at_its_start_aborts_instead_of_sticking_active()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        Vector3[] wall =
        {
            new(0.36f, -2f, -3f), new(0.36f, -2f, 3f),
            new(0.36f, 4f, 3f), new(0.36f, 4f, -3f),
        };
        world.AddStatic(new TriangleMeshShape(wall, new[] { 0, 1, 2, 0, 2, 3 }), Pose.At(Vector3.Zero));
        world.Step(Dt);
        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = true,
            Commitment = new MovementCommitment(21u, Vector2.UnitX, 20f, 7f, 0f, 3f),
        };

        state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Flat, Tuning, world: world);

        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.Blocked, state.Commitment.EndReason);
        Assert.True(state.Position.X < 0.36f, state.Position.ToString());
    }

    [Fact]
    public void A_commitment_that_never_lands_aborts_at_its_timeout()
    {
        static float FarBelow(float _, float __) => -1000f;
        var state = new MoveState
        {
            Position = new Vector3(0f, 50f, 0f),
            Grounded = false,
            Commitment = new MovementCommitment(22u, Vector2.UnitX, 5f, 4f, 0f, 3f * Dt),
        };

        for (int i = 0; i < 4 && state.Commitment.Phase != MovementCommitmentPhase.Aborted; i++)
            state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, FarBelow, Tuning);

        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.TimedOut, state.Commitment.EndReason);
    }

    [Fact]
    public void Entering_deep_water_aborts_without_a_landing_and_enters_swimming()
    {
        static MovementMedium DeepWater(float _, float __, float ___) => new(4f, inWater: true);
        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            Commitment = new MovementCommitment(23u, Vector2.UnitX, 8f, 5f, 0f, 3f),
        };

        state = CharacterMovement.Step(state, MoveCommand.Idle, Dt, Flat, Tuning, medium: DeepWater);

        Assert.True(state.Swimming);
        Assert.Equal(MovementCommitmentPhase.Aborted, state.Commitment.Phase);
        Assert.Equal(MovementCommitmentEndReason.EnteredWater, state.Commitment.EndReason);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Authored_arc_lands_within_one_fixed_step_at_common_tick_rates(int tickRate)
    {
        const float Distance = 8f;
        const float Apex = 2f;
        const float Duration = 0.54f;
        float dt = 1f / tickRate;
        float horizontalSpeed = Distance / Duration;
        float verticalSpeed = 4f * Apex / Duration;
        float gravity = 8f * Apex / (Duration * Duration);
        var state = new MoveState
        {
            Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
            Grounded = true,
            Commitment = new MovementCommitment(24u, Vector2.UnitX, horizontalSpeed, verticalSpeed, gravity,
                preparationSeconds: 0f, recoverySeconds: 0f, timeoutSeconds: 3f),
        };

        for (int i = 0; i < 180 && state.Commitment.Phase != MovementCommitmentPhase.Completed; i++)
            state = CharacterMovement.Step(state, MoveCommand.Idle, dt, Flat, Tuning);

        Assert.Equal(MovementCommitmentPhase.Completed, state.Commitment.Phase);
        Assert.InRange(MathF.Abs(state.Position.X - Distance), 0f, horizontalSpeed * dt + 1e-4f);
    }
}
