using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>A source vertex alias whose posed position must equal its representative exactly.</summary>
public readonly record struct CoverageSourceAlias(int SourceVertex, int Representative);

/// <summary>Ordered split on the edge from SurfaceA to SurfaceB, where SurfaceA is smaller.</summary>
public readonly record struct CoverageEdgeSplit(int SurfaceA, int SurfaceB, int Numerator, int Denominator);

/// <summary>Integer barycentrics over Denominator. Original vertices also retain their exact source index.
/// PositionGroup joins welded geometry. ShadingGroup joins matching authored normals across material and UV seams.
/// Tangent neighborhoods remain indexed render vertices, independent of normal groups and tangent handedness.
/// RegionIndex -1 or Strength zero means protected. A generated vertex on an edge shared within one slice records its
/// first owner as SourceTriangle, which need not be the source of every incident child triangle.</summary>
public readonly record struct CoverageRefinedVertex(int SourceTriangle, int SourceVertex, int A, int B, int C,
    int Denominator, int PositionGroup, int ShadingGroup, int RegionIndex, float Strength);

public readonly record struct CoverageRefinedTriangle(int SourceTriangle, int RegionIndex, CoverageTriangleRole Role);

/// <summary>Contiguous vertex and uint index ranges for one original material slice. Indices remain host-global.</summary>
public readonly record struct CoverageRefinedMaterialRange(int MaterialSlice, int FirstVertex, int VertexCount, int FirstIndex, int IndexCount);

/// <summary>Immutable, fixed, conforming render topology. Compile once, then skin original vertices before interpolation.</summary>
public sealed class CoverageRefinement
{
    const int Denominator = 12; // Common denominator for every permitted edge subdivision count.
    readonly CoverageSourceTopology source;
    readonly uint[] indices;
    readonly PositionRecipe[] positionRecipes;
    readonly int[] positionRepresentatives;
    readonly bool[] coreVertices;

    CoverageRefinement(CoverageSourceTopology source, HostCoverageMap map, uint[] indices, CoverageRefinedVertex[] vertices,
        CoverageRefinedTriangle[] triangles, CoverageRefinedMaterialRange[] materials,
        CoverageEdgeSplit[] splits, CoverageSourceAlias[] aliases, PositionRecipe[] recipes)
    {
        this.source = source; Map = map; this.indices = indices; positionRecipes = recipes;
        coreVertices = new bool[vertices.Length];
        for (int t = 0; t < triangles.Length; t++)
            if (triangles[t].Role == CoverageTriangleRole.Core && triangles[t].RegionIndex >= 0)
                for (int corner = 0; corner < 3; corner++) coreVertices[indices[t * 3 + corner]] = true;
        positionRepresentatives = Enumerable.Repeat(-1, recipes.Length).ToArray();
        for (int i = 0; i < vertices.Length; i++)
            if (positionRepresentatives[vertices[i].PositionGroup] == -1) positionRepresentatives[vertices[i].PositionGroup] = i;
        Vertices = Array.AsReadOnly(vertices); Triangles = Array.AsReadOnly(triangles);
        ShadingGroupCount = vertices.Max(v => v.ShadingGroup) + 1;
        MaterialRanges = Array.AsReadOnly(materials); EdgeSplits = Array.AsReadOnly(splits); SourceAliases = Array.AsReadOnly(aliases);
    }

    /// <summary>The validated immutable source this topology was compiled from.</summary>
    public CoverageSourceTopology Source => source;
    /// <summary>The immutable host map this topology was compiled from. Region indices index Map.Regions.</summary>
    public HostCoverageMap Map { get; }
    public string HostIdentity => source.Identity;
    public int SourceVertexCount => source.Vertices.Count;
    public int VertexCount => Vertices.Count;
    public int TriangleCount => Triangles.Count;
    public int PositionGroupCount => positionRecipes.Length;
    public int ShadingGroupCount { get; }
    public ReadOnlySpan<uint> Indices => indices;
    public IReadOnlyList<CoverageRefinedVertex> Vertices { get; }
    public IReadOnlyList<CoverageRefinedTriangle> Triangles { get; }
    public IReadOnlyList<CoverageRefinedMaterialRange> MaterialRanges { get; }
    public IReadOnlyList<CoverageEdgeSplit> EdgeSplits { get; }
    public IReadOnlyList<CoverageSourceAlias> SourceAliases { get; }

