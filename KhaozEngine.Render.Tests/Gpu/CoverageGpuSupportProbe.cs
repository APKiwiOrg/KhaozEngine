using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using CoverageReferenceSupport = KhaozEngine.Render3D.CoverageReferenceSupport;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuSupportProbe
{
    internal const string Name = "CoverageSupport";
    internal const int InputWords = 11, OutputWords = 4;
    internal static IEnumerable<ShippedComputeKernel> Kernels()
    {
        for (int op = 0; op < 3; op++) yield return new(Name + new[] { "Height", "Boundary", "Overlay" }[op], Source(op));
    }
    internal static string Source(int operation) => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        layout(std430, set = 0, binding = 2) readonly buffer StaticBuf { uint CovStaticWords[]; };
        """ + CoverageGpuArithmeticGlsl.Source + CoverageGpuExactWordsGlsl.WorkerSource + CoverageGpuRoundingGlsl.Source
        + CoverageGpuPredicatesGlsl.Source + CoverageGpuSupportGlsl.Source + """
        vec3 probePoint(uint offset) { return vec3(uintBitsToFloat(Inputs[offset]),uintBitsToFloat(Inputs[offset+1u]),uintBitsToFloat(Inputs[offset+2u])); }
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u + row * 11u, d = row * 4u;
        """ + $"if (Inputs[s] != {operation}u) return;\n" + """
            float first = 0.0, second = 0.0;
            int sourceTriangle = -1;
            vec3 a = probePoint(s+2u), b = probePoint(s+5u), c = probePoint(s+8u);
        """ + (operation switch
        {
            0 => "bool ok = covHeight(Inputs[s+1u],a.xy,first,sourceTriangle);",
            1 => "bool ok = covBoundary(Inputs[s+1u],a.xy,first,second);",
            _ => "bool ok = covOverlay(Inputs[s+1u],a,b,c,first);",
        }) + """
            Results[d] = uint(ok); Results[d+1u] = floatBitsToUint(first);
            Results[d+2u] = floatBitsToUint(second); Results[d+3u] = uint(sourceTriangle);
        }
        """;

    internal static (uint[] Inputs, uint[] Static, uint[] Expected) Data()
    {
        var inputs = new List<uint> { 0 };
        var data = new List<uint>(new uint[17]);
        var expected = new List<uint>();
        void Add(CoverageReferenceSupport support, uint offset, uint op, Vector3 a, Vector3 b = default, Vector3 c = default)
        {
            inputs[0]++;
            inputs.Add(op); inputs.Add(offset);
            foreach (Vector3 p in new[] { a, b, c })
            { inputs.Add(Bits(p.X)); inputs.Add(Bits(p.Y)); inputs.Add(Bits(p.Z)); }
            var f = new CoverageReferenceArithmetic();
            float first = 0, second = 0;
            int source = -1;
            bool ok = op == 0 ? support.TryHeight(f, new(a.X, a.Y), out first, out source)
                : op == 1 ? support.TryBoundary(f, new(a.X, a.Y), out first, out second)
                : support.TryOverlayMaximum(f, [a, b, c], out first);
            expected.Add(ok ? 1u : 0u); expected.Add(Bits(first)); expected.Add(Bits(second)); expected.Add(unchecked((uint)source));
        }
        CoverageReferenceSupport square = Grid([-1, 1]);
        uint squareOffset = CoverageGpuTestSupportPacking.Append(data, square);
        // Literal first-hit and boundary-tie rows precede the larger reference corpus.
        Add(square, squareOffset, 0, default);
        Add(square, squareOffset, 1, default);
        foreach (var support in new[] { square, Grid([0, 1]), Grid([-1, -MathF.ScaleB(1, -25), 1]), Grid([-2, -1, 0, 1, 2]), Rounded(2), Rounded(3) })
        {
            uint offset = ReferenceEquals(support, square) ? squareOffset : CoverageGpuTestSupportPacking.Append(data, support);
            for (int y = 0; y <= 16; y++)
                for (int x = 0; x <= 16; x++)
                    Add(support, offset, 0, new(support.GridMin.X + x / 16f * (support.GridMax.X - support.GridMin.X),
                        support.GridMin.Y + y / 16f * (support.GridMax.Y - support.GridMin.Y), 0));
            foreach (Vector3 p in support.Positions)
                foreach (float x in new[] { MathF.BitDecrement(p.X), p.X, MathF.BitIncrement(p.X) })
                    foreach (float y in new[] { MathF.BitDecrement(p.Y), p.Y, MathF.BitIncrement(p.Y) }) Add(support, offset, 0, new(x, y, 0));
            foreach (Vector3 p in new Vector3[] { new(1, -MathF.ScaleB(1, -24) / (1 - MathF.ScaleB(1, -24)), 0),
                new(-float.Epsilon, 0, 0), new(-0f, 0, 0), new(2, 0, 0), new(float.NaN, 0, 0), new(0, float.PositiveInfinity, 0), new(float.MaxValue, float.MaxValue, 0) })
            { Add(support, offset, 0, p); Add(support, offset, 1, p); }
        }
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        var regression = Triangle(sheet);
        uint reg = CoverageGpuTestSupportPacking.Append(data, regression);
        foreach (float x in new[] { 0.10000000149011612f, 0.20000001788139343f })
            Add(regression, reg, 2, new(-0.050000011920928955f, -0.04999999701976776f, 0),
                new(x, x < .15f ? 0.10000000894069672f : 0.20000000298023224f, 0), new(-.2f, .2f, 0));
        foreach (var (host, frozenSheet) in CoverageReferencePredicateTests.FrozenGroup224Fixtures())
        {
            var frozen = Triangle(frozenSheet);
            uint offset = CoverageGpuTestSupportPacking.Append(data, frozen);
            Add(frozen, offset, 2, host[0], host[1], host[2]);
        }
        var crossing = Triangle([new(0, 1.25f, 0), new(2, 1.25f, .1f), new(1, -.75f, .05f)]);
        uint cross = CoverageGpuTestSupportPacking.Append(data, crossing);
        Add(crossing, cross, 2, new(0, 0, .2f), new(2, 0, .3f), new(1, 2, .1f));
        Add(crossing, cross, 2, new(0, 0, .2f), new(1, 2, .1f), new(2, 0, .3f));
        var flat = Triangle([new(0, 0, .125f), new(2, 0, .125f), new(0, 2, .125f)]);
        uint flatOffset = CoverageGpuTestSupportPacking.Append(data, flat);
        foreach (Vector3[] host in new Vector3[][] {
            [new(0, 0, .25f), new(1, 0, .5f), new(0, 1, .125f)],
            [new(1, 0, .25f), new(3, 0, .75f), new(2, -1, 0)],
            [new(3, 3, 0), new(4, 3, 0), new(3, 4, 0)],
            [default, Vector3.One, Vector3.One * 2],
            [new(0, 0, float.NaN), new(1, 0, 0), new(0, 1, 0)],
            [new(-float.MaxValue, 0, 0), new(float.MaxValue, 0, 0), new(0, 1, 0)] })
            Add(flat, flatOffset, 2, host[0], host[1], host[2]);
        foreach (Vector3[] points in new Vector3[][] {
            [new(0, 0, .125f), new(2, 0, .75f), new(0, 2, .25f)],
            [new(0, 2, .25f), new(2, 0, .75f), new(0, 0, .125f)] })
        {
            var slope = Triangle(points);
            uint slopeOffset = CoverageGpuTestSupportPacking.Append(data, slope);
            foreach (Vector3 p in new Vector3[] { points[0], points[1], points[2], new(0, .5f, 0), new(.5f, 0, 0), new(1, 1, 0), new(.5f, .75f, 0) })
            { Add(slope, slopeOffset, 0, p); Add(slope, slopeOffset, 1, p); }
        }
        // A query must use the uploaded cell list, rather than silently substituting a full scan.
        var emptyCell = Grid([-1, 1]);
        Array.Clear(emptyCell.CellOffsets);
        uint emptyOffset = CoverageGpuTestSupportPacking.Append(data, emptyCell);
        Add(emptyCell, emptyOffset, 0, default);
        // A bad first candidate aborts. It must not be skipped to reach a later valid candidate.
        var invalid = Grid([-1, 1]);
        invalid.Positions[1] = invalid.Positions[0];
        uint invalidOffset = CoverageGpuTestSupportPacking.Append(data, invalid);
        Add(invalid, invalidOffset, 0, default);
        Add(invalid, invalidOffset, 1, default);
        Add(invalid, invalidOffset, 2, new(-.5f, -.5f, 1), new(.5f, -.5f, 1), new(0, .5f, 1));
        return (inputs.ToArray(), data.ToArray(), expected.ToArray());
    }

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    static CoverageReferenceSupport Triangle(Vector3[] points) => CoverageSupportSurface.Compile(
        CoverageCommands.Solid("gpu-support-triangle", points, [0, 1, 2]), [new(0, 0, 0, 1)], Matrix4x4.Identity).ReferenceData;
    static CoverageReferenceSupport Grid(float[] xy)
    {
        int width = xy.Length;
        Vector3[] p = Enumerable.Range(0, width * width).Select(i => new Vector3(xy[i % width], xy[i / width], xy[i % width])).ToArray();
        uint[] indices = Enumerable.Range(0, (width - 1) * (width - 1)).SelectMany(i =>
        {
            uint a = (uint)(i / (width - 1) * width + i % (width - 1)), step = (uint)width;
            return new[] { a, a + 1, a + step + 1, a, a + step + 1, a + step };
        }).ToArray();
        return CoverageSupportSurface.Compile(CoverageCommands.Solid("gpu-support-grid", p, indices), [new(0, 0, 0, indices.Length / 3)], Matrix4x4.Identity).ReferenceData;
    }
    static CoverageReferenceSupport Rounded(float right) => CoverageSupportSurface.Compile(CoverageCommands.Solid("gpu-support-rounded",
        [new(0, 1 - MathF.ScaleB(1, -13), 0), new(1 - MathF.ScaleB(1, -24), 0, 0), new(0, 1, 0), new(right, -MathF.ScaleB(1, -15), 0)],
        [0, 1, 2, 1, 3, 2]), [new(0, 0, 0, 2)], Matrix4x4.Identity).ReferenceData;
}
