using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(-0.5200002f, 6.6658833e-7f, true)]
    [InlineData(-0.5200002f, 6.6658833e-7f, false)]
    [InlineData(-0.5199998f, -6.6658833e-7f, true)]
    [InlineData(-0.5199998f, -6.6658833e-7f, false)]
    public void BuoyantBankFootprintRefusalHoldsTheOriginalTick(float centreY, float vertical, bool refuse)
    {
        // #1708: a refused bank footprint proves no clearance, for either sign of buoyant carry.
        MoveTuning tuning = Tuning with
        {
            CapsuleRadius = 0.3f,
            MaxSlopeRadians = MathF.PI / 4,
            TractionHysteresisRadians = 0,
            StepHeight = 0.4f,
            FootRadiusFraction = 0.5f,
            SwimEnterDepthFraction = 0.65f,
            SwimExitDepthFraction = 0.55f,
            SwimSurfaceSubmersionFraction = 0.6f,
            SwimBuoyancyStiffness = 8,
            SwimSpeed = 3,
            Water = new(WaterTraversalMode.SurfaceSwimmer),
        };
        static float Floor(float z) => Math.Clamp(-1.2359283f + 0.95f * (z + 236.93256f), -1.3f, 0);
        using var scene = new QueryScene(scopeMin: new(100, -4, -245), scopeMax: new(120, 4, -225));
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            var axis = new Vector2(request.Body.Centre.X, request.Body.Centre.Z);
            ground = new(axis, Floor(axis.Y), Vector3.Normalize(new(0, 1, -0.95f)),
                new("world", "floor"), Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        IMovementWaterField water = MovementWaterField.Level("world", -0.37f, -1.3f);
        scene.Environment.OnBodySample = (in MovementBodyQuery body) =>
            (double)body.Centre.Y - body.HalfHeight < Floor(body.Centre.Z)
                ? new(MovementAvailability.Known, Room, null, false, 1, null)
                : water.SampleCentreWater(body, Vector3.Zero);
        if (refuse) scene.View.RefuseFootprintAxis = new(110.5f, -236.93256f);
        var input = new FramedMovementState(new MoveState
        {
            Position = new(110.5f, centreY, -237.03256f),
            VerticalVelocity = vertical,
            HorizontalVelocity = new(0, 3),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
        }, scene.Lease.Frame, new(Room, null, scene.Lease.Identity));

        MovementStepResult result = ContactController.StepLeased(input, Vector2.UnitY, 1, false, false,
            1f / 30, tuning, tuning.Water, scene.Lease);
        MoveState body = result.State.State;
        Assert.True(body.Position.Y - tuning.CapsuleHalfHeight >= Floor(body.Position.Z), $"pose {body.Position}");
        Assert.True(body.Swimming);
        Assert.False(body.Grounded);
        Assert.False(body.SupportGranted);
        Assert.Equal(WaterExcursionState.Surface, body.WaterExcursion);
        if (refuse)
        {
            Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
            Assert.Equal(input.State.Position, body.Position);
            Assert.Equal(input.State.VerticalVelocity, body.VerticalVelocity);
            Assert.Equal(input.State.HorizontalVelocity, body.HorizontalVelocity);
            Assert.Equal(0, body.StepDeltaY);
            Assert.Equal(0, body.ClimbRate);
            Assert.Equal(0, body.LandingImpactSpeed);
        }
        else
        {
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            Assert.True(body.Position.Z > input.State.Position.Z);
        }
    }
}
