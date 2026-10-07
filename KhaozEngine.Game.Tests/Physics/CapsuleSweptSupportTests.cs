using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// A solid-support interval is a premise here, not proof of installed geometry or candidate completeness.
public class CapsuleSweptSupportTests
{
    [Fact]
    public void AWholeSweepStoppingBeforeThePlaneHasPositiveSeparation()
    {
        GeometryInterval gap = Gap(Vector3.Zero, 0.25f, 0.5f, Vector3.UnitX, -Vector3.UnitX,
            GeometryInterval.Exact(-2));
        Contains(gap, 0.75);
        Assert.True(gap.Lower > 0);
    }

    [Fact]
    public void OppositeDirectionUsesTheOtherPathEndpoint()
    {
        GeometryInterval gap = Gap(Vector3.Zero, 0.25f, 0.5f, -Vector3.UnitX, Vector3.UnitX,
            GeometryInterval.Exact(-2));
        Contains(gap, 0.75);
        Assert.True(gap.Lower > 0);
    }

    [Fact]
    public void ClearEndpointsCannotHideAThinInteriorCrossing()
    {
        const float radius = 0.25f, solidHalfWidth = 0.03125f;
        Vector3 start = new(-1f, 0f, 0f), displacement = new(2f, 0f, 0f);
        Assert.True(Math.Abs(start.X) > radius + solidHalfWidth);
        Assert.True(Math.Abs((start + displacement).X) > radius + solidHalfWidth);
        GeometryInterval gap = Gap(start, radius, 0.5f, displacement, -Vector3.UnitX,
            GeometryInterval.Exact(solidHalfWidth));
        Contains(gap, -1.28125);
        Assert.True(gap.Upper < 0);
        // This axis cannot prove Clear. A negative projection gap alone is not a Hit certificate.
    }

    [Fact]
    public void ClosedTangencyCannotAcquireAStrictlyPositiveLowerGap()
    {
        GeometryInterval gap = Gap(new Vector3(-1f, 0f, 0.28125f), 0.25f, 0.5f,
            new Vector3(2f, 0f, 0f), Vector3.UnitZ, GeometryInterval.Exact(0.03125));
        Contains(gap, 0);
        Assert.True(gap.Lower <= 0);
    }

    [Fact]
    public void NonunitAxisScalesTheGapWithoutNormalizingItsPremise()
    {
        GeometryInterval gap = Gap(new Vector3(-1f, 0f, 0.3125f), 0.25f, 0.5f,
            new Vector3(2f, 0f, 0f), Vector3.UnitZ * 2f, GeometryInterval.Exact(0.0625));
        Contains(gap, 0.0625);
        Assert.True(gap.Lower > 0);
    }

    [Fact]
    public void VerticalExtentIncludesTheCylinderAndSphericalRadius()
    {
        GeometryInterval gap = Gap(new Vector3(0f, 2f, 0f), 0.25f, 0.5f,
            Vector3.Zero, Vector3.UnitY, GeometryInterval.Exact(1));
        Contains(gap, 0.25);
        Assert.True(gap.Lower > 0);
    }

    [Fact]
    public void ZeroDisplacementStillIncludesTheWholeStationaryCapsule()
    {
        GeometryInterval gap = Gap(new Vector3(2f, 0f, 0f), 0.25f, 0.5f,
            Vector3.Zero, Vector3.UnitX, GeometryInterval.Exact(0));
        Contains(gap, 1.75);
        Assert.True(gap.Lower > 0);
    }

    [Fact]
    public void ReversingTheSameVolumePreservesItsMathematicalMinimum()
    {
        GeometryInterval forward = Gap(new Vector3(-2f, 0f, 0f), 0.25f, 0.5f,
            new Vector3(4f, 0f, 0f), Vector3.UnitX, GeometryInterval.Exact(-3));
        GeometryInterval reverse = Gap(new Vector3(2f, 0f, 0f), 0.25f, 0.5f,
            new Vector3(-4f, 0f, 0f), Vector3.UnitX, GeometryInterval.Exact(-3));
        Contains(forward, 0.75);
        Contains(reverse, 0.75);
        Assert.True(forward.Lower > 0 && reverse.Lower > 0);
    }

    [Fact]
    public void MissingSupportOrZeroAxisCannotProveSeparation()
    {
        Assert.False(Gap(Vector3.Zero, 0.25f, 0.5f, Vector3.UnitX, Vector3.UnitY, default).IsResolved);
        Assert.False(Gap(Vector3.Zero, 0.25f, 0.5f, Vector3.UnitX, Vector3.Zero,
            GeometryInterval.Exact(-1)).IsResolved);
    }

    [Fact]
    public void MalformedCapsuleOrPathInputsRefuse()
    {
        GeometryInterval support = GeometryInterval.Exact(-1);
        Assert.False(CapsuleSweptSupport.Evaluate(Vector3.Zero, -0.25f, 0.5f, Vector3.UnitX, Vector3.UnitY, support).IsResolved);
        Assert.False(CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, -0.5f, Vector3.UnitX, Vector3.UnitY, support).IsResolved);
        Assert.False(CapsuleSweptSupport.Evaluate(Vector3.Zero, float.NaN, 0.5f, Vector3.UnitX, Vector3.UnitY, support).IsResolved);
        Assert.False(CapsuleSweptSupport.Evaluate(new Vector3(float.NaN), 0.25f, 0.5f, Vector3.UnitX, Vector3.UnitY, support).IsResolved);
        Assert.False(CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, 0.5f, new Vector3(float.PositiveInfinity), Vector3.UnitY, support).IsResolved);
        Assert.False(CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, 0.5f, Vector3.UnitX, new Vector3(float.NaN), support).IsResolved);
    }

    static void Contains(GeometryInterval interval, double exact)
    {
        Assert.True(interval.IsResolved);
        Assert.True(interval.Lower <= exact && interval.Upper >= exact);
        Assert.True(double.IsFinite(interval.Lower) && double.IsFinite(interval.Upper));
    }

    static GeometryInterval Gap(Vector3 centre, float radius, float halfCylinderLength,
        Vector3 displacement, Vector3 axis, GeometryInterval support) =>
        CapsuleSweptSupport.Evaluate(centre, radius, halfCylinderLength, displacement, axis, support);

}
