using System;
using KhaozEngine.Locomotion.Contacts;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropBoundaryTests
{
    [Theory]
    [InlineData(MovementAvailability.Known)]
    [InlineData(MovementAvailability.Unresolved)]
    public void BakeFallHonorsOriginalSegmentBoundaryAndUnavailableEvidence(MovementAvailability availability)
    {
        var boundary = new FlightBoundary(availability);
        using var fixture = new ContactDropFixture(boundary: boundary);
        int candidates = 0;
        using var hooks = ContactNavigationBuildHooks.Install(new() { DropCandidate = _ => { candidates++; boundary.Armed = true; } });
        if (availability == MovementAvailability.Known)
        {
            var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
            Assert.Contains(nav.Space.Links, link => link.Kind == NavLinkKind.Drop);
            Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        }
        else
        {
            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Capture.BuildProfile(ContactDropFixture.Tuning,
                default, new GroundProfileOptions { MaxDropMetres = 6 }));
            Assert.Contains("EnvironmentUnresolved", failure.Message, StringComparison.Ordinal);
        }
        Assert.True(candidates > 0);
        Assert.True(boundary.Refusals > 0);
    }

    [Theory]
    [InlineData(MovementAvailability.Known, MovementStepOutcome.Blocked)]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    public void CorridorBearingPredictionRetainsOriginalSegmentBoundary(MovementAvailability availability, MovementStepOutcome outcome)
    {
        var boundary = new FlightBoundary(availability);
        using var fixture = new ContactDropFixture(boundary: boundary);
        var tuning = ContactDropFixture.Tuning;
        var footprint = new NavAreaFootprint(fixture.Capture.Columns, ContactDropFixture.Options, tuning, default);
        var corridor = new NavDropFootprint(footprint, tuning, new(-0.25f, 4, 0), new(0.25f, 0, 0), 6);
        Assert.True(corridor.Certify());
        boundary.Armed = true;
        var state = new MoveState { Position = new(0, 3.15f, 0), Grounded = false, VerticalVelocity = -10 };
        Assert.True(corridor.Contains(state.Position - Vector3.UnitY * tuning.CapsuleHalfHeight));
        var frame = fixture.Context.NavigationFrame;
        var result = fixture.Context.PredictContactStep(new(state, frame, null), Vector2.UnitX, false,
            ContactDropFixture.Options.EdgeProbeSeconds, tuning, new ContactDropPredictionBasis(), corridor);
        if (availability == MovementAvailability.Known)
        {
            Assert.Contains(result.Outcome, new[] { MovementStepOutcome.Advanced, MovementStepOutcome.Blocked });
            Assert.True(result.State.State.Position.X <= 0.05f);
            Vector3 end = state.Position + new Vector3(0.1f, 0, 0);
            Assert.True(corridor.Contains(end - Vector3.UnitY * tuning.CapsuleHalfHeight));
            Assert.Equal(MovementAvailability.Known, corridor.Compose(boundary, frame).ProveSegment(state.Position,
                end - state.Position, end, out bool admitted));
            Assert.False(admitted);
        }
        else Assert.Equal(outcome, result.Outcome);
        Assert.True(boundary.Refusals > 0);
    }

    // Friend-only fault producer. Ordinary production boundaries are immutable rectangles or circles.
    // The arm changes at the Drop phase, so unavailable motion cannot be hidden by node refusal.
    sealed class FlightBoundary : AnalyticBoundary
    {
        sealed class Fault(MovementAvailability availability)
        {
            internal readonly MovementAvailability Availability = availability;
            internal bool Armed;
            internal int Refusals;
        }
        readonly Fault _fault;
        readonly float _originX;
        internal FlightBoundary(MovementAvailability availability) : this(new Fault(availability), 0) { }
        FlightBoundary(Fault fault, float originX) : base("flight-segment-exclusion/" + fault.Availability)
            => (_fault, _originX) = (fault, originX);
        internal bool Armed { set => _fault.Armed = value; }
        internal int Refusals => _fault.Refusals;
        internal override AnalyticBoundary InFrame(Vector3 origin) => new FlightBoundary(_fault, origin.X);
        protected override bool Inside(float x, float z, float dx, float dz)
        {
            if (!_fault.Armed || (double)x + dx + _originX <= 0.05f) return true;
            _fault.Refusals++;
            if (_fault.Availability != MovementAvailability.Known) throw new ContactQueryFailure(_fault.Availability);
            return false;
        }
        protected override Vector2 Project(Vector2 desired, double inset) => new(0.05f - _originX, desired.Y);
    }
}
