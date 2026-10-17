using System;
using System.Numerics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class SupportDiscEdgeTests
{
    [Fact]
    public void DiscEdgeExpansionMatchesIndependentIntegerDistances()
    {
        var random = new Random(1334);
        for (int row = 0; row < 512; row++)
        {
            double[] v = new double[10];
            for (int i = 0; i < v.Length; i++) v[i] = Math.ScaleB(random.Next(-1024, 1025), -random.Next(3, 61));
            // Preserve exact edge differences, including non-binary32 offsets and tiny lattice residuals.
            v[4] = Math.ScaleB(random.Next(-1024, 1025), -40);
            v[5] = Math.ScaleB(random.Next(-1024, 1025), -40);
            v[6] = v[4] + Math.ScaleB(random.Next(1, 1025), -10);
            v[7] = v[5] + Math.ScaleB(random.Next(1, 1025), -10);
            GeometrySign actual = Compare(v);
            Assert.NotEqual(GeometrySign.Unresolved, actual);
            Assert.Equal(IntegerDiscEdge(v), actual);
        }
        double reach = 0.25 + (double)0.0001f;
        foreach (double x in new[] { Math.BitDecrement(reach), reach, Math.BitIncrement(reach) })
            foreach (double z in new[] { -1d, 0, 1 })
            {
                double[] v = [x, z, 0, 0, 0, -1, 0, 1, 0.25, 0.0001f];
                Assert.Equal(IntegerDiscEdge(v), Compare(v));
            }
        Assert.Equal(GeometrySign.Unresolved, Compare([double.NaN, 0, 0, 0, 0, 0, 1, 0, 0.25, 0.0001f]));
        Assert.Equal(GeometrySign.Unresolved, Compare([double.Epsilon, 0, 0, 0, 0, 0, 1, 0, 0.25, 0.0001f]));
    }

    static GeometrySign Compare(double[] v) => BoundedGeometryArithmetic.CompareDiscEdge(
        v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9]);

    static GeometrySign IntegerDiscEdge(double[] values)
    {
        BigInteger[] v = Array.ConvertAll(values, x => new BigInteger(Math.ScaleB(x, 80)));
        BigInteger dx = v[0] - v[4], dz = v[1] - v[5], ex = v[6] - v[4], ez = v[7] - v[5];
        BigInteger dot = dx * ex + dz * ez, length = ex * ex + ez * ez;
        BigInteger gap = v[2] - v[3], reach = v[8] + v[9], budget = reach * reach - gap * gap;
        BigInteger difference;
        if (dot <= 0) difference = dx * dx + dz * dz - budget;
        else if (dot >= length)
        {
            dx = v[0] - v[6]; dz = v[1] - v[7];
            difference = dx * dx + dz * dz - budget;
        }
        else
        {
            BigInteger cross = dx * ez - dz * ex;
            difference = cross * cross - budget * length;
        }
        return difference.Sign switch { < 0 => GeometrySign.Negative, > 0 => GeometrySign.Positive, _ => GeometrySign.Zero };
    }
}
