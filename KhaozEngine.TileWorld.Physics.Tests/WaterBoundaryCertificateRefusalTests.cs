using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The listed refusal changes at movement level, on the one-level #1697 shore. Where the full solve of a route
/// proof refuses, a step through a field that hides the certificate holds with that refusal, as it always has. Through
/// the certifying field the step continues past that proof, and the later checks decide it: here they publish exactly
/// the step a full solve that did not refuse publishes. The kernel's own refusal tests are unchanged.</summary>
public class WaterBoundaryCertificateRefusalTests(ITestOutputHelper output)
{
    static readonly MovementBoundary Boundary = MovementBoundary.Rectangle(64, -320, 128, -256);
    static readonly MoveCommand TowardWater = new(-Vector2.UnitY, false, 0);

    [Theory]
    [InlineData(MovementAvailability.PrecisionExceeded, MovementStepOutcome.PrecisionRefused, WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(MovementAvailability.PrecisionExceeded, MovementStepOutcome.PrecisionRefused, WaterTraversalMode.WadeOnly)]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved, WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved, WaterTraversalMode.WadeOnly)]
    public void ARefusedRouteProofHoldsTheHiddenStepAndTheCertifiedStepContinuesPastIt(MovementAvailability refusal,
        MovementStepOutcome held, WaterTraversalMode body)
    {
        RouteRefusingWaterField? hiddenFault = null, certifiedFault = null;
        int wetTraces = 0;
        using var clean = new TileWaterMovementScene(ContactDropPrecisionTests.Shore(), body, Boundary,
            sampler => new RecordingWaterField(sampler) { Recorded = (_, result, _, _) => wetTraces += result.ContactsWritten > 0 ? 1 : 0 });
        using var hidden = new TileWaterMovementScene(ContactDropPrecisionTests.Shore(), body, Boundary,
            sampler => new RecordingWaterField(hiddenFault = new RouteRefusingWaterField(sampler, refusal)));
        using var certified = new TileWaterMovementScene(ContactDropPrecisionTests.Shore(), body, Boundary,
            sampler => certifiedFault = new RouteRefusingWaterField(sampler, refusal));
        PlayerMoveState state = clean.Standing(99.5f, -298.6f);
        for (int tick = 0; tick < 4; tick++) state = clean.Step(state, MoveCommand.Idle, out _);

        // Placement is proved Known, then the lift, traverse or seat refuses and the whole tick holds.
        PlayerMoveState refused = hidden.Step(state, TowardWater, out MovementStepOutcome? hiddenOutcome);
        Assert.Equal(held, hiddenOutcome);
        Assert.Equal(state.Move.Position, refused.Move.Position);
        Assert.True(hiddenFault!.Refused > 0);

        for (int tick = 0; tick < 20; tick++)
        {
            PlayerMoveState expected = clean.Step(state, TowardWater, out MovementStepOutcome? cleanOutcome);
            PlayerMoveState actual = certified.Step(state, TowardWater, out MovementStepOutcome? certifiedOutcome);
            Assert.Equal(MovementStepOutcome.Advanced, cleanOutcome);
            Assert.Equal(cleanOutcome, certifiedOutcome);
            LocomotionStateBits.AssertEqual(expected, actual, LocomotionStateBits.Policy.ExcludeContinuityHandle);
            state = expected;
        }
        output.WriteLine($"{refusal} {body}: final {state.Move.Position}, wet traces {wetTraces}, hidden refused {hiddenFault.Refused}");
        // The walk reaches the shore's water, so the certificate answered one free level and not only empty reaches.
        Assert.True(wetTraces > 0);
        // The refusing solve never ran on the certified side.
        Assert.Equal(0, certifiedFault!.Refused);
    }
}
