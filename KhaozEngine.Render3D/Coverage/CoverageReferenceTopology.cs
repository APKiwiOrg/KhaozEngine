using System;

namespace KhaozEngine.Render3D;

internal readonly record struct CoveragePositionRecipe(int A, int B, int C, int WeightA, int WeightB, int WeightC, int Denominator);

/// <summary>Cold canonical recipes and sorted gathers. Arrays are owned here and never changed after compilation.</summary>
internal sealed class CoverageReferenceTopology
{
    internal CoverageReferenceTopology(CoverageRefinement topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        Topology = topology;
        Recipes = new CoveragePositionRecipe[topology.PositionGroupCount];
        GroupRepresentatives = new int[Recipes.Length];
        Array.Fill(GroupRepresentatives, -1);
        for (int g = 0; g < Recipes.Length; g++) Recipes[g] = topology.GetPositionRecipe(g);
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup;
            if (GroupRepresentatives[g] < 0) GroupRepresentatives[g] = v;
        }
        CoreFaceOffsets = new int[Recipes.Length + 1];
        VertexFaceOffsets = new int[topology.VertexCount + 1];
        ShadingCornerOffsets = new int[topology.ShadingGroupCount + 1];
        ReadOnlySpan<uint> indices = topology.Indices;
        for (int c = 0; c < indices.Length; c++)
        {
            int v = (int)indices[c];
            VertexFaceOffsets[v + 1]++;
            ShadingCornerOffsets[topology.Vertices[v].ShadingGroup + 1]++;
        }
        for (int t = 0; t < topology.TriangleCount; t++) VisitCore(t, true, null);
        CoreFaces = Allocate(CoreFaceOffsets);
        VertexCorners = Allocate(VertexFaceOffsets);
        ShadingCorners = Allocate(ShadingCornerOffsets);
        int[] coreNext = (int[])CoreFaceOffsets.Clone(), vertexNext = (int[])VertexFaceOffsets.Clone(), shadingNext = (int[])ShadingCornerOffsets.Clone();
        for (int t = 0; t < topology.TriangleCount; t++) VisitCore(t, false, coreNext);
        for (int c = 0; c < indices.Length; c++)
        {
            int v = (int)indices[c];
            VertexCorners[vertexNext[v]++] = c;
            ShadingCorners[shadingNext[topology.Vertices[v].ShadingGroup]++] = c;
        }
    }

    internal CoverageRefinement Topology { get; }
    internal CoveragePositionRecipe[] Recipes { get; }
    internal int[] GroupRepresentatives { get; }
    internal int[] CoreFaceOffsets { get; }
    internal int[] CoreFaces { get; }
    internal int[] VertexFaceOffsets { get; }
    internal int[] VertexCorners { get; }
    internal int[] ShadingCornerOffsets { get; }
    internal int[] ShadingCorners { get; }

    void VisitCore(int triangle, bool counting, int[]? next)
    {
        var face = Topology.Triangles[triangle];
        if (face.RegionIndex < 0 || face.Role != CoverageTriangleRole.Core) return;
        ReadOnlySpan<uint> indices = Topology.Indices;
        int a = Topology.Vertices[(int)indices[triangle * 3]].PositionGroup,
            b = Topology.Vertices[(int)indices[triangle * 3 + 1]].PositionGroup,
            c = Topology.Vertices[(int)indices[triangle * 3 + 2]].PositionGroup;
        Add(a);
        if (b != a) Add(b);
        if (c != a && c != b) Add(c);
        void Add(int group)
        {
            if (counting) CoreFaceOffsets[group + 1]++;
            else CoreFaces[next![group]++] = triangle;
        }
    }

    static int[] Allocate(int[] offsets)
    {
        for (int i = 1; i < offsets.Length; i++) offsets[i] += offsets[i - 1];
        return new int[offsets[^1]];
    }
}
