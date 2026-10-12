using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Fact]
    public void PushedAxisSeatRebindsItsNewRawSupportUnderOneInterval()
    {
        using var geometry = PushedAxisGeometry();
        using var scene = new QueryScene(geometry);
        MovementContactSupportRequest? pushedBinding = null;
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding) =>
        {
            bool pushed = request.Static == geometry["pushed"];
            if (pushed) pushedBinding = request;
            binding = new(new("world", pushed ? "clearance-floor" : "start-floor"), Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        var queries = scene.Queries();
        var feet = new Vector3(0.7f, 0, 0);
        SupportSample start = queries.FindSupport(Query(feet));
        Assert.Equal(geometry["start"], start.Static);
        var seat = new GroundSeatResult(SeatOutcome.Seated, 0, start, 0, Vector3.Zero);

        GroundPlacement placed = GroundPlacement.Try(queries, start, 0, new(feet.X, feet.Z), Vector2.Zero,
            seat, 0.125f, Tuning, MathF.Cos(Tuning.MaxSlopeRadians), 1);

        Assert.True(placed.Valid);
        Assert.True(placed.Pushed);
        Assert.InRange(placed.Feet.X, 0.9509f, 0.9511f);
        Assert.Equal(0, placed.Feet.Y);
        Assert.Equal(0, placed.Feet.Z);
        Assert.Equal(new Vector2(placed.Feet.X - feet.X, 0), placed.Total);
        Assert.Equal(1, placed.Budget);
        Assert.Equal(SupportStatus.Walkable, placed.Support.Status);
        Assert.Equal(geometry["pushed"], placed.Support.Static);
        Assert.Equal(3, placed.Support.FeatureId);
        Assert.True(Math.Abs(placed.Support.Height) <= placed.Support.HeightError);
        Assert.Equal(new Vector2(placed.Feet.X, 0), scene.View.PushedSupportRequest!.Value.Axis);
        Assert.Equal(2, scene.View.FootprintCalls);
        Assert.Equal(2, scene.Environment.ContactSupportCalls);
        Assert.Equal(new Vector2(placed.Feet.X, 0), pushedBinding!.Value.Axis);
        Assert.Equal(placed.Support.Static, pushedBinding.Value.Static);
        Assert.Equal(placed.Support.FeatureId, pushedBinding.Value.FeatureId);
        Assert.Equal(placed.Support.Witness, pushedBinding.Value.Witness);
        MovementContactSupportBinding owner = queries.BindSupport(placed.Feet, placed.Support,
            0.125f, Tuning.MaxSlopeRadians);
        Assert.Equal(new MovementSupportKey("world", "clearance-floor"), owner.Owner);
        Assert.Equal(Room, owner.Space);
        Assert.Equal(scene.Lease.Identity, owner.Identity);
        Assert.Same(scene.View.AcquiredLease, scene.View.FootprintLease);
        Assert.Equal(1, scene.View.Acquisitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedOrExpiredPushedAxisExposesNoPlacement(bool expired)
    {
        using var geometry = PushedAxisGeometry();
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        var feet = new Vector3(0.7f, 0, 0);
        SupportSample start = queries.FindSupport(Query(feet));
        var seat = new GroundSeatResult(SeatOutcome.Seated, 0, start, 0, Vector3.Zero);
        scene.View.FailPushedSupport = expired;
        scene.View.RefusePushedSupport = !expired;
        GroundPlacement placed = default;
        if (expired)
        {
            ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() => placed = Place());
            Assert.Equal(MovementAvailability.Stale, failure.Availability);
            Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, failure.Outcome);
        }
        else placed = Place();

        Assert.Equal(default, placed);
        Assert.True(scene.View.PushMtv.X > 0);
        Assert.InRange(scene.View.PushedSupportRequest!.Value.Axis.X, 0.9509f, 0.9511f);
        Assert.Equal(2, scene.View.FootprintCalls);
        Assert.Equal(1, scene.Environment.ContactSupportCalls);
        Assert.Same(scene.View.AcquiredLease, scene.View.FootprintLease);
        Assert.Equal(1, scene.View.Acquisitions);

        GroundPlacement Place() => GroundPlacement.Try(queries, start, 0, new(feet.X, feet.Z), Vector2.Zero,
            seat, 0.125f, Tuning, MathF.Cos(Tuning.MaxSlopeRadians), 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PushedSeatSupportFaultUsesTheLeasedHoldMapping(bool expired)
    {
        using var geometry = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 4, -4, 4, 0)
            .Flat("step", 0.3f, 3, -4, 4, 0.25f);
        // The initial 1.5 m body clears the roof. Paying the step rise makes the seat require a downward push.
        geometry.World.AddStatic(new BoxShape(new(4, 0.1f, 4)), Pose.At(new(0, 1.62f, 0)));
        using var scene = new QueryScene(geometry);
        scene.View.FailPushedSupport = expired;
        scene.View.RefusePushedSupport = !expired;
        var input = new FramedMovementState(new MoveState
        { Position = new(0, 0.751f, 0), Grounded = true, JumpBufferRemaining = 1 }, scene.Lease.Frame,
            new(Room, new MovementSupportKey("world", "bank"), scene.Lease.Identity));

        MovementStepResult result = ContactController.StepLeased(input, Vector2.UnitX, 1, false, false,
            0.1f, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);

        Assert.NotNull(scene.View.PushedSupportRequest);
        Assert.True(scene.View.PushMtv.Y < 0);
        Assert.Equal(expired ? MovementStepOutcome.EnvironmentUnresolved : MovementStepOutcome.Blocked, result.Outcome);
        if (expired)
        {
            Assert.Equal(input.State.Position, result.State.State.Position);
            Assert.Equal(0, result.State.State.StepDeltaY);
            Assert.Equal(0, result.State.State.LandingImpactSpeed);
            Assert.False(result.State.State.SupportGranted);
        }
        else
        {
            Assert.Equal(0, result.State.State.Position.Y - input.State.Position.Y);
            Assert.True(result.State.State.Position.X < scene.View.PushedSupportRequest!.Value.Axis.X);
        }
        Assert.Same(scene.View.AcquiredLease, scene.View.FootprintLease);
        Assert.Equal(1, scene.View.Acquisitions);
    }

    static FootSupportScene PushedAxisGeometry()
    {
        var scene = new FootSupportScene(SceneVariant.Box)
            .Flat("start", -4, 0.8f, -4, 4, 0).Flat("pushed", 0.8f, 4, -4, 4, 0);
        // The wall starts above the footprint column, so the initial support remains valid while the shell overlaps.
        scene.World.AddStatic(new BoxShape(new(0.2f, 0.3f, 2)), Pose.At(new(0.5f, 1, 0)));
        return scene;
    }
}
