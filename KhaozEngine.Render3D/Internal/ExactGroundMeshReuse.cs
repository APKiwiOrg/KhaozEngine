using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D.Internal;

/// <summary>GPU upload streams. The canonical CPU mesh remains owned by its caller.</summary>
internal readonly record struct GroundUploadStreams(ModelVertex[] Vertices, Vector3[] OutlineNormals, uint[] Indices);

/// <summary>Shares only complete byte-identical GPU tuples, preserving every ordered triangle input.</summary>
internal static class ExactGroundMeshReuse
{
    internal static GroundUploadStreams Compact(ModelVertex[] vertices, Vector3[] outlineNormals, uint[] indices)
    {
        if (vertices.Length != outlineNormals.Length)
            throw new ArgumentException("Outline normals must match the vertex stream.", nameof(outlineNormals));
        var unique = new Dictionary<int, uint>(vertices.Length, new TupleComparer(vertices, outlineNormals));
        var representatives = new List<int>(vertices.Length);
        var remap = new uint[vertices.Length];
        for (int vertex = 0; vertex < vertices.Length; vertex++)
        {
            if (!unique.TryGetValue(vertex, out uint slot))
            {
                slot = (uint)representatives.Count;
                unique.Add(vertex, slot);
                representatives.Add(vertex);
            }
            remap[vertex] = slot;
        }
        if (representatives.Count == vertices.Length) return new(vertices, outlineNormals, indices);
        var compactVertices = new ModelVertex[representatives.Count];
        var compactOutlines = new Vector3[representatives.Count];
        for (int vertex = 0; vertex < representatives.Count; vertex++)
        {
            compactVertices[vertex] = vertices[representatives[vertex]];
            compactOutlines[vertex] = outlineNormals[representatives[vertex]];
        }
        var compactIndices = new uint[indices.Length];
        for (int index = 0; index < indices.Length; index++) compactIndices[index] = remap[indices[index]];
        return new(compactVertices, compactOutlines, compactIndices);
    }

    // Dictionary keys retain a source index. Hashing and equality use raw words, including signed zeros and NaNs.
    sealed class TupleComparer(ModelVertex[] vertices, Vector3[] outlines) : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => MemoryMarshal.AsBytes(vertices.AsSpan(x, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(vertices.AsSpan(y, 1)))
            && MemoryMarshal.AsBytes(outlines.AsSpan(x, 1)).SequenceEqual(MemoryMarshal.AsBytes(outlines.AsSpan(y, 1)));

        public int GetHashCode(int vertex)
        {
            var hash = new HashCode();
            foreach (int word in MemoryMarshal.Cast<ModelVertex, int>(vertices.AsSpan(vertex, 1))) hash.Add(word);
            foreach (int word in MemoryMarshal.Cast<Vector3, int>(outlines.AsSpan(vertex, 1))) hash.Add(word);
            return hash.ToHashCode();
        }
    }
}
