using System;
using System.Numerics;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void LandJumpKeepsTheSharedTicksPreJumpPlacementAndGrant()
    {
        using var dryScene = FootSupportScenes.Floor(SceneVariant.Box);
        using var scene = new Scene(floor: true);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        var water = new WaterTraversalPolicy(WaterTraversalMode.SurfaceSwimmer, 30);
        MoveState dry = ContactController.Step(state.State, Vector2.Zero, 0, false, true,
            0.1f, (_, _) => -1000, Tuning, null, dryScene.World, null, null);
        Assert.Equal(7, dry.VerticalVelocity);
        Assert.True(dry.SupportGranted);
        Assert.False(dry.Grounded);
        Assert.Equal(state.State.Position, dry.Position);

        var result = ExplicitCharacterMovement.Step(state, new(Vector2.Zero, false, 0, jump: true),
            0.1f, Tuning, water, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(7, result.State.State.VerticalVelocity);
        Assert.True(result.State.State.SupportGranted);
        Assert.False(result.State.State.Grounded);
        Assert.Null(result.State.Selection!.Value.Support);
        Assert.True(result.State.State.Position.Y <= state.State.Position.Y);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void ShallowLandingLatchesImpactAndGrantBeforeBufferedLandLaunch(int hz)
    {
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        float dt = 1f / hz;
        var basis = scene.State(new(0, 0.761f, 0));
        var input = basis.State with { VerticalVelocity = -2, JumpBufferRemaining = 0.1f };
        var result = ExplicitCharacterMovement.Step(new(input, basis.Frame, basis.Selection), MoveCommand.Idle,
            dt, Tuning, Policy, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(7, result.State.State.VerticalVelocity);
        Assert.True(result.State.State.SupportGranted);
        Assert.False(result.State.State.Grounded);
        Assert.InRange(result.State.State.LandingImpactSpeed, 2 + 20 * dt - 0.0005f, 2 + 20 * dt + 0.0005f);
        Assert.InRange(result.State.State.Position.Y, 0.7505f, 0.7515f);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.ClimbRate);
        Assert.Equal(0, result.State.State.JumpBufferRemaining);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Null(result.State.Selection!.Value.Support);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SupportedShallowBankReportsTheSameAcceptedGroundLedgerAsDry(int hz)
    {
        using var dry = new Scene(floor: true, bankHeight: 0.25f);
        using var wet = new Scene(floor: true, bankHeight: 0.25f, waterRegions:
            [new Water("shallows", "room", new(new(-8, -1, -8), new(8, 0.3f, 8)), 0.3f)]);
        var tuning = Tuning with { WadeStartDepthFraction = 0.3f };
        var a = dry.State(new(0, 0.751f, 0), grounded: true);
        var b = wet.State(new(0, 0.751f, 0), grounded: true);
        float paidRise = 0;
        for (int tick = 0; tick < hz / 2; tick++)
        {
            var ar = ExplicitCharacterMovement.StepTowards(a, Vector2.UnitX, false, 1f / hz, tuning, Policy, dry.Lease);
            var br = ExplicitCharacterMovement.StepTowards(b, Vector2.UnitX, false, 1f / hz, tuning, Policy, wet.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, ar.Outcome);
            Assert.Equal(ar.Outcome, br.Outcome);
            a = ar.State;
            b = br.State;
            Assert.Equal(a.State, b.State);
            paidRise += a.State.StepDeltaY;
            Assert.False(b.State.Swimming);
        }
        Assert.True(a.State.Position.X > 1.1f);
        Assert.InRange(paidRise, 0.2495f, 0.2505f);
    }
    [Fact]
    public void InvalidContactShellCannotAdmitAnExplicitLandTick()
    {
        using var dry = FootSupportScenes.Floor(SceneVariant.Box);
        using var scene = new Scene(floor: true, waterRegions:
            [new Water("channel", "room", new(new(1, 0, -8), new(1.05f, 0.6f, 8)), 0.6f)]);
        var tuning = Tuning with { CapsuleHalfHeight = 0.4f };
        var input = scene.State(new(0, 0.402f, 0), grounded: true);
        // Required shell span is 2 * 0.4 - 0.4 = 0.4, below its diameter 2 * 0.25 = 0.5.
        var failure = Assert.Throws<ArgumentException>(() => ContactController.Step(input.State,
            Vector2.UnitX, 1, false, false, 1, (_, _) => -1000, tuning, null, dry.World, null, null));
        Assert.Contains("must be at least 2 * CapsuleRadius", failure.Message);
        var result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitX, false, 1, tuning,
            new(WaterTraversalMode.WadeOnly), scene.Lease);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
    }

}
