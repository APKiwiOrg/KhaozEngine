using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Supplied interval arithmetic only. No installed geometry, eligible normal or contact is certified.
public class GeometryVectorOperationsTests
{
    static readonly BigInteger BinaryScale = BigInteger.One << 1074;

    [Fact]
    public void DotEnclosesTheSignedBoxExtrema()
    {
        GeometryVector box = Box(1, 2, -4, -2, 3, 5);
        GeometryInterval result = Dot(box, Point(-3, 2, -1));
        // -3*x + 2*y - z ranges from -6-8-5 = -19 to -3-4-3 = -10.
        Contains(result, -19, 1);
        Contains(result, -10, 1);
    }

    [Fact]
    public void DotDoesNotLoseTheUnitBetweenCancellingLargeTerms()
    {
        const double large = 9007199254740992d;
        // The represented inputs give exactly 2^53 + 1 - 2^53 = 1, not rounded zero.
        Contains(Dot(Point(large, 1, -large), Point(1, 1, 1)), 1, 1);
    }

    [Fact]
    public void CrossWithPositiveZEnclosesTheSignedAxisExtrema()
    {
        GeometryVector result = Cross(Box(2, 3, -4, -2, 1, 2), Point(0, 0, 1));
        // (x,y,z) cross +Z = (y,-x,0), including both negative axis directions.
        Contains(result.X, -4, 1);
        Contains(result.X, -2, 1);
        Contains(result.Y, -3, 1);
        Contains(result.Y, -2, 1);
        Contains(result.Z, 0, 1);
    }

    [Fact]
    public void NormalizeEnclosesTheSignedThreeFourFiveDirection()
    {
        GeometryVector result = Normalize(Point(3, -4, 0));
        // The exact norm is 5. The normalized components are 3/5, -4/5 and 0.
        Contains(result.X, 3, 5);
        Contains(result.Y, -4, 5);
        Contains(result.Z, 0, 1);
    }

    [Fact]
    public void NormalizeEnclosesEveryDirectionDespiteAComponentCrossingZero()
    {
        GeometryVector result = Normalize(Box(3, 4, -4, 4, 0, 0));
        // x/sqrt(x*x+y*y) ranges over [3/5,1]. The y component ranges over [-4/5,4/5].
        // The extrema occur at (3,+/-4,0) and at y=0. No vector in this box is zero.
        Contains(result.X, 3, 5);
        Contains(result.X, 1, 1);
        Contains(result.Y, -4, 5);
        Contains(result.Y, 4, 5);
        Contains(result.Z, 0, 1);
    }

    [Fact]
    public void NormalizeRefusesABoxWhoseNormIncludesZero()
    {
        // The midpoint is nonzero, but the box contains the origin and has norm range [0,sqrt(6)].
        Assert.False(Normalize(Box(-1, 1, -2, 2, 0, 1)).IsResolved);
    }

    [Fact]
    public void NormalizeRefusesAnOverflowingNorm()
    {
        // sqrt(2)*double.MaxValue exceeds the finite binary64 range, before any division.
        Assert.False(Normalize(Point(double.MaxValue, double.MaxValue, 0)).IsResolved);
    }

    [Fact]
    public void PublishOfAnAlreadyRepresentedPointPermitsZeroError()
    {
        GeometryVector point = Point(3, -4, 0);
        Output result = Publish(point);
        AssertPublicationEnclosesBox(result, point);
        // Zero is permitted. A positive conservative bound is also valid under this contract.
    }

    [Fact]
    public void PublishIncludesEveryAxisOfLargeFrameFloatRounding()
    {
        const double halfSpacing = 1d / 8192;
        GeometryVector point = Point(2048 + halfSpacing, -2048 - halfSpacing, 2048 + halfSpacing);
        Output result = Publish(point);
        AssertPublicationEnclosesBox(result, point);
        // Each coordinate lies halfway between adjacent floats spaced 2^-12 apart.
        // Every possible represented Vector3 is at least sqrt(3)*2^-13 away.
        AssertSquaredErrorAtLeast(result.Error, 3, BigInteger.One << 26);
    }

    [Fact]
    public void PublishEnclosesTheWholeBoxWithEuclideanRatherThanAxisError()
    {
        GeometryVector box = Box(-1, 1, -1, 1, -1, 1);
        Output result = Publish(box);
        AssertPublicationEnclosesBox(result, box);
        // For any representative, a farthest corner has squared distance at least 3.
        // This also checks outward float rounding when the representative is the origin.
        AssertSquaredErrorAtLeast(result.Error, 3, 1);
    }

    [Fact]
    public void PublishRefusesAnUnrepresentableCoordinateWithoutPartialOutput()
    {
        // No finite float representative plus finite float error can reach double.MaxValue.
        AssertDefault(Publish(Point(double.MaxValue, 3, -4)));
    }

    [Fact]
    public void PublishRefusesAnUnrepresentableEuclideanErrorWithoutPartialOutput()
    {
        double extent = float.MaxValue;
        // All endpoints encode as finite floats. Even the optimal center needs sqrt(3)*extent.
        AssertDefault(Publish(Box(-extent, extent, -extent, extent, -extent, extent)));
    }

    [Fact]
    public void DefaultCannotBecomeResolvedEvenThroughMultiplicationByZero()
    {
        AssertAllRefuse(default);
    }

    [Fact]
    public void ANonfiniteComponentCannotPublishTheOtherFiniteComponents()
    {
        GeometryVector invalid = Point(double.NaN, 3, -4);
        AssertAllRefuse(invalid);
    }

