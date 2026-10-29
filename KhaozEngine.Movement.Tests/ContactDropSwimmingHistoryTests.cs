using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public class ContactDropSwimmingHistoryTests(ITestOutputHelper output)
{
    [Fact]
    public void TransientSwimmingBetweenDryTickEndpointsCannotPublishDrop()
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        // The last fall tick samples feet near 0.085 before dry support at 0. The free surface
        // puts that selected substep on the swimmer line, with a dry start and dry final landing.
        var water = new ObservedWater(MovementWaterField.Level("world", 0.985f, 0.01f));
        using var fixture = new ContactDropFixture(tuning: tuning, water: water);
        using var source = fixture.Context.CreateNavigationBinding(tuning);
        var frame = fixture.Context.NavigationFrame;
        var scope = source.CreateOrdinaryScope(frame);
        var state = new FramedMovementState(new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true }, frame, null);
        bool observed = false, landed = false;
        Vector3 landing = new(0.25f, 0, 0);
        for (int tick = 0; tick < ContactDropFixture.Options.MaxEdgeProbeSteps; tick++)
        {
            Assert.Equal(MovementAvailability.Known, source.TryAcquire(scope, out var queries));
            using (queries)
            {
                MoveState before = state.State;
                bool swam = false;
                water.Observe = () => swam |= queries!.Workspace.Modes.Swimming;
                var input = tick == 0 || landed ? Vector2.Zero : ContactDropControl.Input(before, landing, tuning,
                    false, 1, ContactDropFixture.Options.EdgeProbeSeconds);
                var result = source.StepTowards(new(before, frame, null), input, false,
                    ContactDropFixture.Options.EdgeProbeSeconds, queries!);
                output.WriteLine($"tick={tick} from={before.Position} to={result.State.State.Position} {result.Outcome} swim={result.State.State.Swimming} entered={swam}");
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                Assert.Equal(swam, result.ContactEnteredSwimming);
                observed |= !before.Swimming && !result.State.State.Swimming && result.State.State.Grounded && swam;
                water.Observe = null;
                state = result.State;
                if (GroundTraversalProbe.Near(result.State.State.Position - Vector3.UnitY * tuning.CapsuleHalfHeight, landing))
                { landed = true; break; }
            }
        }
        Assert.True(landed);
        Assert.True(observed, "The real Contact tick must enter swimming and return to dry supported footing in one result.");
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Space.Links, link => link.Kind == NavLinkKind.Drop);
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
    }

    [Theory]
    [InlineData(WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(WaterTraversalMode.WadeOnly)]
    public void SupportedShallowWadingStillPublishesDrop(WaterTraversalMode mode)
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(mode) };
        using var fixture = new ContactDropFixture(tuning: tuning, water: MovementWaterField.Level("world", 0.1f, -1));
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
    }
    sealed class ObservedWater(IMovementWaterField inner) : IMovementWaterField
    {
        internal Action? Observe;
        public string SemanticIdentity => inner.SemanticIdentity;
        public long Generation => inner.Generation;
        public MovementWaterExtent Extent => inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
        {
            Observe?.Invoke();
            return inner.SampleCentreWater(body, origin);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
            => inner.TraceWater(query, origin, identity, spans, contacts);
    }
}
