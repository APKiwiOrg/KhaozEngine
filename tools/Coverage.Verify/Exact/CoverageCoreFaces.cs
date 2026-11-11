using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Ordered core triangle adjacency used only by offline conformation and correction.</summary>
internal sealed class CoverageCoreFaces
{
    readonly CoverageRefinement topology;

    internal CoverageCoreFaces(CoverageRefinement topology)
    {
        this.topology = topology;
        Offsets = new int[topology.PositionGroupCount + 1];
        for (int t = 0; t < topology.TriangleCount; t++) Visit(t, true, null);
        for (int g = 1; g < Offsets.Length; g++) Offsets[g] += Offsets[g - 1];
        Faces = new int[Offsets[^1]];
        var next = (int[])Offsets.Clone();
        for (int t = 0; t < topology.TriangleCount; t++) Visit(t, false, next);
    }

    internal int[] Offsets { get; }
    internal int[] Faces { get; }

    void Visit(int triangle, bool counting, int[]? next)
    {
        var face = topology.Triangles[triangle];
        if (face.Role != CoverageTriangleRole.Core || face.RegionIndex < 0) return;
        ReadOnlySpan<uint> indices = topology.Indices;
        int a = topology.Vertices[(int)indices[triangle * 3]].PositionGroup,
            b = topology.Vertices[(int)indices[triangle * 3 + 1]].PositionGroup,
            c = topology.Vertices[(int)indices[triangle * 3 + 2]].PositionGroup;
        Add(a);
        if (b != a) Add(b);
        if (c != a && c != b) Add(c);
        void Add(int group)
        {
            if (counting) Offsets[group + 1]++;
            else Faces[next![group]++] = triangle;
        }
    }
}
