using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class CapsuleSweepApertureTests
{

    [Fact]
    public void WholeHorizontalCapsulePathIsEnclosed()
    {
        var result = Create(Vector3.Zero, new(4, 0, 0));
        Encloses(result, new(-0.25f, -0.75f, -0.25f), new(4.25f, 0.75f, 0.25f));
    }

    [Fact]
    public void SignedDisplacementAndNegativeCoordinatesKeepBothEndpoints()
    {
        var result = Create(new(2, -4, 8), new(-4, 3, -2));
        Encloses(result, new(-2.25f, -4.75f, 5.75f), new(2.25f, -0.25f, 8.25f));
    }

    [Fact]
    public void LostPositiveEndpointBitsStillExpandTheUpperBound()
    {
        Assert.Equal(1f, 1f + float.Epsilon);
        var result = Create(Vector3.UnitX, new(float.Epsilon, 0, 0));
        Assert.True(result.Complete);
        Assert.True(result.Max.X > 1.25f);
    }

    [Fact]
    public void LostNegativeEndpointBitsStillExpandTheLowerBound()
    {
        Assert.Equal(-1f, -1f - float.Epsilon);
        var result = Create(-Vector3.UnitX, new(-float.Epsilon, 0, 0));
        Assert.True(result.Complete);
        Assert.True(result.Min.X < -1.25f);
    }

    [Fact]
    public void LargeTranslationDoesNotUseTheRoundedFloatEndpoint()
    {
        const float start = 16777216f;
        Assert.Equal(start, start + 1f);
        var result = Create(new(start, 0, 0), Vector3.UnitX);
        Assert.True(result.Complete);
        Assert.True((double)result.Min.X <= (double)start - 0.25);
        Assert.True((double)result.Max.X >= (double)start + 1.25);
    }

    [Fact]
    public void StationaryCapsuleRetainsItsFullAxialExtent() =>
        Encloses(Create(new(2, 3, 4), Vector3.Zero), new(1.75f, 2.25f, 3.75f), new(2.25f, 3.75f, 4.25f));

    [Fact]
    public void InvalidInputsRefuseWithoutBounds()
    {
        Refused(Create(new(float.NaN, 0, 0), Vector3.Zero));
        Refused(Create(Vector3.Zero, new(float.PositiveInfinity, 0, 0)));
        Refused(Create(Vector3.Zero, Vector3.Zero, radius: 0));
        Refused(Create(Vector3.Zero, Vector3.Zero, halfCylinderLength: -float.Epsilon));
    }

    [Fact]
    public void UnencodableExpandedBoundsRefuseWithoutAPartialAperture() =>
        Refused(Create(new(float.MaxValue, 0, 0), Vector3.Zero, radius: float.MaxValue));

    static void Encloses((bool Complete, Vector3 Min, Vector3 Max) result, Vector3 min, Vector3 max)
    {
        Assert.True(result.Complete);
        Assert.True(result.Min.X <= min.X && result.Min.Y <= min.Y && result.Min.Z <= min.Z);
        Assert.True(result.Max.X >= max.X && result.Max.Y >= max.Y && result.Max.Z >= max.Z);
        Assert.True(float.IsFinite(result.Min.X) && float.IsFinite(result.Min.Y) && float.IsFinite(result.Min.Z));
        Assert.True(float.IsFinite(result.Max.X) && float.IsFinite(result.Max.Y) && float.IsFinite(result.Max.Z));
    }

    static void Refused((bool Complete, Vector3 Min, Vector3 Max) result)
    {
        Assert.False(result.Complete);
        Assert.Equal(Vector3.Zero, result.Min);
        Assert.Equal(Vector3.Zero, result.Max);
    }

    static (bool Complete, Vector3 Min, Vector3 Max) Create(Vector3 centre, Vector3 displacement,
        float radius = 0.25f, float halfCylinderLength = 0.5f)
    {
        bool complete = CapsuleSweepAperture.TryCreate(centre, radius, halfCylinderLength, displacement,
            out Vector3 min, out Vector3 max);
        return (complete, min, max);
    }
}
