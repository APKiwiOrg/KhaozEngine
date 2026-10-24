using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class WaterOpticsTriangleGpuTests
{
    // Catches operand selection, subtraction sign and intermediate-rounding changes in production.
    // The oracle is the original straight-line CPU sampler, never a copy of the shader schedule.
    [GpuFact]
    public void NonconstantInterpolationMatchesOrderedCpuBitsIncludingSignedZeroAndSubnormalIntermediates()
    {
        float tiny = BitConverter.UInt32BitsToSingle(1);
        Vector2[] points = [new(-0f, 0), new(tiny, 0), new(3 * tiny, 0),
            new(1.17549435E-38f, 0), new(1, 0), new(1, 1), new(2, 2),
            new(float.BitDecrement(1), 1), new(float.BitIncrement(1), 1),
            new(.7f, 1.1f), new(1, .5f), new(float.BitIncrement(2), 1)];
        foreach (bool reverse in new[] { false, true })
        {
            WaterOpticsTriangle[] signed = Square(-0f, 1, -1, 1);
            if (reverse) Array.Reverse(signed);
            uint[] bits = VerifyOrderedBits("ordered-signed-" + reverse, signed, points);
            Assert.Equal(reverse ? 0u : 0x80000000u, bits[1]); // First source triangle settles signed zero on the shared vertex.
            Assert.Equal(0u, bits[9]); // Half a minimum subnormal ties to even zero.
            Assert.Equal(2u, bits[17]); // Three halves round up to the even subnormal.
            Assert.Equal(0x00400000u, bits[25]); // Normal query, subnormal weight and depth.
            Assert.Equal(0u, bits[81]); // Opposite signed depth contributions cancel exactly.
            WaterOpticsTriangle[] affine = Square(-.02f, .98f, 1.98f, .98f);
            if (reverse)
            {
                Array.Reverse(affine);
                for (int i = 0; i < affine.Length; i++)
                    affine[i] = new(affine[i].A, affine[i].C, affine[i].B);
            }
            VerifyOrderedBits("ordered-affine-" + reverse, affine, points);
        }
        VerifyOrderedBits("ordered-equal-zero", Square(-0f, 0, -0f, 0), points);
    }

    uint[] VerifyOrderedBits(string name, WaterOpticsTriangle[] triangles, Vector2[] points,
        WaterOpticsTriangleField? field = null)
    {
        field ??= new WaterOpticsTriangleField(new(2), 3, 5, triangles, [new(Vector2.Zero, new(2))]);
        uint[] actual = Capture(name, field, points.Select(p => new Vector4(p, 1, 3)).ToArray());
        for (int i = 0; i < points.Length; i++)
        {
            bool hit = TryOriginalDepth(triangles, points[i], out float depth);
            Assert.Equal(hit ? 1u : 0u, actual[8 * i]);
            if (hit) AssertBits(depth, actual[8 * i + 1], i, name);
        }
        return actual;
    }

    static bool TryOriginalDepth(WaterOpticsTriangle[] triangles, Vector2 p, out float depth)
    {
        Span<float> packed = stackalloc float[9];
        foreach (WaterOpticsTriangle t in triangles)
        {
            int winding = WaterOpticsTriangleMath.Orient(t.A.X, t.A.Z, t.B.X, t.B.Z, t.C.X, t.C.Z);
            if (WaterOpticsTriangleMath.Orient(t.A.X, t.A.Z, t.B.X, t.B.Z, p.X, p.Y) * winding < 0
                || WaterOpticsTriangleMath.Orient(t.B.X, t.B.Z, t.C.X, t.C.Z, p.X, p.Y) * winding < 0
                || WaterOpticsTriangleMath.Orient(t.C.X, t.C.Z, t.A.X, t.A.Z, p.X, p.Y) * winding < 0)
                continue;
            packed[0] = t.A.X; packed[1] = t.A.Z; packed[2] = t.A.Y;
            packed[3] = t.B.X; packed[4] = t.B.Z; packed[5] = t.B.Y;
            packed[6] = t.C.X; packed[7] = t.C.Z; packed[8] = t.C.Y;
            depth = WaterOpticsTriangleMath.Interpolate(packed, p.X, p.Y);
            return true;
        }
        depth = 0;
        return false;
    }

    static WaterOpticsTriangle[] Square(float a, float b, float c, float d) =>
        [new(new(0, a, 0), new(2, b, 0), new(2, c, 2)),
         new(new(0, a, 0), new(2, c, 2), new(0, d, 2))];
}
