using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// An exact vertical face must retain its zero upward derivative. A positive isotropic
// normal error cannot establish that sign, even when it is far below the normal ceiling.
public class GeometryExactNormalTests
{
    [Theory]
    [InlineData(2, 0, 0, 1, 0, 0)]
    [InlineData(-8, 0, 0, -1, 0, 0)]
    [InlineData(0, 0.5, 0, 0, 1, 0)]
    [InlineData(0, -4, 0, 0, -1, 0)]
    [InlineData(0, 0, 16, 0, 0, 1)]
    [InlineData(0, 0, -0.25, 0, 0, -1)]
    public void AProvedAxisNormalPublishesItsExactDirection(double x, double y, double z,
        float nx, float ny, float nz)
    {
        GeometryVector unit = GeometryVectorOperations.Normalize(Point(x, y, z));
        GeometryVectorOutput output = GeometryVectorOperations.Publish(unit);

        Assert.True(unit.IsResolved);
        Assert.True(output.IsResolved);
        Assert.Equal(new Vector3(nx, ny, nz), output.Value);
        Assert.Equal(0f, output.Error);
        Assert.Equal((double)nx, unit.X.Lower);
        Assert.Equal((double)nx, unit.X.Upper);
        Assert.Equal((double)ny, unit.Y.Lower);
        Assert.Equal((double)ny, unit.Y.Upper);
        Assert.Equal((double)nz, unit.Z.Lower);
        Assert.Equal((double)nz, unit.Z.Upper);
    }

    [Fact]
    public void ARoundedUnitLengthProposalDoesNotEraseANearAxisDirectionError()
    {
        const double y = 1d / 1073741824;
        // The true squared norm is (2^60+1)/2^60. Its numerator lies strictly between
        // consecutive integer squares, so this direction has no exact dyadic unit vector.
        BigInteger lower = BigInteger.One << 30;
        BigInteger numerator = lower * lower + 1;
        Assert.True(numerator > lower * lower && numerator < (lower + 1) * (lower + 1));
        GeometryVector unit = GeometryVectorOperations.Normalize(Point(1, y, 0));
        GeometryVectorOutput output = GeometryVectorOperations.Publish(unit);

        Assert.True(unit.IsResolved && output.IsResolved);
        Assert.True(unit.X.Lower < 1);
        Assert.True(output.Error > 0);
    }

    [Fact]
    public void AnIntervalNormalCannotReceiveAnExactPointCertificate()
    {
        var source = new GeometryVector(GeometryInterval.Exact(1),
            GeometryInterval.Enclose(-0.125, 0.125), GeometryInterval.Exact(0));
        GeometryVector unit = GeometryVectorOperations.Normalize(source);
        GeometryVectorOutput output = GeometryVectorOperations.Publish(unit);

        Assert.True(unit.IsResolved && output.IsResolved);
        Assert.True(unit.Y.Lower < 0 && unit.Y.Upper > 0);
        Assert.True(output.Error > 0);
    }

    static GeometryVector Point(double x, double y, double z) =>
        new(GeometryInterval.Exact(x), GeometryInterval.Exact(y), GeometryInterval.Exact(z));
}