    /// <summary>Validates the actual programmatic source, compiles shared rational edge splits and checks both final
    /// totals before allocating final render arrays. Exceeding a cap throws InvalidDataException with asset identity.</summary>
    public static CoverageRefinement Build(CoverageSourceTopology source, HostCoverageMap map)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(map);
        Check(source.Vertices.Count, HostCoverageMap.MaxSurfaceVertices, "vertices");
        Check(source.Triangles.Count, HostCoverageMap.MaxTriangles, "triangles");
        HostCoveragePart.Validate(map, source);
        var authored = map.Topology.ToDictionary(v => (v.Mesh, v.Primitive, v.Vertex), v => v.SurfaceId);
        var surfaces = source.Vertices.Select(v => authored[(v.Mesh, v.Primitive, v.Vertex)]).ToArray();
        var representatives = new Dictionary<int, int>();
        var aliases = new List<CoverageSourceAlias>();
        for (int i = 0; i < surfaces.Length; i++)
            if (representatives.TryGetValue(surfaces[i], out int other)) aliases.Add(new(i, other));
            else representatives.Add(surfaces[i], i);
        var levels = Enumerable.Repeat(1, source.Triangles.Count).ToArray();
        foreach (var refinement in map.Refinement)
            foreach (int i in source.Select([refinement.Triangles])) levels[i] = refinement.Level;
        var edgeSplits = new Dictionary<(int A, int B), SortedSet<int>>();
        for (int i = 0; i < levels.Length; i++)
        {
            var t = source.Triangles[i];
            AddEdge(t.A, t.B, levels[i]); AddEdge(t.B, t.C, levels[i]); AddEdge(t.C, t.A, levels[i]);
        }
        var regionIndices = Enumerable.Repeat(-1, source.Triangles.Count).ToArray();
        var roles = new CoverageTriangleRole[source.Triangles.Count];
        var strengths = new Dictionary<int, (int Region, float Strength)>();
        var sourceOwners = Enumerable.Repeat(-1, source.Vertices.Count).ToArray();
        for (int i = 0; i < source.Triangles.Count; i++)
        {
            var t = source.Triangles[i];
            if (sourceOwners[t.A] < 0) sourceOwners[t.A] = i;
            if (sourceOwners[t.B] < 0) sourceOwners[t.B] = i;
            if (sourceOwners[t.C] < 0) sourceOwners[t.C] = i;
        }
        for (int r = 0; r < map.Regions.Count; r++)
        {
            foreach (var entry in map.Regions[r].Triangles)
                foreach (int i in source.Select([entry.Triangles])) { regionIndices[i] = r; roles[i] = entry.Role; }
            foreach (var s in map.Regions[r].Strengths)
                if (s.Strength > 0) strengths.Add(s.SurfaceId, (r, s.Strength));
        }
        // Cold compilation uses bounded plans. Final vertex/index arrays are allocated only after both counts pass.
        var vertexPlan = new List<CoverageRefinedVertex>();
        var trianglePlan = new List<CoverageRefinedTriangle>();
        var indexPlan = new List<uint>();
        var materialPlan = new List<CoverageRefinedMaterialRange>();
        var renderKeys = new Dictionary<VertexKey, int>();
        var positionKeys = new Dictionary<VertexKey, int>();
        var shadingKeys = new Dictionary<(int Position, Vector3 Normal), int>();
        var recipes = new List<PositionRecipe>();
        foreach (int slice in source.Vertices.Select(v => v.MaterialSlice).Distinct().Order())
        {
            int firstVertex = vertexPlan.Count, firstIndex = indexPlan.Count;
            for (int i = 0; i < source.Vertices.Count; i++)
                if (source.Vertices[i].MaterialSlice == slice) Original(i);
            for (int i = 0; i < source.Triangles.Count; i++)
            {
                var t = source.Triangles[i];
                if (t.MaterialSlice != slice) continue;
                var children = Subdivide(levels[i]);
                SplitBoundary(t.A, t.B, new(Denominator, 0, 0), new(0, Denominator, 0));
                SplitBoundary(t.B, t.C, new(0, Denominator, 0), new(0, 0, Denominator));
                SplitBoundary(t.C, t.A, new(0, 0, Denominator), new(Denominator, 0, 0));
                foreach (var child in children)
                {
                    Check(trianglePlan.Count + 1, HostCoverageMap.MaxTriangles, "triangles");
                    indexPlan.Add((uint)Vertex(child.A)); indexPlan.Add((uint)Vertex(child.B)); indexPlan.Add((uint)Vertex(child.C));
                    trianglePlan.Add(new(i, regionIndices[i], roles[i]));
                }
                void SplitBoundary(int a, int b, Bary start, Bary end)
                {
                    int sa = surfaces[a], sb = surfaces[b];
                    foreach (int split in edgeSplits[(Math.Min(sa, sb), Math.Max(sa, sb))])
                    {
                        int k = sa < sb ? split : Denominator - split;
                        Bary point = new((start.A * (Denominator - k) + end.A * k) / Denominator,
                            (start.B * (Denominator - k) + end.B * k) / Denominator,
                            (start.C * (Denominator - k) + end.C * k) / Denominator);
                        for (int j = 0; j < children.Count; j++)
                        {
                            var face = children[j];
                            if (TrySplit(face.A, face.B, face.C) || TrySplit(face.B, face.C, face.A) || TrySplit(face.C, face.A, face.B)) break;
                            bool TrySplit(Bary x, Bary y, Bary opposite)
                            {
                                if (!StrictlyBetween(point, x, y)) return false;
                                children[j] = new(x, point, opposite); children.Add(new(point, y, opposite));
                                return true;
                            }
                        }
                    }
                }
                int Vertex(Bary b)
                {
                    if (b.A == Denominator) return Original(t.A);
                    if (b.B == Denominator) return Original(t.B);
                    if (b.C == Denominator) return Original(t.C);
                    var key = Key(b, t.A, t.B, t.C, i);
                    if (renderKeys.TryGetValue(key, out int existing)) return existing;
                    int pa = representatives[surfaces[t.A]], pb = representatives[surfaces[t.B]], pc = representatives[surfaces[t.C]];
                    var positionKey = Key(b, pa, pb, pc, i);
                    int group = Position(positionKey, new(pa, pb, pc, b));
                    float strength = Strength(t.A) * b.A / Denominator + Strength(t.B) * b.B / Denominator + Strength(t.C) * b.C / Denominator;
                    int index = vertexPlan.Count;
                    Check(index + 1, HostCoverageMap.MaxSurfaceVertices, "vertices");
                    Vector3 na = source.SkinVertices[t.A].Normal, nb = source.SkinVertices[t.B].Normal, nc = source.SkinVertices[t.C].Normal;
                    // Double sums give seam aliases the same authored field despite reversed edge order.
                    var authoredNormal = new Vector3((float)(((double)na.X * b.A + (double)nb.X * b.B + (double)nc.X * b.C) / Denominator),
                        (float)(((double)na.Y * b.A + (double)nb.Y * b.B + (double)nc.Y * b.C) / Denominator),
                        (float)(((double)na.Z * b.A + (double)nb.Z * b.B + (double)nc.Z * b.C) / Denominator));
                    vertexPlan.Add(new(i, -1, b.A, b.B, b.C, Denominator, group, Shading(group, authoredNormal), strength == 0 ? -1 : regionIndices[i], strength));
                    renderKeys.Add(key, index);
                    return index;
                }
            }
            materialPlan.Add(new(slice, firstVertex, vertexPlan.Count - firstVertex, firstIndex, indexPlan.Count - firstIndex));
        }
        var splits = edgeSplits.OrderBy(p => p.Key).SelectMany(p => p.Value.Select(k =>
        {
            int gcd = Gcd(k, Denominator);
            return new CoverageEdgeSplit(p.Key.A, p.Key.B, k / gcd, Denominator / gcd);
        })).ToArray();
        return new(source, map, indexPlan.ToArray(), vertexPlan.ToArray(), trianglePlan.ToArray(), materialPlan.ToArray(), splits, aliases.ToArray(), recipes.ToArray());

