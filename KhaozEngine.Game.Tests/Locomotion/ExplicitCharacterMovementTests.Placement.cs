using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void A1AdmissionDoorsUseTheBankFootprintAndShellInDryAndShallowWater(int door, bool wet)
    {
        using var scene = new Scene(floor: true, bankHeight: 0.25f, cold: true, waterRegions: wet
            ? [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)] : []);
        var basis = scene.State(new(0.9f, 0.991f, 0), grounded: true);
        var input = new FramedMovementState(basis.State with
        {
            JumpBufferRemaining = 0.08f,
            TimeSinceGrounded = 0.03f,
            FacingYaw = 0.7f,
            HorizontalVelocity = new(2, 1),
        }, basis.Frame, basis.Selection);
        MovementStepResult result = door switch
        {
            0 => ExplicitCharacterMovement.ValidatePlacement(input, Tuning, Policy, scene.Lease),
            1 => ExplicitCharacterMovement.SettlePlacement(input, Tuning, Policy, scene.Lease),
            _ => ExplicitCharacterMovement.ReclassifyContinuation(input, Tuning, Policy, scene.Lease),
        };
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
        Assert.Equal(input.State.Position.X, result.State.State.Position.X);
        Assert.Equal(input.State.FacingYaw, result.State.State.FacingYaw);
        Assert.Equal(input.State.HorizontalVelocity, result.State.State.HorizontalVelocity);
        if (door == 1)
        {
            // Geometric bank height 0.25 + full half-height 0.75 + vertical skin 0.001.
            Assert.InRange(result.State.State.Position.Y, 1.0009f, 1.0011f);
            Assert.Equal(0, result.State.State.JumpBufferRemaining);
        }
        else Assert.Equal(input.State, result.State.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AdmissionBelowTheKneeDoesNotUseAFullBodySolidPrecheck(int door)
    {
        using var scene = new Scene(floor: true, cold: true);
        var input = scene.State(new(0, 0.741f, 0), grounded: true);
        MovementStepResult result = door switch
        {
            0 => ExplicitCharacterMovement.ValidatePlacement(input, Tuning, Policy, scene.Lease),
            1 => ExplicitCharacterMovement.SettlePlacement(input, Tuning, Policy, scene.Lease),
            _ => ExplicitCharacterMovement.ReclassifyContinuation(input, Tuning, Policy, scene.Lease),
        };
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(new MovementSupportKey("world", "floor"), result.State.Selection!.Value.Support);
        Assert.Equal(door == 1 ? new Vector3(0, 0.751f, 0) : input.State.Position, result.State.State.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ForbiddenPlacementStillSamplesTheFullBodyBelowADryShell(int door)
    {
        using var scene = new Scene(floor: true, cold: true, waterRegions:
            [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var basis = scene.State(new(0, 0.751f, 0), grounded: true);
        var input = new FramedMovementState(basis.State with { JumpBufferRemaining = 0.08f }, basis.Frame, null);
        var policy = new WaterTraversalPolicy(WaterTraversalMode.DryOnly);
        MovementStepResult result = door switch
        {
            0 => ExplicitCharacterMovement.ValidatePlacement(input, Tuning, policy, scene.Lease),
            1 => ExplicitCharacterMovement.SettlePlacement(input, Tuning, policy, scene.Lease),
            _ => ExplicitCharacterMovement.ReclassifyContinuation(input, Tuning, policy, scene.Lease),
        };
        Assert.Equal(MovementStepOutcome.PlacementRefused, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Null(result.State.Selection);
    }
}
