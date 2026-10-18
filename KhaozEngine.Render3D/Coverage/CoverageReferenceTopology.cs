using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D;

internal readonly record struct CoveragePositionRecipe(int A, int B, int C, int WeightA, int WeightB, int WeightC, int Denominator);

internal readonly record struct CoverageReferenceWeights(float A, float B, float C);

/// <summary>Cold canonical recipes and sorted gathers. Arrays are owned here and never changed after compilation.</summary>
internal sealed class CoverageReferenceTopology
{
    internal CoverageReferenceTopology(CoverageRefinement topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        Topology = topology;
        VertexCount = topology.VertexCount;
        TriangleCount = topology.TriangleCount;
        PositionGroupCount = topology.PositionGroupCount;
        SourceVertexCount = topology.SourceVertexCount;
        AliasCount = topology.SourceAliases.Count;
        Vertices = topology.Vertices;
        Triangles = topology.Triangles;
        SourceTriangles = topology.Source.Triangles;
        SourceAliases = topology.SourceAliases;
        Indices = topology.Indices.ToArray();
        GroupWeights = new CoverageReferenceWeights[PositionGroupCount];
        EdgeParameters = new float[PositionGroupCount];
        VertexWeights = new CoverageReferenceWeights[VertexCount];
        Recipes = new CoveragePositionRecipe[topology.PositionGroupCount];
        GroupRepresentatives = new int[Recipes.Length];
        Array.Fill(GroupRepresentatives, -1);
        for (int g = 0; g < Recipes.Length; g++)
        {
            var recipe = Recipes[g] = topology.GetPositionRecipe(g);
            GroupWeights[g] = new(recipe.WeightA, recipe.WeightB, recipe.WeightC);
            int a, b, numerator;
            if (recipe.WeightC == 0) { a = recipe.A; b = recipe.B; numerator = recipe.WeightB; }
            else if (recipe.WeightB == 0) { a = recipe.A; b = recipe.C; numerator = recipe.WeightC; }
            else { a = recipe.B; b = recipe.C; numerator = recipe.WeightC; }
            EdgeParameters[g] = (a > b ? 12 - numerator : numerator) / 12f;
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            VertexWeights[v] = new(record.A, record.B, record.C);
            int g = record.PositionGroup;
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

    internal int VertexCount { get; }
    internal int TriangleCount { get; }
    internal int PositionGroupCount { get; }
    internal int SourceVertexCount { get; }
    internal int AliasCount { get; }
    internal IReadOnlyList<CoverageRefinedVertex> Vertices { get; }
    internal IReadOnlyList<CoverageRefinedTriangle> Triangles { get; }
    internal IReadOnlyList<CoverageSourceTriangle> SourceTriangles { get; }
    internal IReadOnlyList<CoverageSourceAlias> SourceAliases { get; }
    internal uint[] Indices { get; }
    internal CoverageReferenceWeights[] GroupWeights { get; }
    internal CoverageReferenceWeights[] VertexWeights { get; }
    internal float[] EdgeParameters { get; }
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
