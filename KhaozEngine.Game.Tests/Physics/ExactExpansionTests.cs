using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// The allocation-free exact paths against the dyadic references they replace. Operands span the expansion domain,
// its edges, subnormals and exponents far enough apart that the dyadic path refuses, and every exact cancellation
// the kernels rely on: products that round exactly, sums that do not, and coplanar or collinear binary32 points.
public class ExactExpansionTests
{
    const int Cases = 4000;

    [Fact]
    public void ProductComparisonsMatchTheDyadicPath()
    {
        var random = new Random(9311);
        for (int i = 0; i < Cases; i++)
        {
            double a = Operand(random), b = Operand(random);
            double product = a * b;
            // The kernels' exactness test, the root endpoint test and an arbitrary comparison.
            Same(BoundedGeometryArithmetic.CompareProductsDyadic(a, b, product, 1),
                BoundedGeometryArithmetic.CompareProducts(a, b, product, 1), a, b, product, 1);
            double root = Math.BitDecrement(Math.Sqrt(Math.Abs(a)));
            Same(BoundedGeometryArithmetic.CompareProductsDyadic(root, root, Math.Abs(a), 1),
                BoundedGeometryArithmetic.CompareProducts(root, root, Math.Abs(a), 1), root, root, Math.Abs(a), 1);
            double c = Operand(random), d = Operand(random);
            Same(BoundedGeometryArithmetic.CompareProductsDyadic(a, b, c, d),
                BoundedGeometryArithmetic.CompareProducts(a, b, c, d), a, b, c, d);
            double quotient = a / (b == 0 ? 1 : b);
            Same(BoundedGeometryArithmetic.CompareProductsDyadic(quotient, b, a, 1),
                BoundedGeometryArithmetic.CompareProducts(quotient, b, a, 1), quotient, b, a, 1);
        }
    }

    [Fact]
    public void SquaredDistanceComparisonsMatchTheDyadicPath()
    {
        var random = new Random(9312);
        for (int i = 0; i < Cases; i++)
        {
            double a = Operand(random), b = Operand(random), sum = a + b;
            // FeatureNumber.Add's certificate of an exact sum.
            Assert.Equal(BoundedGeometryArithmetic.CompareSquaredDistancesDyadic([sum], [a], [b], [0d]),
                BoundedGeometryArithmetic.CompareSquaredDistances([sum], [a], [b], [0d]));
            int n = 1 + random.Next(3);
            var x = new double[n];
            var y = new double[n];
            var u = new double[n];
            var v = new double[n];
            for (int k = 0; k < n; k++)
            {
                x[k] = Operand(random);
                y[k] = Operand(random);
                // Half the rows reuse the first distance's coordinates, so the distances tie exactly.
                bool tie = random.Next(2) == 0;
                u[k] = tie ? y[k] : Operand(random);
                v[k] = tie ? x[k] : Operand(random);
            }
            Assert.Equal(BoundedGeometryArithmetic.CompareSquaredDistancesDyadic(x, y, u, v),
                BoundedGeometryArithmetic.CompareSquaredDistances(x, y, u, v));
        }
        Assert.Equal(GeometrySign.Unresolved, BoundedGeometryArithmetic.CompareSquaredDistances([1, 2], [1], [1], [1]));
        Assert.Equal(GeometrySign.Unresolved,
            BoundedGeometryArithmetic.CompareSquaredDistances([double.NaN], [1], [1], [1]));
    }

    [Fact]
    public void SumsOfFourSquaresMatchTheDyadicPath()
    {
        var random = new Random(9313);
        for (int i = 0; i < Cases; i++)
        {
            float x = Signed(random), y = Signed(random), z = Signed(random), w = Signed(random);
            double norm = (double)x * x + (double)y * y + (double)z * z + (double)w * w;
            foreach (double expected in new[] { norm, Math.BitIncrement(norm), 1 - 1d / (1 << 20), Operand(random) })
                Assert.Equal(BoundedGeometryArithmetic.CompareSumOfFourSquaresDyadic(x, y, z, w, expected),
                    BoundedGeometryArithmetic.CompareSumOfFourSquares(x, y, z, w, expected));
        }
    }

    [Fact]
    public void OrientationsMatchTheDyadicPath()
    {
        var random = new Random(9314);
        for (int i = 0; i < Cases; i++)
        {
            Vector3 a = Point(random), b = Point(random), c = Point(random), d = Point(random);
            // A point on the plane of a, b and c with small integer weights, exactly when the floats allow it.
            Vector3 planar = a + (b - a) * random.Next(-2, 3) + (c - a) * random.Next(-2, 3);
            Assert.Equal(BoundedGeometryArithmetic.Orient3DDyadic(a, b, c, d), BoundedGeometryArithmetic.Orient3D(a, b, c, d));
            Assert.Equal(BoundedGeometryArithmetic.Orient3DDyadic(a, b, c, planar),
                BoundedGeometryArithmetic.Orient3D(a, b, c, planar));
            Assert.Equal(BoundedGeometryArithmetic.Orient2DDyadic(a.X, a.Y, b.X, b.Y, d.X, d.Y),
                BoundedGeometryArithmetic.Orient2D(a.X, a.Y, b.X, b.Y, d.X, d.Y));
            Assert.Equal(BoundedGeometryArithmetic.Orient2DDyadic(a.X, a.Y, b.X, b.Y, planar.X, planar.Y),
                BoundedGeometryArithmetic.Orient2D(a.X, a.Y, b.X, b.Y, planar.X, planar.Y));
        }
        Assert.Equal(GeometrySign.Zero,
            BoundedGeometryArithmetic.Orient3D(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new Vector3(5, 0, -3)));
        Assert.Equal(GeometrySign.Unresolved,
            BoundedGeometryArithmetic.Orient2D(float.NaN, 0, 1, 0, 0, 1));
    }