    static void AssertAllRefuse(GeometryVector invalid)
    {
        GeometryVector zero = Point(0, 0, 0);
        Assert.False(Dot(invalid, zero).IsResolved);
        Assert.False(Dot(zero, invalid).IsResolved);
        Assert.False(Cross(invalid, zero).IsResolved);
        Assert.False(Cross(zero, invalid).IsResolved);
        Assert.False(Normalize(invalid).IsResolved);
        AssertDefault(Publish(invalid));
    }

    static GeometryVector Point(double x, double y, double z) =>
        new(GeometryInterval.Exact(x), GeometryInterval.Exact(y), GeometryInterval.Exact(z));

    static GeometryVector Box(double lx, double ux, double ly, double uy, double lz, double uz) =>
        new(GeometryInterval.Enclose(lx, ux), GeometryInterval.Enclose(ly, uy), GeometryInterval.Enclose(lz, uz));

    // No static reference to either absent Task 3 type. Missing contracts fail assertions at runtime.
    static GeometryInterval Dot(GeometryVector a, GeometryVector b) =>
        Assert.IsType<GeometryInterval>(Invoke("Dot", typeof(GeometryInterval), a, b));

    static GeometryVector Cross(GeometryVector a, GeometryVector b) =>
        Assert.IsType<GeometryVector>(Invoke("Cross", typeof(GeometryVector), a, b));

    static GeometryVector Normalize(GeometryVector value) =>
        Assert.IsType<GeometryVector>(Invoke("Normalize", typeof(GeometryVector), value));

    static Output Publish(GeometryVector value)
    {
        object result = Invoke("Publish", Required("GeometryVectorOutput"), value);
        return new(Read<bool>(result, "IsResolved"), Read<Vector3>(result, "Value"), Read<float>(result, "Error"));
    }

    static Type Required(string name)
    {
        Type? type = typeof(GeometryVector).Assembly.GetType("KhaozEngine.Physics.Bepu." + name);
        Assert.True(type is not null, "Missing Task 3 type " + name);
        Assert.True(type!.IsNotPublic, name + " must remain internal");
        return type!;
    }

    static object Invoke(string name, Type returnType, params GeometryVector[] values)
    {
        Type[] parameters = values.Length == 2 ? [typeof(GeometryVector), typeof(GeometryVector)] : [typeof(GeometryVector)];
        MethodInfo? method = Required("GeometryVectorOperations").GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, parameters, null);
        Assert.True(method is not null, "Missing Task 3 method " + name);
        Assert.Equal(returnType, method!.ReturnType);
        object[] arguments = values.Length == 2 ? [values[0], values[1]] : [values[0]];
        try
        {
            object? result = method!.Invoke(null, arguments);
            Assert.NotNull(result);
            return result!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    static T Read<T>(object value, string name)
    {
        PropertyInfo? property = value.GetType().GetProperty(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(typeof(T), property.PropertyType);
        return Assert.IsType<T>(property.GetValue(value));
    }

    static void AssertDefault(Output value)
    {
        Assert.False(value.IsResolved);
        Assert.Equal(Vector3.Zero, value.Value);
        Assert.Equal(0f, value.Error);
    }

    static void Contains(GeometryInterval interval, BigInteger numerator, BigInteger denominator)
    {
        Assert.True(interval.IsResolved);
        Assert.True(denominator > 0);
        Assert.True(interval.Lower <= interval.Upper);
        Assert.True(Units(interval.Lower) * denominator <= numerator * BinaryScale, "lower endpoint excludes the exact rational");
        Assert.True(Units(interval.Upper) * denominator >= numerator * BinaryScale, "upper endpoint excludes the exact rational");
    }

    static void AssertPublicationEnclosesBox(Output output, GeometryVector box)
    {
        Assert.True(output.IsResolved);
        Assert.True(float.IsFinite(output.Error) && output.Error >= 0);
        BigInteger x = FarthestAxis(box.X, output.Value.X), y = FarthestAxis(box.Y, output.Value.Y);
        BigInteger z = FarthestAxis(box.Z, output.Value.Z), error = Units(output.Error);
        // Coordinate maxima can occur together at one corner. Squared comparison avoids a root oracle.
        Assert.True(error * error >= x * x + y * y + z * z, "float Error excludes a point in the supplied box");
    }

    static BigInteger FarthestAxis(GeometryInterval interval, float representative)
    {
        Assert.True(interval.IsResolved && interval.Lower <= interval.Upper);
        BigInteger point = Units(representative);
        return BigInteger.Max(BigInteger.Abs(Units(interval.Lower) - point), BigInteger.Abs(Units(interval.Upper) - point));
    }

    static void AssertSquaredErrorAtLeast(float error, BigInteger numerator, BigInteger denominator)
    {
        Assert.True(float.IsFinite(error) && error >= 0 && denominator > 0);
        BigInteger represented = Units(error);
        Assert.True(represented * represented * denominator >= numerator * BinaryScale * BinaryScale,
            "float Error is below the independent squared-distance bound");
    }

    // Exactly maps any finite binary64 value to integer units of 2^-1074, including signed zero.
    // Normal values have mantissa*(2^(encodedExponent-1075)). No floating arithmetic forms the oracle.
    static BigInteger Units(double value)
    {
        Assert.True(double.IsFinite(value));
        ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        int encoded = (int)((bits >> 52) & 0x7ff);
        BigInteger mantissa = bits & 0x000f_ffff_ffff_ffffUL;
        if (encoded != 0) mantissa = (mantissa + (BigInteger.One << 52)) << (encoded - 1);
        return (bits >> 63) == 0 ? mantissa : -mantissa;
    }

    readonly record struct Output(bool IsResolved, Vector3 Value, float Error);
}
