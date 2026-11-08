using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPhysicalEvidenceTests(ITestOutputHelper output)
{
    // #1697. Continuous terrain keeps the sharp recipe's local horizon and independent physical gates.
    [Theory]
    [InlineData(1.3f)]
    [InlineData(4f)]
    [InlineData(6f)]
    public void ContinuousTerrainDoesNotAcquireSharpDropCandidates(float height)
    {
        using var fixture = new ContactTerrainFixture(height);
        var nav = fixture.Capture.BuildProfile(ContactTerrainFixture.Tuning, default,
            new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.True(nav.Footprint.Accepts(fixture.From));
        Assert.True(nav.Footprint.Accepts(fixture.To));
        Assert.Empty(ContactDropCandidates.Generate(nav.Space.Layers, nav.Graph.Layers, ContactTerrainFixture.Tuning, 6));
        var corridor = new NavDropFootprint(nav.Footprint, ContactTerrainFixture.Tuning, fixture.From, fixture.To, 6);
        Assert.False(corridor.Certify());
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == KhaozEngine.Navigation.NavLinkKind.Drop);
    }

    // #1697. Physical slide feasibility does not satisfy the released sharp Drop's contact and first-landing gates.
    [Theory]
    [InlineData(1.3f, false)]
    [InlineData(1.3f, true)]
    [InlineData(4f, false)]
    [InlineData(4f, true)]
    [InlineData(6f, false)]
    [InlineData(6f, true)]
    public void ContinuousTerrainSlidesAndFirstSupportsOutsideTheAdmittedLanding(float height, bool run)
    {
        using var fixture = new ContactTerrainFixture(height);
        using var source = fixture.Context.CreateNavigationBinding(ContactTerrainFixture.Tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState
        { Position = fixture.From + Vector3.UnitY * ContactTerrainFixture.Tuning.CapsuleHalfHeight, Grounded = true }, frame, null);
        var footprint = new NavAreaFootprint(fixture.Capture.Columns, ContactTerrainFixture.Options, ContactTerrainFixture.Tuning, default);
        bool steep = false, supported = false, arrived = false;
        for (int slice = 0; slice < ContactTerrainFixture.Options.MaxEdgeProbeSteps; slice++)
        {
            Vector2 input = slice == 0 || arrived ? Vector2.Zero : ContactDropControl.Input(state.State,
                fixture.To, ContactTerrainFixture.Tuning, run, 1, ContactTerrainFixture.Options.EdgeProbeSeconds);
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var queries));
            using (queries)
            {
                var result = source.StepTowards(new(state.State, frame, null), input, run,
                    ContactTerrainFixture.Options.EdgeProbeSeconds, queries!);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                Assert.False(result.State.State.Swimming || result.ContactEnteredSwimming);
                Assert.False(result.ContactAirLanded);
                Assert.Null(result.ContactFirstAirLandingFeet);
                steep |= result.ContactUnwalkableSupport;
                Vector3 feet = result.State.State.Position - Vector3.UnitY * ContactTerrainFixture.Tuning.CapsuleHalfHeight;
                if (steep && !supported && result.ContactWalkableSupport)
                {
                    supported = true;
                    Assert.False(footprint.Accepts(feet));
                    Assert.True(feet.X < fixture.To.X);
                    Assert.Equal(0, feet.Y);
                }
                if (supported && result.ContactWalkableSupport && GroundTraversalProbe.Near(feet, fixture.To))
                {
                    Assert.True(footprint.Accepts(feet));
                    if (arrived) return;
                    arrived = true;
                }
                state = result.State;
            }
        }
        Assert.Fail("The continuous terrain path did not reach lower support and Hold the anchor inside the existing budget.");
    }

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
