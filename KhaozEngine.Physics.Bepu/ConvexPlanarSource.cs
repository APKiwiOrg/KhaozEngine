using System;
using System.Numerics;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;

namespace KhaozEngine.Physics.Bepu;

/// <summary>An authenticated installed planar source, or an inner simplex retaining original
/// vertex indices. Inner support never authorizes a result for the original target.</summary>
internal readonly struct ConvexPlanarSource
{
    readonly SourceData? _data;
    readonly int[]? _vertices;
    readonly int[][]? _faces;
    internal bool IsOriginal => _data is not null && _vertices is null;
    internal bool IsValid => _data is { Authenticated: true } d && d.Leaf.Geometry.Matches(d.Leaf.Geometry.Owner, d.Leaf.Geometry.Root) &&
        d.Leaf.Geometry.TryPose(d.Leaf.Geometry.ParentPose, out _) && d.Leaf.Geometry.LeafPose.Position == d.Leaf.Pose.Position &&
        d.Leaf.Geometry.LeafPose.Orientation == d.Leaf.Pose.Orientation;
    internal InstalledSweepLeaf Leaf => _data!.Leaf;
    internal object? Token => _vertices ?? (object?)_data;
    internal int VertexCount => _vertices?.Length ?? Leaf.Geometry.VertexCount;
    internal int FaceCount => _faces?.Length ?? (IsOriginal ? Leaf.Geometry.FaceCount : 0);
    internal int EdgeCount => _vertices is null ? Leaf.Geometry.EdgeCount : _vertices.Length * (_vertices.Length - 1) / 2;
    internal int Rank => _vertices is null ? 3 : _vertices.Length - 1;

    sealed class SourceData(InstalledSweepLeaf leaf, bool authenticated)
    { internal readonly InstalledSweepLeaf Leaf = leaf; internal readonly bool Authenticated = authenticated; }
    ConvexPlanarSource(SourceData data, int[]? vertices = null, int[][]? faces = null)
    { _data = data; _vertices = vertices; _faces = faces; }

    internal static bool TryCreate(in InstalledSweepLeaf leaf, out ConvexPlanarSource source)
    {
        source = default;
        if (leaf.Geometry is not { } g || !leaf.Map.IsResolved || g.Curved.HasValue || g.Children is not null ||
            g.Mesh is not null || g.IsTriangle || !g.Matches(g.Owner, g.Root) || !g.TryPose(g.ParentPose, out _) ||
            g.LeafPose.Position != leaf.Pose.Position || g.LeafPose.Orientation != leaf.Pose.Orientation ||
            !InstalledPoseOperator.TryCreateSupport(leaf.Pose, out var map)) return false;
        source = new(new SourceData(new(g, leaf.Pose, map, operational: true), true));
        return true;
    }

    internal static ConvexPlanarSource FromRepresented(in InstalledSweepLeaf leaf) => new(new SourceData(leaf, false));
    internal int OriginalVertex(int index) => _vertices is null ? index : _vertices[index];
    internal P Vertex(int index) => ConvexPlanarFeatures.Vertex(Leaf, OriginalVertex(index), Vector3.Zero);
    internal P Vertex(int index, P feet) => Vertex(index) - feet;
    internal int FaceVertex(int face, int index) => _faces is null ? Leaf.Geometry.FaceVertex(face, index) : _faces[face][index];
    internal int InteriorFaceSize(int face) => _faces is null ? Leaf.Geometry.InteriorFaceSize(face) : 3;
    internal (int A, int B) Edge(int index)
    {
        if (_vertices is null) return Leaf.Geometry.Edge(index);
        for (int a = 0; a < _vertices.Length; a++)
            for (int b = a + 1; b < _vertices.Length; b++)
                if (index-- == 0) return (a, b);
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    internal bool TrySimplex(ReadOnlySpan<int> vertices, out ConvexPlanarSource source)
    {
        source = default;
        if (!IsValid || !IsOriginal || vertices.Length is < 1 or > 4) return false;
        int[] indices = vertices.ToArray();
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] < 0 || indices[i] >= VertexCount) return false;
            for (int j = 0; j < i; j++) if (indices[i] == indices[j]) return false;
        }
        try
        {
            P a = Vertex(indices[0]);
            if (indices.Length > 1 && P.Dot(Vertex(indices[1]) - a, Vertex(indices[1]) - a).Sign == 0) return false;
            P n = indices.Length > 2 ? P.Cross(Vertex(indices[1]) - a, Vertex(indices[2]) - a) : default;
            if (indices.Length > 2 && P.Dot(n, n).Sign == 0) return false;
            if (indices.Length > 3 && P.Dot(n, Vertex(indices[3]) - a).Sign == 0) return false;
            int[][] faces = indices.Length switch
            {
                3 => [[0, 1, 2], [0, 2, 1]],
                4 => [[1, 2, 3], [0, 3, 2], [0, 1, 3], [0, 2, 1]],
                _ => []
            };
            if (indices.Length == 4)
                for (int i = 0; i < 4; i++)
                {
                    int[] f = faces[i];
                    P p = Vertex(indices[f[0]]);
                    if (P.Dot(P.Cross(Vertex(indices[f[1]]) - p, Vertex(indices[f[2]]) - p), Vertex(indices[i]) - p).Sign > 0)
                        (f[1], f[2]) = (f[2], f[1]);
                }
            source = new(_data!, indices, faces);
            return true;
        }
        catch (ConvexExactRefusalException) { return false; }
    }
}
