using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationAdmissionRefusalTests
{
    [Theory]
    [InlineData(NavigationBuildRefusal.MovementLeaseSourceMismatch)]
    [InlineData(NavigationBuildRefusal.MovementLeaseGenerationMismatch)]
    [InlineData(NavigationBuildRefusal.MovementLeaseOriginMismatch)]
    [InlineData(NavigationBuildRefusal.MovementLeaseFrameMismatch)]
    [InlineData(NavigationBuildRefusal.MovementLeaseIdentityChanged)]
    public void LeaseMismatchNamesOnlyTheFirstFailedInput(NavigationBuildRefusal reason)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var other = new OrdinaryContactScene();
        using var proof = fixture.Proof(false);
        using var lease = fixture.Scene.Acquire();
        MovementQueryLeaseId expected = lease.Id;
        MovementFrameDescriptor frame = fixture.Context.NavigationFrame;
        var actualFrame = reason == NavigationBuildRefusal.MovementLeaseOriginMismatch
            ? new(frame.Frame, Vector3.UnitY, frame.Epoch)
            : reason == NavigationBuildRefusal.MovementLeaseFrameMismatch
                ? new MovementFrameDescriptor(frame.Frame, frame.PhysicsOrigin, frame.Epoch + 1) : frame;
        var actual = new MovementQueryLeaseId(
            reason == NavigationBuildRefusal.MovementLeaseSourceMismatch ? other.Physics : expected.SourceWorld,
            reason == NavigationBuildRefusal.MovementLeaseGenerationMismatch ? expected.GeometryGeneration + 1 : expected.GeometryGeneration,
            reason == NavigationBuildRefusal.MovementLeaseIdentityChanged ? expected.EnvironmentGeneration + 1 : expected.EnvironmentGeneration,
            actualFrame.Epoch);
        var failure = Assert.Throws<NavigationBuildException>(() =>
            proof.InjectMismatchedLeaseForTests(fixture.From, actual, actualFrame));
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(6, failure.CellX);
        Assert.Equal(4, failure.CellZ);
        switch (reason)
        {
            case NavigationBuildRefusal.MovementLeaseGenerationMismatch:
                Assert.Equal(expected.GeometryGeneration, failure.ExpectedGeneration);
                Assert.Equal(actual.GeometryGeneration, failure.ActualGeneration);
                break;
            case NavigationBuildRefusal.MovementLeaseOriginMismatch:
                Assert.Equal(Vector3.Zero, failure.ExpectedOrigin);
                Assert.Equal(Vector3.UnitY, failure.ActualOrigin);
                break;
            case NavigationBuildRefusal.MovementLeaseFrameMismatch:
                Assert.Equal(frame, failure.ExpectedFrame);
                Assert.Equal(actualFrame, failure.ActualFrame);
                break;
            case NavigationBuildRefusal.MovementLeaseIdentityChanged:
                Assert.Equal(expected, failure.ExpectedLease);
                Assert.Equal(actual, failure.ActualLease);
                break;
        }
        proof.InjectMismatchedLeaseForTests(fixture.From, expected, frame);
    }

    [Fact]
    public void ContextFrameChangeReportsBothFramesAndTheCell()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var proof = fixture.Proof(false);
        var expected = fixture.Context.NavigationFrame;
        var changed = new MovementFrameDescriptor(expected.Frame, expected.PhysicsOrigin, expected.Epoch + 1);
        var failure = Assert.Throws<NavigationBuildException>(() =>
            proof.InjectChangedProofFrameForTests(fixture.From, changed));
        Assert.Equal(NavigationBuildRefusal.ProofFrameChanged, failure.Reason);
        Assert.Equal(expected, failure.ExpectedFrame);
        Assert.Equal(changed, failure.ActualFrame);
        Assert.Equal(6, failure.CellX);
        Assert.Equal(4, failure.CellZ);
        proof.InjectChangedProofFrameForTests(fixture.From, expected);
    }

    [Fact]
    public void LeaseUnavailableReportsTheAvailabilityAndCell()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var proof = fixture.Proof(false);
        var failure = Assert.Throws<NavigationBuildException>(() =>
            proof.InjectUnavailableLeaseForTests(fixture.From, MovementAvailability.Stale));
        Assert.Equal(NavigationBuildRefusal.MovementLeaseUnavailable, failure.Reason);
        Assert.Equal(MovementAvailability.Stale, failure.Availability);
        Assert.Equal(6, failure.CellX);
        Assert.Equal(4, failure.CellZ);
        proof.InjectUnavailableLeaseForTests(fixture.From, MovementAvailability.Known);
    }

    [Fact]
    public void UnboundedSupportReportsTheInputsUsedToConstructIt()
    {
        var failure = Assert.Throws<NavigationBuildException>(() =>
            ContactNavigationEnvelope.InjectUnboundedSupportForTests(float.MaxValue, 1, 0));
        Assert.Equal(NavigationBuildRefusal.SupportEnvelopeUnbounded, failure.Reason);
        Assert.Equal(float.MaxValue, failure.FootRadius);
        Assert.Equal(1, failure.SupportReach);
        Assert.Equal(0, failure.SupportSlope);
        ContactNavigationEnvelope.InjectUnboundedSupportForTests(0.25f, 1, 0);
    }
}
