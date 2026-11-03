using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPhysicalEvidenceTests(ITestOutputHelper output)
{
    [Fact]
    public void ExactSixMetreFallHasAnAdvancedPhysicalPathWithoutTheNavigationBoundary()
    {
        using var fixture = new ContactDropFixture(6);
        using var source = fixture.Context.CreateNavigationBinding(ContactDropFixture.Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState { Position = new(-0.25f, 6.75f, 0), Grounded = true }, frame, null);
        Vector3 target = new(0.25f, 0, 0);
        bool flew = false, landed = false;
        for (int slice = 0; slice < ContactDropFixture.Options.MaxEdgeProbeSteps; slice++)
        {
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var queries));
            using (queries)
            {
                var unselected = new FramedMovementState(state.State, frame, null);
                Assert.Equal(MovementAvailability.Known,
                    source.SampleWaterForPacing(unselected, queries!, out _, out float landScale).Availability);
                Vector2 input = slice == 0 || landed ? Vector2.Zero : ContactDropControl.Input(state.State,
                    target, ContactDropFixture.Tuning, false, landScale, ContactDropFixture.Options.EdgeProbeSeconds);
                var result = source.StepTowards(unselected, input, false, ContactDropFixture.Options.EdgeProbeSeconds, queries!);
                Vector3 feet = result.State.State.Position - Vector3.UnitY * ContactDropFixture.Tuning.CapsuleHalfHeight;
                output.WriteLine($"slice={slice} {result.Outcome} feet={feet} grounded={result.State.State.Grounded}");
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                Assert.False(result.State.State.Swimming);
                flew |= !result.State.State.Grounded || result.ContactAirLanded;
                if (flew && result.State.State.Grounded && GroundTraversalProbe.Near(feet, target))
                {
                    if (landed) return;
                    landed = true;
                }
                state = result.State;
            }
        }
        Assert.Fail("The physical controller did not complete the fall and post-landing Hold within 64 slices.");
    }
}
