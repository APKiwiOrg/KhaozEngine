using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Task 4's cold production packer must reproduce these words. All table offsets are relative
// to this support's 16-word header, so an owned upload can be relocated without rewriting it.
internal static class CoverageGpuTestSupportPacking
{
    internal static uint Append(List<uint> words, CoverageReferenceSupport support)
    {
        int start = words.Count;
        words.AddRange(new uint[16]);
        words[start] = Bits(support.MaxSlope);
        words[start + 1] = Bits(support.GridMin.X); words[start + 2] = Bits(support.GridMin.Y);
        words[start + 3] = Bits(support.GridMax.X); words[start + 4] = Bits(support.GridMax.Y);
        words[start + 5] = (uint)support.TriangleCount; words[start + 6] = (uint)support.BoundaryCount;
        Offset(7);
        foreach (var p in support.Positions) { words.Add(Bits(p.X)); words.Add(Bits(p.Y)); words.Add(Bits(p.Z)); }
        Offset(8); Write(support.SourceIndices);
        Offset(9);
        foreach (var t in support.Triangles) Write([t.Mesh, t.Primitive, t.Triangle, t.Node, t.MaterialSlice, t.A, t.B, t.C]);
        Offset(10); Write(support.BoundarySourceVertices);
        Offset(11); Write(support.CellOffsets);
        Offset(12); Write(support.CellTriangles);
        return (uint)start;

        void Offset(int header) => words[start + header] = checked((uint)(words.Count - start));
        void Write(int[] values) { foreach (int value in values) words.Add(unchecked((uint)value)); }
    }

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
