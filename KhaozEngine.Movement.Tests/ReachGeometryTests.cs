using System;
using System.Numerics;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ReachGeometryTests
{
    [Fact]
    public void CapsuleSidesUseEdgeDistanceAndExplicitTolerance()
    {
        var body = new MovementBody(new Vector3(0f, 0.75f, 0f), 0.25f, 0.75f);
        var other = new MovementBody(new Vector3(1.5f, 0.75f, 0f), 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Capsule(other);
        Assert.Equal(1f, ReachGeometry.Distance(body, target));
        Assert.True(ReachGeometry.Within(body, target, 1f));
        Assert.False(ReachGeometry.Within(body, target, 0.999f));
        Assert.False(ReachGeometry.Within(body, target, 0.99999994f));
        Assert.True(ReachGeometry.Within(body, target, 0.75f, 0.25f));
        Assert.False(ReachGeometry.Within(body, target, 0.75f, 0.249f));
        Assert.False(ReachGeometry.Within(body, target, 0.75f, 0.24999999f));
    }

    [Fact]
    public void CapsuleHorizontalSeparationIncludesZ()
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new Vector3(0f, 0f, 2f), 0.75f, 1.75f));
        Assert.Equal(1f, ReachGeometry.Distance(body, target));
    }

    [Theory]
    [InlineData(0f, 3.5f, 1f)]
    [InlineData(0f, -3.5f, 1f)]
    [InlineData(1.5f, 0f, 0.5f)]
    [InlineData(3f, 5.5f, 4f)]
    public void CapsuleSeparationUsesEachBodiesOwnDimensions(float x, float y, float distance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        var other = new MovementBody(new Vector3(x, y, 0f), 0.75f, 1.75f);
        ReachTarget target = ReachTarget.Capsule(other);
        Assert.Equal(distance, ReachGeometry.Distance(body, target));
        Assert.Equal(distance, ReachGeometry.Distance(other, ReachTarget.Capsule(body)));
    }

    [Fact]
    public void NonQuarterCapsuleDimensionsHaveNumericReach()
    {
        var body = new MovementBody(Vector3.Zero, 0.3f, 0.75f);
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new Vector3(1.2f, 0f, 0f), 0.3f, 0.75f));
        Assert.Equal(0.6f, ReachGeometry.Distance(body, target));
        Assert.True(ReachGeometry.Within(body, target, 0.6f));
        Assert.False(ReachGeometry.Within(body, target, 0.599f));
        Assert.Equal(0.5f, ReachGeometry.Distance(body,
            ReachTarget.Capsule(new MovementBody(new Vector3(0f, 2f, 0f), 0.3f, 0.75f))));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.25f, 0f, 0f)]
    [InlineData(0f, 1.5f, 0f)]
    public void TouchingOrOverlappingCapsulesClampToZero(float x, float y, float z)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new Vector3(x, y, z), 0.25f, 0.75f));
        Assert.Equal(0f, ReachGeometry.Distance(body, target));
        Assert.True(ReachGeometry.Within(body, target, 0f));
    }

    [Theory]
    [InlineData(1.5f, 0f, 0f, 1f)]
    [InlineData(0f, 2f, 0f, 1f)]
    [InlineData(3.25f, 4.75f, 0f, 4.75f)]
    [InlineData(0f, 0f, 0f, 0f)]
    public void BoxFacesAndHeightUseSegmentToIntervals(float x, float y, float z, float distance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Box(new Vector3(x, y, z), new Vector3(0.25f));
        Assert.Equal(distance, ReachGeometry.Distance(body, target));
    }

    [Fact]
    public void RotatedBoxCornersUseExactShapeDistance()
    {
        var body = new MovementBody(new Vector3(2.828427f, 0f, 0f), 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Box(Vector3.Zero, new Vector3(1f, 0.5f, 0.5f), MathF.PI / 4f);
        Assert.Equal(1.5527756f, ReachGeometry.Distance(body, target), 0.000001f);
        Assert.False(ReachGeometry.Within(body, target, 1.5f));
        Assert.True(ReachGeometry.Within(body, target, 1.553f));
    }

    [Fact]
    public void BoxYawMatchesPositiveYPhysicalRotation()
    {
        var body = new MovementBody(new Vector3(2.1213202f, 0f, -0.70710677f), 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Box(Vector3.Zero, new Vector3(1f, 0.5f, 0.5f), MathF.PI / 4f);
        Assert.Equal(0.868034f, ReachGeometry.Distance(body, target), 0.000001f);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(1.25f, 0f, 0f, 1f)]
    [InlineData(0f, 2f, 0f, 1.25f)]
    [InlineData(0f, -2f, 0f, 1.25f)]
    [InlineData(3f, 4.5f, 0f, 4.75f)]
    public void PointsUseDistanceFromCapsuleAxis(float x, float y, float z, float distance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        Assert.Equal(distance, ReachGeometry.Distance(body, ReachTarget.Point(new Vector3(x, y, z))));
    }

    [Fact]
    public void ZeroHeightBoxAndSphericalBodyRemainValid()
    {
        var body = new MovementBody(new Vector3(0f, 1f, 0f), 0.25f, 0.25f);
        ReachTarget target = ReachTarget.Box(Vector3.Zero, new Vector3(0.25f, 0f, 0.25f));
        Assert.Equal(0.75f, ReachGeometry.Distance(body, target));
    }

    [Theory]
    [InlineData(0f, 0.75f)]
    [InlineData(-0.25f, 0.75f)]
    [InlineData(1f, 0.5f)]
    [InlineData(float.NaN, 0.75f)]
    [InlineData(0.25f, float.NaN)]
    [InlineData(float.PositiveInfinity, float.PositiveInfinity)]
    [InlineData(0.25f, float.PositiveInfinity)]
    public void BodyRefusesInvalidDimensions(float radius, float halfHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementBody(Vector3.Zero, radius, halfHeight));

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    public void BodiesAndTargetsRefuseNonfinitePositions(float x, float y, float z)
    {
        var position = new Vector3(x, y, z);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementBody(position, 0.25f, 0.75f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachTarget.Point(position));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachTarget.Box(position, Vector3.One));
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, -1f, 1f)]
    [InlineData(1f, 1f, 0f)]
    [InlineData(float.NaN, 1f, 1f)]
    [InlineData(1f, float.PositiveInfinity, 1f)]
    [InlineData(1f, 1f, float.NegativeInfinity)]
    public void BoxesRefuseInvalidHalfExtents(float x, float y, float z) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachTarget.Box(Vector3.Zero, new Vector3(x, y, z)));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void BoxesRefuseNonfiniteYaw(float yaw) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachTarget.Box(Vector3.Zero, Vector3.One, yaw));

    [Fact]
    public void DefaultsAreInvalidButPointAtZeroIsValid()
    {
        MovementBody invalidBody = default;
        ReachTarget invalidTarget = default;
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        ReachTarget point = ReachTarget.Point(Vector3.Zero);
        Assert.Equal(0f, ReachGeometry.Distance(body, point));
        Assert.Throws<ArgumentException>(() => ReachTarget.Capsule(invalidBody));
        Assert.Throws<ArgumentException>(() => ReachGeometry.Distance(invalidBody, point));
        Assert.Throws<ArgumentException>(() => ReachGeometry.Distance(body, invalidTarget));
        Assert.Throws<ArgumentException>(() => ReachGeometry.Within(invalidBody, point, 0f));
        Assert.Throws<ArgumentException>(() => ReachGeometry.Within(body, invalidTarget, 0f));
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0f, -1f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.MaxValue, float.MaxValue)]
    public void WithinRefusesInvalidOrOverflowingThreshold(float range, float tolerance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachGeometry.Within(body, ReachTarget.Point(Vector3.Zero), range, tolerance));
    }

    [Fact]
    public void LargeFiniteDistanceDoesNotOverflowSquaredArithmetic()
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        float distance = ReachGeometry.Distance(body, ReachTarget.Point(new Vector3(1e20f, 0f, 1e20f)));
        Assert.Equal(1.4142136e20f, distance);
        Assert.False(ReachGeometry.Within(body, ReachTarget.Point(new Vector3(1e20f, 0f, 1e20f)), 1e20f));
    }

    [Fact]
    public void ExtremeIntervalsAndRadiiKeepTouchingDistanceFinite()
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), float.MaxValue, float.MaxValue);
        var other = new MovementBody(new Vector3(0f, -float.MaxValue, 0f), float.MaxValue, float.MaxValue);
        Assert.Equal(0f, ReachGeometry.Distance(body, ReachTarget.Capsule(other)));
        var tallBody = new MovementBody(new Vector3(0f, float.MaxValue, 0f), 1f, float.MaxValue);
        Assert.Equal(0f, ReachGeometry.Distance(tallBody, ReachTarget.Point(Vector3.Zero)));
        Assert.Equal(0f, ReachGeometry.Distance(tallBody,
            ReachTarget.Box(new Vector3(0f, float.MaxValue, 0f), new Vector3(1f, float.MaxValue, 1f))));
    }

    [Fact]
    public void UnrepresentableDistanceIsRefused()
    {
        var body = new MovementBody(new Vector3(float.MaxValue, 0f, 0f), 0.25f, 0.75f);
        ReachTarget target = ReachTarget.Point(new Vector3(-float.MaxValue, 0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, float.MaxValue));
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(0.25f)]
    [InlineData(float.Epsilon)]
    public void ExtremeCapsuleAxisKeepsLocalPointSeparation(float gap)
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), 1f, float.MaxValue);
        ReachTarget target = ReachTarget.Point(new Vector3(0f, -gap, 0f));
        Assert.Equal(gap, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 0f));
        Assert.True(ReachGeometry.Within(body, target, gap));
    }

    [Fact]
    public void ExtremeCapsuleAxisKeepsOtherCapsulesLocalSeparation()
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), 1f, float.MaxValue);
        var other = new MovementBody(new Vector3(0f, -3f, 0f), 0.25f, 1f);
        Assert.Equal(2f, ReachGeometry.Distance(body, ReachTarget.Capsule(other)));
        Assert.Equal(2f, ReachGeometry.Distance(other, ReachTarget.Capsule(body)));
        Assert.False(ReachGeometry.Within(body, ReachTarget.Capsule(other), 0f));
    }

    [Fact]
    public void ExtremeCapsuleAxisKeepsBoxVerticalSeparation()
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), 1f, float.MaxValue);
        ReachTarget target = ReachTarget.Box(new Vector3(0f, -3f, 0f), new Vector3(1f));
        Assert.Equal(2f, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 0f));
    }

    [Fact]
    public void ExtremeBoxExtentKeepsLocalFaceSeparation()
    {
        var body = new MovementBody(new Vector3(float.MaxValue, 0f, 0f), 1f, 1f);
        ReachTarget target = ReachTarget.Box(new Vector3(-3f, 0f, 0f), new Vector3(float.MaxValue, 1f, 1f));
        Assert.Equal(2f, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 0f));
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(0.25f)]
    [InlineData(float.Epsilon)]
    public void ExtremeSphereRadiusKeepsLocalPointSeparation(float gap)
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), float.MaxValue, float.MaxValue);
        ReachTarget target = ReachTarget.Point(new Vector3(0f, -gap, 0f));
        Assert.Equal(gap, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 0f));
        Assert.True(ReachGeometry.Within(body, target, gap));
    }

    [Fact]
    public void ExtremeSphereRadiusKeepsDiagonalNormCorrection()
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), float.MaxValue, float.MaxValue);
        ReachTarget target = ReachTarget.Point(new Vector3(1e20f, -2f, 0f));
        Assert.Equal(16.693682f, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 16.69f));
        Assert.True(ReachGeometry.Within(body, target, 16.694f));
    }

    [Fact]
    public void ExtremeCapsuleAxisKeepsDiagonalLocalSeparation()
    {
        var body = new MovementBody(new Vector3(0f, float.MaxValue, 0f), 1f, float.MaxValue);
        ReachTarget target = ReachTarget.Point(new Vector3(2f, -2f, 0f));
        Assert.Equal(2.6055512f, ReachGeometry.Distance(body, target));
        Assert.False(ReachGeometry.Within(body, target, 2.6f));
    }

    [Theory]
    [InlineData(float.MaxValue, 1f)]
    [InlineData(1f, float.MaxValue)]
    [InlineData(float.MaxValue, float.Epsilon)]
    [InlineData(float.Epsilon, float.MaxValue)]
    public void WithinRefusesOverflowEvenWhenDoubleAdditionLosesSmallOperand(float range, float tolerance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachGeometry.Within(body, ReachTarget.Point(Vector3.Zero), range, tolerance));
    }

    [Theory]
    [InlineData(float.MaxValue, 0f)]
    [InlineData(0f, float.MaxValue)]
    [InlineData(float.MaxValue / 2f, float.MaxValue / 2f)]
    public void WithinAcceptsThresholdExactlyAtFiniteLimit(float range, float tolerance)
    {
        var body = new MovementBody(Vector3.Zero, 0.25f, 0.75f);
        Assert.True(ReachGeometry.Within(body, ReachTarget.Point(Vector3.Zero), range, tolerance));
    }
}
