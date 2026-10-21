using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageGpuSupportInvariantTests
{
    [Fact]
    public void Cold_predicates_preserve_every_probe_supports_reference_signs_and_words()
    {
        CoverageGpuSupportProbe.Data((words, support) =>
        {
            var upload = CoverageGpuUpload.Support(support.Support);
            int cursor = checked((int)upload.CellTriangles + support.CellTriangles.Length);
            Assert.Equal(cursor + 2 * (support.TriangleCount + support.BoundaryCount), upload.Words.Length);
            var f = new CoverageReferenceArithmetic();
            foreach (var t in support.Triangles)
                Equal(f.Edge(support.Positions[t.A], support.Positions[t.B], support.Positions[t.C]));
            for (int edge = 0; edge < support.BoundaryCount; edge++)
            {
                Vector3 a = support.Positions[support.BoundarySourceVertices[edge]];
                Vector3 b = support.Positions[support.BoundarySourceVertices[(edge + 1) % support.BoundaryCount]];
                Equal(f.Dot(a, b, a, b));
            }
            uint offset = checked((uint)words.Count);
            words.AddRange(upload.Words);
            return offset;

            void Equal(CoverageReferenceScalar expected)
            {
                Assert.Equal(unchecked((uint)expected.Sign), upload.Words[cursor++]);
                Assert.Equal(BitConverter.SingleToUInt32Bits(expected.Value), upload.Words[cursor++]);
            }
        });
    }

    [Theory]
    [InlineData(1, 0x00000000u)]
    [InlineData(-1, 0x80000000u)]
    public void Cold_predicates_retain_nonzero_exact_signs_with_flushed_values(int sign, uint value)
    {
        var support = Triangle(1);
        var data = support.ReferenceData;
        float tiny = MathF.ScaleB(1, -64);
        data.Positions[1] = new(tiny, 0, -0f);
        data.Positions[2] = new(0, sign * tiny, -0f);
        var upload = CoverageGpuUpload.Support(support);
        int start = checked((int)upload.CellTriangles + data.CellTriangles.Length);
        Assert.Equal(new uint[] { unchecked((uint)sign), value, 1, 0, 1, 0, 1, 0 }, upload.Words[start..]);
    }

    [Fact]
    public void A_replacement_support_upload_uses_its_own_geometry_and_keeps_the_previous_words()
    {
        var first = CoverageGpuUpload.Support(Triangle(1));
        var frozen = (uint[])first.Words.Clone();
        var replacement = CoverageGpuUpload.Support(Triangle(2));
        int firstArea = first.Words.Length - 8, replacementArea = replacement.Words.Length - 8;
        Assert.Equal(new uint[] { 1, 0x3f800000 }, first.Words[firstArea..(firstArea + 2)]);
        Assert.Equal(new uint[] { 1, 0x40800000 }, replacement.Words[replacementArea..(replacementArea + 2)]);
        Assert.Equal(frozen, first.Words);
    }

    static CoverageSupportSurface Triangle(float size) => CoverageSupportSurface.Compile(
        CoverageCommands.Solid("cold-support-predicates", [Vector3.Zero, new(size, 0, 0), new(0, size, 0)], [0, 1, 2]),
        [new(0, 0, 0, 1)], Matrix4x4.Identity);
}
