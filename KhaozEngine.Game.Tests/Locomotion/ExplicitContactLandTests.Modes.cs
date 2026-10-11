using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void LateLeasedLandRefusalResetsTheEntireGroundLedgerAndKeepsTheInputCommitment()
    {
        using var scene = new Scene(floor: true);
        var basis = scene.State(new(0, 0.751f, 0), grounded: true);
        MoveState input = basis.State with
        {
            StepDeltaY = 0.2f,
            ClimbRate = 1,
            ClimbRateEwma = 1,
            LandingImpactSpeed = 3,
            SupportGranted = true,
            JumpBufferRemaining = 1,
            Commitment = new(4, Vector2.UnitX, 4, 4, 20, 0, 0, 3)
        };
        var trace = scene.Environment.Acquisition.OnCoverage!;
        bool acceptedPrefix = false;
        scene.Environment.Acquisition.OnCoverage = (in MovementMediumSweepQuery query,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        {
            if (query.Body.Centre.X > 0.1f && query.Delta.X > 0)
            {
                acceptedPrefix = true;
                return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, Identity);
            }
            return trace(query, spans, contacts);
        };
        var result = ExplicitCharacterMovement.Step(new(input, basis.Frame, basis.Selection),
            MoveCommand.Idle, 0.1f, Tuning, Policy, scene.Lease);
        Assert.True(acceptedPrefix);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(input.Position, result.State.State.Position);
        Assert.Equal(input.Commitment, result.State.State.Commitment);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.ClimbRate);
        Assert.Equal(0, result.State.State.ClimbRateEwma);
        Assert.Equal(0, result.State.State.LandingImpactSpeed);
        Assert.False(result.State.State.SupportGranted);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Null(result.State.Selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WadeOnlyAirborneShallowsUseTheSharedCoyoteJumpAndClearStaleWaterHistory(bool staleSwimming)
    {
        using var scene = new Scene(waterRegions:
            [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var basis = scene.State(new(0, 0.9f, 0));
        MoveState move = basis.State with
        {
            TimeSinceGrounded = 0,
            Swimming = staleSwimming,
            WaterExcursion = staleSwimming ? WaterExcursionState.Surface : WaterExcursionState.None,
            ClimbRateEwma = 1
        };
        var result = ExplicitCharacterMovement.Step(new(move, basis.Frame, basis.Selection),
            new(Vector2.Zero, false, 0, jump: true), 1f / 30, Tuning,
            new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(7, result.State.State.VerticalVelocity);
        Assert.False(result.State.State.Grounded);
        Assert.False(result.State.State.SupportGranted);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Equal(0, result.State.State.ClimbRateEwma);
        Assert.Null(result.State.Selection!.Value.Support);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SupportedShallowStairsShareTheDryGroundRunAndResetAtTheLanding(int hz)
    {
        using var dry = new ContactStairScene(false);
        using var wet = new ContactStairScene(true);
        var tuning = Tuning with { WadeStartDepthFraction = 0.3f };
        var a = dry.State;
        var b = wet.State;
        bool climbed = false;
        for (int tick = 0; tick < hz; tick++)
        {
            Vector2 direction = a.State.Position.X < 1.5f ? Vector2.UnitX : Vector2.Zero;
            var ar = ExplicitCharacterMovement.StepTowards(a, direction, false, 1f / hz, tuning, Policy, dry.Lease);
            var br = ExplicitCharacterMovement.StepTowards(b, direction, false, 1f / hz, tuning, Policy, wet.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, ar.Outcome);
            Assert.Equal(ar.Outcome, br.Outcome);
            a = ar.State;
            b = br.State;
            Assert.Equal(a.State, b.State);
            Assert.True(b.State.Grounded);
            Assert.False(b.State.Swimming);
            if (a.State.ClimbRate > 0)
            {
                climbed = true;
                Assert.Equal(0, a.State.StepDeltaY);
            }
        }
        Assert.True(climbed);
        Assert.InRange(a.State.Position.Y, 1.5005f, 1.5015f);
        Assert.Equal(0, a.State.ClimbRate);
        Assert.Equal(0, a.State.ClimbRateEwma);
        Assert.Equal(0, a.State.StepDeltaY);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaceCameraTurnsOnceAcrossSupportedShallowsAndCommitmentKeepsItsHeading(bool committed)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("shallows", "room", new(new(1, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var tuning = Tuning with { FacingTurnSpeed = 1.5f, WadeStartDepthFraction = 0.3f };
        var basis = scene.State(new(0.6f, 0.751f, 0), grounded: true);
        MoveState move = basis.State;
        if (committed)
        {
            move.Grounded = false;
            move.HorizontalVelocity = new(4, 0);
            move.FacingYaw = 0.7f;
            move.Commitment = new MovementCommitment(2, Vector2.UnitX, 4, 4, 20, 0, 0, 3)
            { Phase = MovementCommitmentPhase.Airborne };
        }
        var input = new FramedMovementState(move, basis.Frame,
            new(Space("room"), committed ? null : new MovementSupportKey("world", "floor"), Identity));
        var result = ExplicitCharacterMovement.Step(input,
            new(new(0, 1), false, -MathF.PI / 2, faceCamera: true), 0.1f, tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Position.X > 0.75f);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(committed ? 0.7f : -0.15f, result.State.State.FacingYaw);
        Assert.True(MathF.Abs(result.State.State.FacingYaw - move.FacingYaw) <= tuning.FacingTurnSpeed * 0.1f);
        if (committed) Assert.Equal(2.9f, result.State.State.Commitment.TimeoutRemaining);
    }

    sealed class ContactStairScene : IDisposable
    {
        readonly AnalyticMovementEnvironment environment;
        internal MovementQueryLease Lease { get; }
        internal FramedMovementState State => new(new MoveState
        { Position = new(-0.5f, 0.751f, 0), Grounded = true }, Lease.Frame,
            new(Space("room"), new MovementSupportKey("world", "floor"), Identity));

        internal ContactStairScene(bool wet)
        {
            environment = new([new Room("room", new(new(-16), new(16)))], wet
                ? [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)] : []);
            var floor = environment.Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
            environment.RegisterBoxSupport(floor, "floor", "room");
            for (int k = 1; k <= 3; k++)
            {
                float x = 0.35f * (k - 1), top = 0.25f * k;
                var handle = environment.Physics.AddStatic(new BoxShape(new((2.7f - x) / 2, top / 2, 1)),
                    Pose.At(new((2.7f + x) / 2, top / 2, 0)));
                environment.RegisterBoxSupport(handle, "step" + k, "room");
            }
            Lease = environment.Acquire("room", 4, 4);
            environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                int count = 0;
                candidates[count++] = new(new("world", "floor"), Space("room"),
                    new(request.Body.Centre.X, 0, request.Body.Centre.Z), Vector3.UnitY, null);
                for (int k = 1; k <= 3; k++)
                    if (request.Body.Centre.X >= 0.35f * (k - 1) && request.Body.Centre.X <= 2.7f &&
                        MathF.Abs(request.Body.Centre.Z) <= 1)
                        candidates[count++] = new(new("world", "step" + k), Space("room"),
                            new(request.Body.Centre.X, 0.25f * k, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, count, count, Identity);
            };
        }

        public void Dispose() { Lease.Dispose(); environment.Dispose(); }
    }
}
