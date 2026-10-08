using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentQueryValueTests
{
    static readonly MovementSpaceKey Space = new("world", "room");
    static readonly MovementSupportKey Support = new("world", "floor");
    static readonly MovementQueryIdentity Identity = new("closure", 1u, "scope");
    static readonly MovementBodyQuery Body = new(Vector3.Zero, 0.3f, 0.75f, Space, null);

    [Theory]
    [InlineData("owner")]
    [InlineData("position")]
    [InlineData("normal")]
    [InlineData("world")]
    public void SupportCandidateRejectsInvalidProvenanceOrGeometry(string fault)
    {
        var owner = fault == "owner" ? default : Support;
        var space = fault == "world" ? new MovementSpaceKey("another-world", "room") : Space;
        var feet = fault == "position" ? new Vector3(float.NaN) : Vector3.Zero;
        var normal = fault == "normal" ? Vector3.Zero : Vector3.UnitY;
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementSupportCandidate(owner, space, feet, normal, null));
    }

    [Fact]
    public void SupportCandidateRetainsCanonicalOwnerAndTransitionProvenance()
    {
        MovementSupportCandidate candidate = new MovementSupportCandidate(Support, Space, Vector3.Zero, Vector3.UnitY, "door");
        Assert.Equal(Support, candidate.Owner);
        Assert.Equal("door", candidate.TraversedLinkId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransitionContextRejectsMissingSpaceOrNonfiniteOrigin(bool badPosition)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MovementTransitionContext(
            badPosition ? Space : default, badPosition ? new Vector3(float.NaN) : Vector3.Zero));
    }

    [Theory]
    [InlineData(-1f, 2f, 0.8f)]
    [InlineData(0.5f, float.PositiveInfinity, 0.8f)]
    [InlineData(0.5f, 2f, float.NaN)]
    public void SupportRequestRejectsInvalidSearchOrSlope(float rise, float drop, float slope)
    {
        MovementTransitionContext transition = new MovementTransitionContext(Space, Body.Feet);
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementSupportRequest(Body, rise, drop, slope, transition));
    }

    [Theory]
    [InlineData(-0.1f, 1f)]
    [InlineData(0.5f, 0.4f)]
    [InlineData(0f, 1.1f)]
    public void CoverageSpanRejectsInvalidFractionRange(float enter, float exit)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MovementCoverageSpan(enter, exit, 0, 0, true));
    }

    [Fact]
    public void KnownCoverageCannotOmitPortableIdentity()
    {
        Assert.ThrowsAny<ArgumentException>(() => new MovementCoverageResult(MovementAvailability.Known,
            1, 1, 0, 0, 0.001f, default(MovementQueryIdentity)));
    }

    [Fact]
    public void MediumSweepRejectsNonfiniteDisplacement()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementMediumSweepQuery(Body, new Vector3(float.NaN)));
    }

    [Fact]
    public void FramedStatePreservesTheRecordedOriginAndUnresolvedSelection()
    {
        var state = new MoveState { Position = new Vector3(1f, 2f, 3f) };
        var frame = new MovementFrameDescriptor(new WorldFrame(1, -1), new Vector3(128f, 32f, -128f), 4ul);
        FramedMovementState framed = new FramedMovementState(state, frame, null);
        Assert.Equal(state.Position, framed.State.Position);
        Assert.Equal(frame, framed.Frame);
        Assert.Null(framed.Selection);
    }

    [Fact]
    public void DomainContactRetainsClippedIntervalAndLocalRegionProvenance()
    {
        var domain = new MovementDomainKey("world", "river");
        var interval = new MovementWaterInterval(-4f, 0f, 0f, true, "bed", "surface");
        MovementDomainContact contact = new MovementDomainContact(domain, Space, interval, Vector2.Zero, Vector3.UnitX, 0.25f, "bank", 0u, MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping);
        Assert.Equal(interval, contact.Interval);
        Assert.Equal(0u, contact.CoverageRegionHandle);
    }

    [Fact]
    public void DomainContactCannotCrossWorldIdentities()
    {
        var domain = new MovementDomainKey("another-world", "river");
        var interval = new MovementWaterInterval(-4f, 0f, 0f, true, "bed", "surface");
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementDomainContact(domain, Space, interval, Vector2.Zero, Vector3.UnitX, 0.25f, "bank", 0u, MovementContactOverlap.Overlapping, MovementContactOverlap.Overlapping));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    public void SupportSetRejectsContradictoryKnownCounts(int written, int required)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new MovementSupportSet(MovementAvailability.Known, written, required, Identity));
    }
}
