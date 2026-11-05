using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDescentMediumEvidenceTests
{
    // #1697. The established transient-swim geometry must retain selected mode history, even without analytic identity.
    [Fact]
    public void MissingIdentityDoesNotEraseTransientSwimmingOrAlterTheMovementResult()
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var fixture = new ContactDropFixture(tuning: tuning, water: MovementWaterField.Level("world", 0.985f, 0.01f));
        using var source = fixture.Context.CreateNavigationBinding(tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true }, frame, null);
        var evidence = new ContactDescentEvidence();
        bool transient = false;
        for (int tick = 0; tick < ContactDropFixture.Options.MaxEdgeProbeSteps; tick++)
        {
            Vector2 input = tick == 0 ? Vector2.Zero : ContactDropControl.Input(state.State, new(0.25f, 0, 0),
                tuning, false, 1, ContactDropFixture.Options.EdgeProbeSeconds);
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var lease));
            using (lease)
            {
                var ordinary = source.StepTowards(new(state.State, frame, null), input, false,
                    ContactDropFixture.Options.EdgeProbeSeconds, lease!);
                lease!.DescentEvidence = evidence;
                var observed = source.StepTowards(new(state.State, frame, null), input, false,
                    ContactDropFixture.Options.EdgeProbeSeconds, lease);
                LocomotionStateBits.AssertEqual(ordinary, observed, LocomotionStateBits.Policy.IncludeContinuityHandle);
                Assert.Equal(MovementStepOutcome.Advanced, observed.Outcome);
                if (!state.State.Swimming && !observed.State.State.Swimming && observed.ContactEnteredSwimming)
                {
                    Assert.True(evidence.Summary.Swimming);
                    Assert.True(evidence.Summary.Water);
                    Assert.NotNull(evidence.Summary.FirstWalkableSupport);
                    transient = true;
                }
                state = observed.State;
                if (GroundTraversalProbe.Near(state.State.Position - Vector3.UnitY * tuning.CapsuleHalfHeight, new(0.25f, 0, 0))) break;
            }
        }
        Assert.True(transient);
    }
    [Fact]
    public void ReturningWaterExcursionSurvivesItsDryLandingInTheEvidence()
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var fixture = new ContactDropFixture(tuning: tuning);
        using var source = fixture.Context.CreateNavigationBinding(tuning);
        var frame = fixture.Context.NavigationFrame;
        float gap = tuning.GroundedEpsilon + tuning.StepHeight;
        var state = new FramedMovementState(new MoveState
        {
            Position = new(0.25f, tuning.CapsuleHalfHeight + gap, 0),
            Grounded = false,
            VerticalVelocity = -gap / ContactDropFixture.Options.EdgeProbeSeconds,
            WaterExcursion = WaterExcursionState.AirborneFromWater,
        }, frame, null);
        Assert.Equal(MovementAvailability.Known, source.TryAcquire(source.CreateOrdinaryScope(frame), out var lease));
        using (lease)
        {
            var ordinary = source.StepTowards(state, Vector2.Zero, false, ContactDropFixture.Options.EdgeProbeSeconds, lease!);
            var evidence = new ContactDescentEvidence();
            lease!.DescentEvidence = evidence;
            var observed = source.StepTowards(state, Vector2.Zero, false, ContactDropFixture.Options.EdgeProbeSeconds, lease);
            LocomotionStateBits.AssertEqual(ordinary, observed, LocomotionStateBits.Policy.IncludeContinuityHandle);
            Assert.Equal(MovementStepOutcome.Advanced, observed.Outcome);
            Assert.True(observed.ContactWalkableSupport);
            Assert.Equal(WaterExcursionState.None, observed.State.State.WaterExcursion);
            Assert.False(evidence.Summary.Water || evidence.Summary.Swimming);
            Assert.True(evidence.Summary.WaterExcursion);
        }
    }

}