        float Strength(int v) => strengths.TryGetValue(surfaces[v], out var s) ? s.Strength : 0;
        int Original(int v)
        {
            var key = new VertexKey(0, v, 0, 0);
            if (renderKeys.TryGetValue(key, out int existing)) return existing;
            int representative = representatives[surfaces[v]];
            int group = Position(new(0, representative, 0, 0), new(representative, representative, representative, new(Denominator, 0, 0)));
            int index = vertexPlan.Count;
            Check(index + 1, HostCoverageMap.MaxSurfaceVertices, "vertices");
            strengths.TryGetValue(surfaces[v], out var strength);
            int owner = sourceOwners[v];
            int corner = owner < 0 || source.Triangles[owner].A == v ? 0 : source.Triangles[owner].B == v ? 1 : 2;
            vertexPlan.Add(new(owner, v, corner == 0 ? Denominator : 0, corner == 1 ? Denominator : 0, corner == 2 ? Denominator : 0,
                Denominator, group, Shading(group, source.SkinVertices[v].Normal), strength.Strength == 0 ? -1 : strength.Region, strength.Strength));
            renderKeys.Add(key, index); return index;
        }
        int Position(VertexKey key, PositionRecipe recipe)
        {
            if (positionKeys.TryGetValue(key, out int index)) return index;
            index = recipes.Count; recipes.Add(recipe); positionKeys.Add(key, index); return index;
        }
        int Shading(int group, Vector3 normal)
        {
            var key = (group, normal);
            if (shadingKeys.TryGetValue(key, out int index)) return index;
            index = shadingKeys.Count; shadingKeys.Add(key, index); return index;
        }
        void AddEdge(int a, int b, int level)
        {
            var key = (Math.Min(surfaces[a], surfaces[b]), Math.Max(surfaces[a], surfaces[b]));
            if (!edgeSplits.TryGetValue(key, out var set)) edgeSplits.Add(key, set = new());
            for (int k = 1; k < level; k++) set.Add(k * Denominator / level);
        }
        void Check(int count, int limit, string kind)
        {
            if (count > limit) throw source.Error($"Final refinement {kind} {count} exceed capacity {limit}.");
        }
    }

    /// <summary>Checked convenience wrapper. Runtime preparation must use TryInterpolate for a structured refusal.</summary>
    public void Interpolate(ReadOnlySpan<ModelVertex> posedSource, Span<ModelVertex> destination)
    {
        if (!TryInterpolate(posedSource, destination, out var failure))
            throw new InvalidOperationException($"The posed coverage surface was refused: {failure.Code}.");
    }

    /// <summary>Requires exactly SourceVertexCount inputs, exactly VertexCount outputs and disjoint buffers. Callers that own
    /// larger capacity pass the active prefix. Invalid lengths or overlap throw ArgumentException.
    /// Refusals happen before any destination write. A nonfinite input, or generated normal or tangent contributions that
    /// cancel too far to certify a direction, return UnsupportedTransform. An unequal positional alias returns FoldedPatch.
    /// Original source attributes, including protected aliases, copy exactly. Generated attributes are finite. Truly
    /// absent normals and tangents stay zero.</summary>
    public bool TryInterpolate(ReadOnlySpan<ModelVertex> posedSource, Span<ModelVertex> destination, out CoverageFailure failure)
    {
        if (posedSource.Length != SourceVertexCount || destination.Length != VertexCount || posedSource.Overlaps(destination))
            throw new ArgumentException("Coverage interpolation requires exact source and destination lengths and disjoint buffers.");
        for (int i = 0; i < posedSource.Length; i++)
            if (!Finite(posedSource[i])) { failure = Refusal(CoverageFailureCode.UnsupportedTransform); return false; }
        for (int i = 0; i < SourceAliases.Count; i++)
        {
            var alias = SourceAliases[i];
            if (posedSource[alias.SourceVertex].Position != posedSource[alias.Representative].Position)
            {
                failure = Refusal(CoverageFailureCode.FoldedPatch); return false;
            }
        }
        // Allocation-free certification pass. It repeats the generated direction arithmetic so a refusal writes nothing.
        for (int i = 0; i < VertexCount; i++)
            if (Vertices[i].SourceVertex < 0 && !TryFrame(Vertices[i], posedSource, out _, out _))
            {
                failure = Refusal(CoverageFailureCode.UnsupportedTransform); return false;
            }
        for (int i = 0; i < VertexCount; i++)
        {
            var v = Vertices[i];
            if (v.SourceVertex >= 0) { destination[i] = posedSource[v.SourceVertex]; continue; }
            var t = source.Triangles[v.SourceTriangle];
            var a = posedSource[t.A]; var b = posedSource[t.B]; var c = posedSource[t.C];
            _ = TryFrame(v, posedSource, out var normal, out var tangent); // Certified by the pass above.
            int first = positionRepresentatives[v.PositionGroup];
            Vector3 position = first < i ? destination[first].Position : Position(positionRecipes[v.PositionGroup], posedSource);
            destination[i] = new(position, normal, CoverageWeightedAttributes.Value(a.Color, b.Color, c.Color, v),
                CoverageWeightedAttributes.Value(a.Uv, b.Uv, c.Uv, v), tangent);
        }
        failure = default; return true;
    }

    bool TryFrame(in CoverageRefinedVertex v, ReadOnlySpan<ModelVertex> posed, out Vector3 normal, out Vector4 tangent)
    {
        var t = source.Triangles[v.SourceTriangle];
        tangent = default;
        return CoverageWeightedAttributes.TryDirection(posed[t.A].Normal, posed[t.B].Normal, posed[t.C].Normal, v, out normal)
            && CoverageWeightedAttributes.TryTangent(posed[t.A].Tangent, posed[t.B].Tangent, posed[t.C].Tangent, v, out tangent);
    }

    CoverageFailure Refusal(CoverageFailureCode code) => new(code, HostIdentity, 0, 0, 0, 0);

    /// <summary>True for every vertex of an authored core triangle, whose region strength is exactly one.</summary>
    internal bool IsCoreVertex(int vertex) => coreVertices[vertex];

    /// <summary>Rest-pose refined position, used only to shape cold spatial hierarchies.</summary>
    internal Vector3 RestPosition(int vertex)
    {
        var recipe = positionRecipes[Vertices[vertex].PositionGroup];
        var p = source.Positions;
        var b = recipe.Weights;
        return new((float)(((double)p[recipe.A].X * b.A + (double)p[recipe.B].X * b.B + (double)p[recipe.C].X * b.C) / Denominator),
            (float)(((double)p[recipe.A].Y * b.A + (double)p[recipe.B].Y * b.B + (double)p[recipe.C].Y * b.C) / Denominator),
            (float)(((double)p[recipe.A].Z * b.A + (double)p[recipe.B].Z * b.B + (double)p[recipe.C].Z * b.C) / Denominator));
    }
    static Vector3 Position(PositionRecipe recipe, ReadOnlySpan<ModelVertex> posed)
    {
        var b = recipe.Weights;
        if (b.A == Denominator) return posed[recipe.A].Position;
        // Ordered endpoints make every seam share the exact same float operations.
        if (b.C == 0) return Edge(recipe.A, recipe.B, b.B, posed);
        if (b.B == 0) return Edge(recipe.A, recipe.C, b.C, posed);
        if (b.A == 0) return Edge(recipe.B, recipe.C, b.C, posed);
        return new((float)(((double)posed[recipe.A].Position.X * b.A + (double)posed[recipe.B].Position.X * b.B + (double)posed[recipe.C].Position.X * b.C) / Denominator),
            (float)(((double)posed[recipe.A].Position.Y * b.A + (double)posed[recipe.B].Position.Y * b.B + (double)posed[recipe.C].Position.Y * b.C) / Denominator),
            (float)(((double)posed[recipe.A].Position.Z * b.A + (double)posed[recipe.B].Position.Z * b.B + (double)posed[recipe.C].Position.Z * b.C) / Denominator));
        static Vector3 Edge(int a, int c, int k, ReadOnlySpan<ModelVertex> values)
        {
            if (a > c) { (a, c) = (c, a); k = Denominator - k; }
            return Vector3.Lerp(values[a].Position, values[c].Position, (float)k / Denominator);
        }
    }

    internal static bool Finite(ModelVertex v) => Finite(v.Position) && Finite(v.Normal)
        && float.IsFinite(v.Color.X) && float.IsFinite(v.Color.Y) && float.IsFinite(v.Color.Z) && float.IsFinite(v.Color.W)
        && float.IsFinite(v.Uv.X) && float.IsFinite(v.Uv.Y) && float.IsFinite(v.Tangent.X)
        && float.IsFinite(v.Tangent.Y) && float.IsFinite(v.Tangent.Z) && float.IsFinite(v.Tangent.W);
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    static VertexKey Key(Bary b, int a, int c, int d, int triangle)
    {
        if (b.C == 0) return Edge(a, c, b.B);
        if (b.B == 0) return Edge(a, d, b.C);
        if (b.A == 0) return Edge(c, d, b.C);
        return new(2, triangle, b.B, b.C);
        static VertexKey Edge(int x, int y, int k) => x < y ? new(1, x, y, k) : new(1, y, x, Denominator - k);
    }
    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    static bool StrictlyBetween(Bary p, Bary a, Bary b)
    {
        if (p == a || p == b) return false;
        int x = b.B - a.B, y = b.C - a.C, px = p.B - a.B, py = p.C - a.C;
        return x * py == y * px && px * x + py * y > 0 && px * x + py * y < x * x + y * y;
    }
    static List<Face> Subdivide(int level)
    {
        var faces = new List<Face>(level * level);
        int step = Denominator / level;
        Bary At(int x, int y) => new(Denominator - (x + y) * step, x * step, y * step);
        for (int x = 0; x < level; x++)
            for (int y = 0; y < level - x; y++)
            {
                faces.Add(new(At(x, y), At(x + 1, y), At(x, y + 1)));
                if (x + y < level - 1) faces.Add(new(At(x + 1, y), At(x + 1, y + 1), At(x, y + 1)));
            }
        return faces;
    }
    readonly record struct Bary(int A, int B, int C);
    readonly record struct Face(Bary A, Bary B, Bary C);
    readonly record struct VertexKey(int Kind, int A, int B, int C);
    readonly record struct PositionRecipe(int A, int B, int C, Bary Weights);
}
