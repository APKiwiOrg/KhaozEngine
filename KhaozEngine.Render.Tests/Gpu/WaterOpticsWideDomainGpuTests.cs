using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class WaterOpticsTriangleGpuTests
{
    // Dropping word 17 or treating admitted fields as narrow-only must fail through real uploads and sampling.
    [GpuFact]
    public void AdmittedFlatUnitAndHugeFieldsHitClosedEdgesWithWideNormalQueries()
    {
        float tiny = MathF.ScaleB(1, -100);
        var evidence = new List<object>();
        foreach (float size in new[] { 1f, MathF.ScaleB(1, 126) })
        {
            WaterOpticsTriangle[] triangles =
                [new(new(0, 1, 0), new(size, 1, 0), new(size, 1, size)),
                 new(new(0, 1, 0), new(size, 1, size), new(0, 1, size))];
            var field = new WaterOpticsTriangleField(new(size), 1, 1, triangles, [new(Vector2.Zero, new(size))]);
            Assert.InRange(field.ReconstructionErrorBoundMetres, 0, .0001f);
            Assert.Equal(2, field.MaximumBinOccupancy);
            Assert.All(field.AtlasScalars.ToArray(), value => Assert.True(value == 0 || float.IsNormal(value)));
            Assert.True(float.IsNormal(1 / size));
            int edges = (int)field.AtlasScalars[5];
            int vertical = Assert.Single(Enumerable.Range(0, field.EdgeLineCount), edge =>
                field.AtlasScalars[edges + 4 * edge] == size && field.AtlasScalars[edges + 4 * edge + 1] == 0
                && field.AtlasScalars[edges + 4 * edge + 2] == size && field.AtlasScalars[edges + 4 * edge + 3] == size);
            int address = edges + 4 * vertical;
            Vector2[] points = [new(tiny), new(float.BitDecrement(tiny), tiny), new(float.BitIncrement(tiny), tiny),
                new(0, tiny), new(tiny, 0), new(size, tiny), new(float.BitDecrement(size), tiny),
                new(float.BitIncrement(size), tiny), new(size, size), new(0, 0), new(tiny, size)];
            bool[] wide = [true, true, true, true, true, true, true, true, false, false, true];
            string source = Source.Replace("Values[2u*i+1u] = uvec4(opticsMul(a,b), opticsDiv(a,b), 0u, 0u);", @"
    int edge = int(q.w);
    uvec2 lo = uvec2(opticsScalar(edge),opticsScalar(edge+1));
    uvec2 hi = uvec2(opticsScalar(edge+2),opticsScalar(edge+3));
    Values[2u*i+1u] = uvec4(opticsMul(a,b), opticsDiv(a,b),
        uint(opticsOrientBaseShift(lo,hi,floatBitsToUint(q.xy))),uint(opticsOrient(lo,hi,floatBitsToUint(q.xy))));",
                StringComparison.Ordinal);
            Assert.NotEqual(Source, source);
            string name = size == 1 ? "admitted-wide-unit" : "admitted-wide-2pow126";
            uint[] values = Capture(name, field, points.Select(p => new Vector4(p, 1, address)).ToArray(), source);
            for (int i = 0; i < points.Length; i++)
            {
                bool hit = ExactOriginalHit(triangles, points[i]);
                Assert.Equal(i != 7, hit);
                Assert.Equal(hit ? 1u : 0u, values[8 * i]);
                Assert.Equal(hit ? 0x3f800000u : 0u, values[8 * i + 1]);
                Assert.Equal(wide[i], unchecked((int)values[8 * i + 6]) < 0);
                Assert.Equal(points[i].X < size ? 1 : points[i].X == size ? 0 : -1,
                    unchecked((int)values[8 * i + 7]));
            }
            // Physical coordinates reach the production opticalBedDepth directly, with zero origin.
            // For the huge field, tiny/size = 2^-226 cannot be represented as a normalized float query.
            Vector4[] physical = points.Where((_, i) => i != 7).Select(p => new Vector4(p, 4, 999)).ToArray();
            float[] depths = CapturePhysical(0, field, physical, new(0, 0, 1 / size, 1 / size), name + "-physical");
            Assert.All(depths, depth => Assert.Equal(0x40000000u, BitConverter.SingleToUInt32Bits(depth)));
            evidence.Add(new
            {
                Name = name,
                SizeBits = BitConverter.SingleToUInt32Bits(size),
                InverseBits = BitConverter.SingleToUInt32Bits(1 / size),
                field.ReconstructionErrorBoundMetres,
                VerticalEdgeAddress = address,
                QueryBits = points.Select(p => new[] { BitConverter.SingleToUInt32Bits(p.X), BitConverter.SingleToUInt32Bits(p.Y) }),
                ExpectedWide = wide,
                Values = values,
                PhysicalDepthBits = depths.Select(BitConverter.SingleToUInt32Bits)
            });
        }
        const string directory = "/tmp/khaozengine-water-triangles/streamed-wide";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "admitted-domain.json"), JsonSerializer.Serialize(evidence,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // Independent difference-of-coordinates determinant over original binary32 geometry, never normalized UV.
    static bool ExactOriginalHit(WaterOpticsTriangle[] triangles, Vector2 p)
    {
        foreach (WaterOpticsTriangle t in triangles)
        {
            Vector2 a = new(t.A.X, t.A.Z), b = new(t.B.X, t.B.Z), c = new(t.C.X, t.C.Z);
            int winding = Orient(a, b, c);
            if (Orient(a, b, p) * winding >= 0 && Orient(b, c, p) * winding >= 0 && Orient(c, a, p) * winding >= 0)
                return true;
        }
        return false;

        static int Orient(Vector2 a, Vector2 b, Vector2 p)
        {
            BigInteger ax = Integer(a.X), ay = Integer(a.Y), bx = Integer(b.X), by = Integer(b.Y);
            return ((bx - ax) * (Integer(p.Y) - ay) - (by - ay) * (Integer(p.X) - ax)).Sign;
        }
        static BigInteger Integer(float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            int exponent = (int)((bits >> 23) & 255);
            BigInteger magnitude = bits & 0x7fffff;
            if (exponent != 0) magnitude = (magnitude + 0x800000) << (exponent - 1);
            return (bits >> 31) == 0 ? magnitude : -magnitude;
        }
    }
}
