using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Fact]
    public void ProvedSteepHandoffObstructionFromGroundReportsBlockedAndHoldsTheWholeTick()
    {
        using var scene = new QueryScene();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            ground = axis.X < 0.1f
                ? new(axis, 0, Vector3.UnitY, new("world", "floor"), Room, null, scene.Lease.Identity)
                : axis.X < 0.2f ? null : new(axis, -0.025f, new(0.8660254f, 0.5f, 0),
                    new("world", "floor"), Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        var input = new FramedMovementState(new MoveState
        { Position = new(0, 0.751f, 0), Grounded = true, JumpBufferRemaining = 1 }, scene.Lease.Frame,
            new(Room, new MovementSupportKey("world", "floor"), scene.Lease.Identity));
        var boundary = new RefuseRiseBoundary();

        MovementStepResult result = ContactController.StepLeased(input, Vector2.UnitX, 1, false, false,
            0.1f, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease, boundary);

        Assert.True(boundary.SteepRiseSeen, $"outcome {result.Outcome}, pose {result.State.State.Position}");
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.True(result.State.State.Grounded);
        Assert.Null(result.State.Selection);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.False(result.State.State.SupportGranted);
    }

    [Fact]
    public void ProvedReseatObstructionHoldsTheOriginalClassificationAndClearsSelection()
    {
        using var scene = new QueryScene(_ => 0);
        var input = new FramedMovementState(new MoveState
        {
            Position = new(0, 0.151f, 0),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
            TimeSinceGrounded = 1,
            JumpBufferRemaining = 1,
            ClimbRate = 2,
            ClimbRateEwma = 2,
        }, scene.Lease.Frame, new(Room, null, scene.Lease.Identity));

        MovementStepResult result = ContactController.StepLeased(input, Vector2.Zero, 0, false, false,
            0.1f, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease, new RefuseRiseBoundary());

        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Equal(input.State.Swimming, result.State.State.Swimming);
        Assert.Equal(input.State.WaterExcursion, result.State.State.WaterExcursion);
        Assert.Null(result.State.Selection);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(0, result.State.State.ClimbRateEwma);
    }

    [Fact]
    public void NonfiniteFinalCorrectionReturnsNoContinuationEvidence()
    {
        using var scene = new QueryScene(_ => 0);
        int clamps = 0;
        MoveState input = new() { Position = new(0, 0.751f, 0), Grounded = true };
        MoveState result = ContactController.StepWithQueries(input, Vector2.Zero, 0, false, false,
            0.1f, Tuning, scene.Queries(), out var continuation,
            clampXz: (x, z) => ++clamps == 1 ? new(x, z) : new(float.NaN, z));

        Assert.Equal(input.Position, result.Position);
        Assert.Equal(default, continuation);
    }

    [Fact]
    public void RejectedSubmergedBedSeatClipsDescentAtTheUnraisedSubstep()
    {
        using var scene = new QueryScene(_ => 0);
        var queries = new LeasedContactQueryContext(scene.Lease, Tuning,
            new(Room, null, scene.Lease.Identity), new(Room, Vector3.Zero),
            boundary: new RefuseRiseBoundary());

        AirStepResult result = AirPass.StepWithMedium(new(0, 0.05f, 0), Vector2.Zero, -1, 0.1f,
            0, Tuning, Settings, Tuning.MaxSlopeRadians, queries, null, null, new SubmergedBedPolicy());

        Assert.Equal(AirOutcome.Airborne, result.Outcome);
        Assert.InRange(result.Feet.Y, -0.050001f, -0.049999f);
        Assert.Equal(0, result.VerticalVelocity);
        Assert.True(result.MediumHandoff);
        Assert.True(result.DescendingAtContact);
        Assert.Equal(SupportStatus.None, result.Support.Status);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(1f)]
    public void RejectedLiftOntoARisingSubmergedBedCannotPublishPenetration(float vertical)
    {
        using var scene = new QueryScene(x => x > 0.05f ? 0.2f : -1);
        var queries = new LeasedContactQueryContext(scene.Lease, Tuning,
            new(Room, null, scene.Lease.Identity), new(Room, Vector3.Zero),
            boundary: new RefuseRiseBoundary(pureRiseOnly: true));

        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() =>
            AirPass.StepWithMedium(Vector3.Zero, Vector2.UnitX, vertical, 0.1f,
                0, Tuning, Settings, Tuning.MaxSlopeRadians, queries, null, null,
                new SubmergedBedPolicy(), buoyant: true));

        Assert.Equal(MovementStepOutcome.Blocked, failure.Outcome);
    }

    sealed class SubmergedBedPolicy : ContactMediumPolicy
    {
        internal override bool AllowsFooting(Vector3 feet, in SupportSample support) => false;
        internal override bool ChangesOwner(Vector3 feet, float verticalVelocity, in SupportSample support,
            bool grounded) => false;
    }

    sealed class RefuseRiseBoundary(bool pureRiseOnly = false) : MovementBoundary
    {
        internal bool SteepRiseSeen;
        public override string SemanticIdentity => "refuse-rise";
        public override MovementAvailability Classify(Vector3 centre, out bool inside)
        { inside = true; return MovementAvailability.Known; }
        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
        { constrained = displacement; return MovementAvailability.Known; }
        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd,
            out bool admitted)
        {
            SteepRiseSeen |= start.X > 0.2f && displacement.X == 0 && displacement.Y > 0;
            admitted = displacement.Y <= 0 || pureRiseOnly && (displacement.X != 0 || displacement.Z != 0);
            return MovementAvailability.Known;
        }
    }
}
