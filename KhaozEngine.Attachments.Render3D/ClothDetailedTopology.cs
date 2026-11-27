using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Immutable grouping of the moving triangles in the final visible view.
internal sealed class ClothDetailedTopology
{
    private const int LeafCapacity = 8;
    private readonly ClothTriangle[] _faces;
    private readonly Node[] _nodes;

    internal ClothDetailedTopology(BoundClothMaterial view)
    {
        var faces = new List<Face>();
        ReadOnlySpan<uint> indices = view.VisibleIndices.AsSpan();
        ReadOnlySpan<SkinnedVertex> vertices = view.Vertices.AsSpan();
        for (int i = 0; i < indices.Length; i += 3)
        {
            var face = new ClothTriangle((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
            if (view.Moves(face.A) || view.Moves(face.B) || view.Moves(face.C))
            {
                Vector3 center = (vertices[face.A].Position + vertices[face.B].Position + vertices[face.C].Position) / 3;
                faces.Add(new(face, center, i));
            }
        }

        Face[] ordered = faces.ToArray();
        var nodes = new List<Node>();
        if (ordered.Length > 0) Build(0, ordered.Length);
        _nodes = nodes.ToArray();
        _faces = Array.ConvertAll(ordered, static face => face.Triangle);

        void Build(int first, int count)
        {
            int node = nodes.Count;
            nodes.Add(default);
            if (count <= LeafCapacity)
            {
                nodes[node] = new(first, count, node + 1);
                return;
            }

            Vector3 min = ordered[first].Center, max = min;
            for (int i = first + 1; i < first + count; i++)
            {
                min = Vector3.Min(min, ordered[i].Center);
                max = Vector3.Max(max, ordered[i].Center);
            }
            Vector3 extent = max - min;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            Array.Sort(ordered, first, count, Comparer<Face>.Create((a, b) =>
            {
                int order = a.Center[axis].CompareTo(b.Center[axis]);
                return order == 0 ? a.Ordinal.CompareTo(b.Ordinal) : order;
            }));
            int leftCount = count / 2;
            Build(first, leftCount);
            Build(first + leftCount, count - leftCount);
            nodes[node] = new(0, 0, nodes.Count);
        }
    }

    internal ReadOnlySpan<ClothTriangle> Faces => _faces;
    internal ReadOnlySpan<Node> Nodes => _nodes;

    // Splitting a full leaf produces at least half a leaf on either side.
    internal static int NodeCapacity(int triangles) => 2 * Math.Max(1, triangles / (LeafCapacity / 2));

    internal void Refit(Vector3[] before, Vector3[] after, Span<ClothBounds> faceBounds, Span<ClothBounds> nodeBounds)
    {
        for (int i = 0; i < _faces.Length; i++)
            faceBounds[i] = ClothBounds.Enclose(new ClothFacet(_faces[i], before).Bounds, new ClothFacet(_faces[i], after).Bounds);
        for (int i = _nodes.Length - 1; i >= 0; i--)
        {
            Node node = _nodes[i];
            if (node.Count == 0)
            {
                nodeBounds[i] = ClothBounds.Enclose(nodeBounds[i + 1], nodeBounds[_nodes[i + 1].Skip]);
                continue;
            }
            ClothBounds bounds = faceBounds[node.First];
            for (int face = node.First + 1; face < node.First + node.Count; face++)
                bounds = ClothBounds.Enclose(bounds, faceBounds[face]);
            nodeBounds[i] = bounds;
        }
    }

    internal readonly record struct Node(int First, int Count, int Skip);
    private readonly record struct Face(ClothTriangle Triangle, Vector3 Center, int Ordinal);
}