    [Fact]
    public void FeatureNumberShortPathsMatchTheDyadicCertificates()
    {
        var random = new Random(9315);
        for (int i = 0; i < Cases; i++)
        {
            FeatureNumber a = FeatureNumber.Exact(Operand(random)), b = FeatureNumber.Exact(Operand(random));
            Same(ReferenceAdd(a, b), a.Add(b));
            Same(ReferenceMultiply(a, b), a.Multiply(b));
            Same(ReferenceDivide(a, b), a.Divide(b));
            // Inexact operands keep the interval path.
            FeatureNumber widened = FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.BitDecrement(a.Value),
                Math.BitIncrement(a.Value)));
            Same(ReferenceAdd(widened, b), widened.Add(b));
            Same(ReferenceMultiply(widened, b), widened.Multiply(b));
        }
    }

    // The FeatureNumber operations as they were before the short paths, certified by the dyadic comparators.
    static FeatureNumber ReferenceAdd(FeatureNumber a, FeatureNumber b)
    {
        GeometryInterval bounds = a.Bounds.Add(b.Bounds);
        if (!bounds.IsResolved || !a.IsExact || !b.IsExact) return FeatureNumber.Enclosed(bounds);
        double sum = a.Value + b.Value;
        GeometrySign magnitude = BoundedGeometryArithmetic.CompareSquaredDistancesDyadic([sum], [a.Value], [b.Value], [0d]);
        bool direction = b.Value < 0 ? sum <= a.Value : sum >= a.Value;
        return magnitude == GeometrySign.Zero && direction ? FeatureNumber.Exact(sum) : FeatureNumber.Enclosed(bounds);
    }

    static FeatureNumber ReferenceMultiply(FeatureNumber a, FeatureNumber b)
    {
        GeometryInterval bounds = a.Bounds.Multiply(b.Bounds);
        if (!bounds.IsResolved || !a.IsExact || !b.IsExact) return FeatureNumber.Enclosed(bounds);
        double product = a.Value * b.Value;
        return BoundedGeometryArithmetic.CompareProductsDyadic(a.Value, b.Value, product, 1) == GeometrySign.Zero
            ? FeatureNumber.Exact(product) : FeatureNumber.Enclosed(bounds);
    }

    static FeatureNumber ReferenceDivide(FeatureNumber a, FeatureNumber b)
    {
        GeometryInterval bounds = a.Bounds.Divide(b.Bounds);
        if (!bounds.IsResolved || !a.IsExact || !b.IsExact || b.Value == 0) return FeatureNumber.Enclosed(bounds);
        double quotient = a.Value / b.Value;
        return BoundedGeometryArithmetic.CompareProductsDyadic(quotient, b.Value, a.Value, 1) == GeometrySign.Zero
            ? FeatureNumber.Exact(quotient) : FeatureNumber.Enclosed(bounds);
    }

    static void Same(FeatureNumber expected, FeatureNumber actual)
    {
        Assert.Equal(expected.IsResolved, actual.IsResolved);
        Assert.Equal(expected.IsExact, actual.IsExact);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Value), BitConverter.DoubleToInt64Bits(actual.Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Bounds.Lower), BitConverter.DoubleToInt64Bits(actual.Bounds.Lower));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expected.Bounds.Upper), BitConverter.DoubleToInt64Bits(actual.Bounds.Upper));
    }

    static void Same(GeometrySign expected, GeometrySign actual, double a, double b, double c, double d) =>
        Assert.True(expected == actual, $"CompareProducts({a:R}, {b:R}, {c:R}, {d:R}): dyadic {expected}, got {actual}.");

    // A binary64 operand: mostly binary32 values and products of them as the kernels see, sometimes a random
    // binary64 at any exponent, a subnormal, a zero, or a value on either edge of the expansion domain.
    static double Operand(Random random)
    {
        double sign = random.Next(2) == 0 ? -1 : 1;
        return random.Next(10) switch
        {
            0 => 0,
            1 => sign * Math.ScaleB(1 + random.NextDouble(), random.Next(-1074, 1000)),
            2 => sign * double.Epsilon * random.Next(1, 1 << 20),
            3 => sign * Math.ScaleB(1 + random.NextDouble(), random.Next(2) == 0 ? -201 : 199),
            4 => sign * Math.ScaleB(1, random.Next(2) == 0 ? -200 : 199),
            5 => (double)Signed(random) * Signed(random),
            _ => Signed(random),
        };
    }

    static float Signed(Random random) =>
        (float)Math.ScaleB(random.NextDouble() * 2 - 1, random.Next(-30, 12));

    // Coordinates on a coarse grid at one of a few scales, so exact coincidences and cancellations are common.
    static Vector3 Point(Random random)
    {
        int scale = random.Next(-24, 8);
        float Coordinate() => random.Next(4) == 0 ? Signed(random) : (float)Math.ScaleB(random.Next(-64, 65), scale);
        return new Vector3(Coordinate(), Coordinate(), Coordinate());
    }
}
