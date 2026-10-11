using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactMediumRouteTests
{
    [Fact]
    public void P4FailedMappedRetraceDiscardsEarlierAcceptedSubsteps()
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 1;
        scene.DenyMappedRetrace = true;
        var input = new MoveState { Position = new(0, 0.751f, 0), Grounded = true, JumpBufferRemaining = 1 };
        var queries = scene.Queries();
        MoveState result = ContactController.StepWithQueries(input, Vector2.Normalize(Vector2.One), 1,
            false, true, 0.5f, Tuning, queries);

        Assert.True(scene.ProgressSeen);
        Assert.Equal(input.Position, result.Position);
        Assert.Equal(0f, result.JumpBufferRemaining);
        Assert.Equal(0f, result.StepDeltaY);
        Assert.False(result.SupportGranted);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, queries.Outcome);
    }

    [Fact]
    public void AProvedShoreObstructionReportsBlockedAndKeepsAlongShoreProgress()
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 1;
        var queries = scene.Queries();
        var input = new MoveState { Position = new(0, 0.751f, 0), Grounded = true };
        MoveState result = ContactController.StepWithQueries(input, Vector2.Normalize(Vector2.One), 1,
            false, false, 0.5f, Tuning, queries);

        Assert.Equal(MovementStepOutcome.Blocked, queries.Outcome);
        Assert.InRange(result.Position.X, 0.74f, 0.75f);
        Assert.True(result.Position.Z > 2.1f);
        Assert.True(result.Grounded);
    }

    [Fact]
    public void TiltedMediumConstraintsCannotLiftAGroundSideRoute()
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 1;
        scene.WaterNormal = new(-0.8f, 0.6f, 0);
        ShellSweep result = ShellMotion.SweepWithQueries(scene.Queries(), Vector3.Zero, 0, new(2, 2),
            Tuning, MathF.Cos(Tuning.MaxSlopeRadians));

        Assert.True(result.Blocked);
        Assert.InRange(result.Achieved.X, 0.74f, 0.75f);
        Assert.Equal(2f, result.Achieved.Y);
    }

    [Fact]
    public void AStoredRouteVertexCannotHideBehindRoundedDisplacement()
    {
        using var scene = new RouteScene();
        var queries = scene.Queries(MovementBoundary.Rectangle(0, -5, 1, 5));
        Assert.False(queries.AcceptSegment(new(1, 0, 0), new(-0.00000001f, 0, 0)));
    }

    [Fact]
    public void P2RefusedRecoveryConsumesThePressAndCannotGrantSupportOrLaunch()
    {
        using var scene = new RouteScene(configure: world =>
            world.AddStatic(new BoxShape(new(0.2f, 2, 2)), Pose.At(new(0.5f, 1, 0))));
        scene.WaterMinX = 1;
        var input = new MoveState { Position = new(0.7f, 0.751f, 0), Grounded = true, JumpBufferRemaining = 1 };
        var queries = scene.Queries();
        MoveState result = ContactController.StepWithQueries(input, Vector2.UnitX, 1, false, true, 0.1f,
            Tuning, queries);

        Assert.Equal(input.Position, result.Position);
        Assert.Equal(input.Grounded, result.Grounded);
        Assert.Equal(input.VerticalVelocity, result.VerticalVelocity);
        Assert.Equal(0f, result.JumpBufferRemaining);
        Assert.False(result.SupportGranted);
        Assert.Equal(MovementStepOutcome.Blocked, queries.Outcome);
    }

    [Fact]
    public void FlightClipsVelocityAgainstEveryTiedMediumNormal()
    {
        using var scene = new RouteScene();
        scene.WaterMinX = 1;
        scene.SecondShore = true;
        Vector3 velocity = new(2, 1, 1);
        ShellFlight flight = ShellMotion.FlyWithQueries(scene.Queries(), Vector3.Zero, new(2, 0, 2),
            ref velocity, Tuning, MathF.Cos(Tuning.MaxSlopeRadians));

        Assert.True(flight.Blocked);
        Assert.InRange(flight.Achieved.X, 0.74f, 0.75f);
        Assert.InRange(flight.Achieved.Z, 0.74f, 0.75f);
        Assert.Equal(new Vector3(0, 1, 0), velocity);
    }

    [Fact]
    public void ASteepLandingSeatMustEarnItsOwnMediumProof()
    {
        using var scene = new RouteScene(0.1f, normal: new(-MathF.Sin(MathF.PI / 3), 0.5f, 0));
        scene.FailSeat = true;
        var input = new MoveState { Position = new(0, 1.001f, 0), VerticalVelocity = -2, TimeSinceGrounded = 1 };
        MoveState result = ContactController.StepWithQueries(input, Vector2.UnitX, 0.1f, false, false, 0.1f,
            Tuning with { Gravity = 0 }, scene.Queries());

        Assert.True(scene.ProgressSeen);
        Assert.Equal(input.Position, result.Position);
        Assert.Equal(input.VerticalVelocity, result.VerticalVelocity);
        Assert.False(result.SupportGranted);
    }

    [Fact]
    public void P2DeniedClearancePushCannotPublishAnAxisOutsideThePlayArea()
    {
        using var scene = new RouteScene(configure: world =>
            world.AddStatic(new BoxShape(new(0.2f, 2, 2)), Pose.At(new(0.5f, 1, 0))));
        var queries = scene.Queries(MovementBoundary.Rectangle(-5, -5, 0.8, 5));
        var support = new SupportSample(SupportStatus.Walkable, 0, 0, Vector3.UnitY, null, -1, Vector3.Zero);
        var seat = new GroundSeatResult(SeatOutcome.Seated, 0, support, 0, Vector3.Zero);
        GroundPlacement result = GroundPlacement.Try(queries, support, 0, new(0.7f, 0), Vector2.Zero,
            seat, 0.125f, Tuning, MathF.Cos(Tuning.MaxSlopeRadians), 1);

        Assert.False(result.Valid);
        Assert.Equal(Vector2.Zero, result.Total);
    }

    [Fact]
    public void ASelectedWallTangentCannotBorrowItsEndpointChordBoundaryProof()
    {
        using var scene = new RouteScene(configure: world =>
            world.AddStatic(new BoxShape(new(0.05f, 2, 4)), Pose.At(new(1, 1, 0))));
        var boundary = new RefuseTangentBoundary();
        var input = new MoveState { Position = new(0, 0.751f, 0), Grounded = true };
        MoveState result = ContactController.StepWithQueries(input, Vector2.Normalize(Vector2.One), 1,
            false, false, 0.5f, Tuning, scene.Queries(boundary));

        Assert.True(boundary.TangentSeen);
        Assert.Equal(input.Position, result.Position);
        Assert.False(result.SupportGranted);
    }

    sealed class RefuseTangentBoundary : MovementBoundary
    {
        public bool TangentSeen;
        public override string SemanticIdentity => "refuse-tangent";
        public override MovementAvailability Classify(Vector3 centre, out bool inside)
        { inside = true; return MovementAvailability.Known; }
        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained)
        { constrained = displacement; return MovementAvailability.Known; }
        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd,
            out bool admitted)
        {
            if (start.X > 0.69f && displacement.X == 0 && displacement.Z > 0)
            {
                TangentSeen = true;
                admitted = false;
                return MovementAvailability.Unresolved;
            }
            admitted = true;
            return MovementAvailability.Known;
        }
    }
}
