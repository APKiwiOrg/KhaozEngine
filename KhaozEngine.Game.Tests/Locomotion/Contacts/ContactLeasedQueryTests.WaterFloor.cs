using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void RejectedBuoyantBedSeatHoldsAboveTheFloorForTenFollowingTicks(int hz)
    {
        using var environment = LakeWithBed(0.15f);
        using var lease = environment.Acquire("room", 4, 4);
        var tuning = Tuning with { StepHeight = 0.4f, WalkSpeed = 4, SwimSpeed = 3 };
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, 0.921f, 0),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
            VerticalVelocity = -1,
            TimeSinceGrounded = 1,
        }, lease.Frame, new(Room, null, Identity));
        var boundary = new BedRiseBoundary();
        for (int tick = 0; tick < 4 && !boundary.SeatRefused; tick++)
        {
            MovementStepResult result = ContactController.StepLeased(state, Vector2.Zero, 0, false, false,
                1f / hz, tuning, new(WaterTraversalMode.SurfaceSwimmer), lease, boundary);
            if (boundary.SeatRefused)
            {
                Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
                Assert.Equal(state.State.Position, result.State.State.Position);
                Assert.Equal(state.State.VerticalVelocity, result.State.State.VerticalVelocity);
            }
            else
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
        }
        Assert.True(boundary.SeatRefused);
        Vector3 held = state.State.Position;
        float vertical = state.State.VerticalVelocity;
        Assert.True(held.Y - tuning.CapsuleHalfHeight >= 0.15f);
        for (int tick = 0; tick < 10; tick++)
        {
            // A held tick clears selection, so the next input acquires a fresh cold reconstruction witness.
            using var nextEnvironment = LakeWithBed(0.15f);
            using var next = nextEnvironment.Acquire(null, 4, 4);
            MovementStepResult result = ContactController.StepLeased(state, Vector2.Zero, 0, false, false,
                1f / hz, tuning, new(WaterTraversalMode.SurfaceSwimmer), next, boundary);
            Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
            Assert.Equal(held, result.State.State.Position);
            Assert.True(result.State.State.Position.Y - tuning.CapsuleHalfHeight >= 0.15f);
            Assert.Equal(vertical, result.State.State.VerticalVelocity);
            Assert.True(result.State.State.Swimming);
            Assert.False(result.State.State.Grounded);
            Assert.False(result.State.State.SupportGranted);
            Assert.Equal(WaterExcursionState.Surface, result.State.State.WaterExcursion);
            Assert.Null(result.State.Selection);
            state = result.State;
        }
    }

    [Theory]
    [InlineData(false, WaterExcursionState.Surface)]
    [InlineData(true, WaterExcursionState.Surface)]
    [InlineData(false, WaterExcursionState.AirborneFromWater)]
    [InlineData(true, WaterExcursionState.AirborneFromWater)]
    public void WaterDescentAtOrBelowCertifiedWalkableOrSteepSupportClipsItsVelocity(bool steep,
        WaterExcursionState excursion)
    {
        using var environment = LakeWithBed(0.15f);
        using var lease = environment.Acquire("room", 4, 4);
        Vector3 normal = steep ? new(0.8660254f, 0.5f, 0) : Vector3.UnitY;
        environment.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            ground = new(axis, 0.15f, normal, new("world", "bank"), Room, null, Identity);
            return MovementAvailability.Known;
        };
        environment.Acquisition.OnContactSupport = (in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "bank"), Room, null, Identity);
            return MovementAvailability.Known;
        };
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, 0.8f, 0),
            Swimming = excursion == WaterExcursionState.Surface,
            WaterExcursion = excursion,
            VerticalVelocity = -1,
            TimeSinceGrounded = 1,
        }, lease.Frame, new(Room, null, Identity));

        MovementStepResult result = ContactController.StepLeased(state, Vector2.Zero, 0, false, false,
            1f / 60, Tuning with { StepHeight = 0.4f, GroundedEpsilon = 0.02f },
            new(WaterTraversalMode.SurfaceSwimmer), lease);

        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Position.Y >= state.State.Position.Y);
        Assert.Equal(0, result.State.State.VerticalVelocity);
        Assert.False(result.State.State.Grounded);
        Assert.Null(result.State.Selection!.Value.Support);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SwimmingAboveTheBedKeepsTheUnobstructedWaterMotion(int hz)
    {
        using var bed = LakeWithBed(0.15f);
        using var clear = LakeWithBed(-1);
        using var bedLease = bed.Acquire("room", 4, 4);
        using var clearLease = clear.Acquire("room", 4, 4);
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0, 0.921f, 0),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
            VerticalVelocity = -0.1f,
            TimeSinceGrounded = 1,
        }, bedLease.Frame, new(Room, null, Identity));

        MovementStepResult expected = ContactController.StepLeased(state, Vector2.UnitX, 1, false, false,
            1f / hz, Tuning, new(WaterTraversalMode.SurfaceSwimmer), clearLease);
        MovementStepResult result = ContactController.StepLeased(state, Vector2.UnitX, 1, false, false,
            1f / hz, Tuning, new(WaterTraversalMode.SurfaceSwimmer), bedLease);

        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(expected, result);
        Assert.True(result.State.State.Position.Y < state.State.Position.Y);
        Assert.True(result.State.State.VerticalVelocity < 0);
    }

    static AnalyticMovementEnvironment LakeWithBed(float height)
    {
        var environment = new AnalyticMovementEnvironment(
            [new("room", new(new(-16), new(16)))],
            [new("lake", "room", new(new(-8, -4, -8), new(8, 1, 8)), 1)]);
        environment.RegisterAnalyticGround("room", "bank", height);
        return environment;
    }

    sealed class BedRiseBoundary : MovementBoundary
    {
        internal bool SeatRefused;
        public override string SemanticIdentity => "bed-refuse-rise";
        public override MovementAvailability Classify(Vector3 centre, out bool inside)
        { inside = true; return MovementAvailability.Known; }
        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
        { constrained = displacement; return MovementAvailability.Known; }
        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd,
            out bool admitted)
        {
            SeatRefused |= displacement.Y > 0;
            admitted = displacement.Y <= 0;
            return MovementAvailability.Known;
        }
    }
}
