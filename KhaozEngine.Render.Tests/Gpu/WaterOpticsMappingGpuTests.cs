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
    // Wrong axis, clamp placement or bank dependencies can select an unrelated cell or miss a hit.
    // Expectations use original geometry and straight-line CPU arithmetic, never the shader schedule.
    [GpuFact]
    public void RectangularBinsAndOriginAxesMatchOriginalBitsAtBoundariesAndSignedZeros()
    {
        var triangles = new List<WaterOpticsTriangle>();
        for (int row = 0; row < 5; row++)
            for (int column = 0; column < 3; column++)
            {
                Vector3 a = Vertex(2 * column, 2 * row), b = Vertex(2 * column + 2, 2 * row);
                Vector3 c = Vertex(2 * column + 2, 2 * row + 2), d = Vertex(2 * column, 2 * row + 2);
                triangles.Add(new(a, b, c));
                triangles.Add(new(a, c, d));
            }
        WaterOpticsTriangle[] sources = triangles.ToArray();
        var field = new WaterOpticsTriangleField(new(6, 10), 3, 5, sources, [new(Vector2.Zero, new(6, 10))]);
        Vector2[] points = [new(-0f, 0), new(0, -0f), new(-0f, -0f), new(.75f, 3.25f), new(4.75f, .25f),
            new(2, 4), new(float.BitDecrement(2), 4), new(float.BitIncrement(2), 4),
            new(2, float.BitDecrement(4)), new(2, float.BitIncrement(4)),
            new(6, 0), new(0, 10), new(6, 10), new(float.BitDecrement(6), 10), new(6, float.BitDecrement(10)),
            new(float.BitIncrement(6), 5), new(3, float.BitIncrement(10)), new(float.BitDecrement(0), 1)];
        uint[] local = VerifyOrderedBits("mapping-rectangular", sources, points, field);
        Assert.Equal(15, Enumerable.Range(0, points.Length).Count(i => local[8 * i] == 1));
        Assert.Equal(0u, local[8 * 15]);
        Assert.Equal(0u, local[8 * 16]);
        Assert.Equal(0u, local[8 * 17]);
        var evidence = new List<object>();
        Vector2[] origins = [new(-16.25f, -32.5f), new(0f, -0f), new(-0f, 0f)];
        for (int capture = 0; capture < origins.Length; capture++)
        {
            Vector2 origin = origins[capture];
            // Preserve zero signs as query inputs. Nonzero origins exercise cancellation in subtraction.
            Vector4[] queries = points.Take(15).Select(p => new Vector4(
                origin.X == 0 ? p.X : origin.X + p.X,
                origin.Y == 0 ? p.Y : origin.Y + p.Y, 8, 999)).ToArray();
            Vector2[] roundedLocal = queries.Select(q => new Vector2(q.X - origin.X, q.Y - origin.Y)).ToArray();
            VerifyOrderedBits("mapping-rounded-origin-" + capture, sources, roundedLocal, field);
            float[] actual = CapturePhysical(0, field, queries, new(origin, 1f / 6, 1f / 10),
                "mapping-physical-" + capture);
            var expected = new uint[queries.Length];
            for (int i = 0; i < queries.Length; i++)
            {
                Assert.True(TryOriginalDepth(sources, roundedLocal[i], out float signedDepth));
                float displaced = signedDepth + queries[i].Z;
                float depth = MathF.Max(0, displaced - 3);
                expected[i] = BitConverter.SingleToUInt32Bits(depth);
                AssertBits(depth, BitConverter.SingleToUInt32Bits(actual[i]), i, "origin physical depth");
            }
            evidence.Add(new
            {
                OriginBits = new[] { BitConverter.SingleToUInt32Bits(origin.X), BitConverter.SingleToUInt32Bits(origin.Y) },
                QueryBits = queries.Select(q => new[] { BitConverter.SingleToUInt32Bits(q.X), BitConverter.SingleToUInt32Bits(q.Y) }),
                LocalBits = roundedLocal.Select(p => new[] { BitConverter.SingleToUInt32Bits(p.X), BitConverter.SingleToUInt32Bits(p.Y) }),
                ExpectedDepthBits = expected,
                ActualDepthBits = actual.Select(BitConverter.SingleToUInt32Bits)
            });
        }
        string directory = "/tmp/khaozengine-water-triangles/shared-predicate-mapping";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mapping-readback.json"), JsonSerializer.Serialize(evidence,
            new JsonSerializerOptions { WriteIndented = true }));
        static Vector3 Vertex(float x, float z) => new(x, -2 + x * .25f + z * .5f, z);
    }
}
