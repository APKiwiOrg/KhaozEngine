using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationPlacementAdmissionTests
{
    [Fact]
    public void VerticalDisplacementJustOutsideOneMillimetreRefuses()
    {
        Vector3 anchor = new(4, 0, -8);
        Vector3 settled = new(4, MathF.BitIncrement(0.001f), -8);
        Assert.False(ContactNavigationProof.AdmitsPlacement(anchor, settled,
            MovementStepOutcome.Advanced, grounded: true, hasFooting: true));
    }

    [Fact]
    public void SidewaysDisplacementJustOutsideOneMillimetreRefuses()
    {
        Vector3 anchor = new(0, 2, -4);
        Vector3 settled = new(MathF.BitIncrement(0.001f), 2, -4);
        Assert.False(ContactNavigationProof.AdmitsPlacement(anchor, settled,
            MovementStepOutcome.Advanced, grounded: true, hasFooting: true));
    }

    [Fact]
    public void CombinedDisplacementOutsideTheSphereRefuses()
    {
        Assert.False(ContactNavigationProof.AdmitsPlacement(Vector3.Zero, new(0.00075f, 0.00075f, 0),
            MovementStepOutcome.Advanced, grounded: true, hasFooting: true));
    }

    [Theory]
    [InlineData(0.001f, 0, 0)]
    [InlineData(0, 0.001f, 0)]
    [InlineData(0, 0, 0.001f)]
    public void DisplacementExactlyOnOneMillimetreSphereAdmits(float x, float y, float z)
    {
        Assert.True(ContactNavigationProof.AdmitsPlacement(Vector3.Zero, new(x, y, z),
            MovementStepOutcome.Advanced, grounded: true, hasFooting: true));
    }

    [Theory]
    [InlineData(MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementStepOutcome.Blocked)]
    [InlineData(MovementStepOutcome.EnvironmentInvalid)]
    [InlineData(MovementStepOutcome.PlacementRefused)]
    [InlineData(MovementStepOutcome.FrameMismatch)]
    public void NonAdvancedOutcomeRefuses(MovementStepOutcome outcome)
    {
        Assert.False(ContactNavigationProof.AdmitsPlacement(Vector3.Zero, Vector3.Zero,
            outcome, grounded: true, hasFooting: true));
    }

    [Fact]
    public void NotGroundedRefuses()
    {
        Assert.False(ContactNavigationProof.AdmitsPlacement(Vector3.Zero, Vector3.Zero,
            MovementStepOutcome.Advanced, grounded: false, hasFooting: true));
    }

    [Fact]
    public void WithoutFootingRefuses()
    {
        Assert.False(ContactNavigationProof.AdmitsPlacement(Vector3.Zero, Vector3.Zero,
            MovementStepOutcome.Advanced, grounded: true, hasFooting: false));
    }
}
