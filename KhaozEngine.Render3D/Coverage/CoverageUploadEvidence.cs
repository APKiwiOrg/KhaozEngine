using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KhaozEngine.Render3D;

// Cold geometry authentication. Asset-byte authentication remains the caller's separate responsibility.
internal sealed class CoverageUploadEvidence
{
    internal readonly string Fingerprint;
    internal readonly MeshBounds Bounds;

    internal CoverageUploadEvidence(GltfMesh mesh)
    {
        Fingerprint = Hash(mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Indices32);
        Bounds = MeshBounds.FromVertices(mesh.Vertices);
    }

    internal static string Hash(Vector3[] positions, uint[] indices)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(positions.Length));
        hash.AppendData(MemoryMarshal.AsBytes(positions.AsSpan()));
        hash.AppendData(BitConverter.GetBytes(indices.Length));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed class CoverageRigidSourceEvidence
{
    internal readonly Dictionary<int, string> Slices = new();
    internal readonly MeshBounds Bounds;
    internal readonly bool Supported;

    internal CoverageRigidSourceEvidence(CoverageSourceTopology source)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in source.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        Bounds = new(min, max);
        foreach (int slice in source.Materials.Select(m => m.Slice))
        {
            var vertices = source.Vertices.Select((v, i) => (v, i)).Where(v => v.v.MaterialSlice == slice).ToArray();
            if (vertices.Length == 0 || vertices.Any(v => v.v.SliceVertex >= vertices.Length)) return;
            var positions = new Vector3[vertices.Max(v => v.v.SliceVertex) + 1];
            var seen = new bool[positions.Length];
            foreach (var (v, i) in vertices)
            {
                if (seen[v.SliceVertex] && positions[v.SliceVertex] != source.Positions[i]) return;
                seen[v.SliceVertex] = true;
                positions[v.SliceVertex] = source.Positions[i];
            }
            if (seen.Any(value => !value)) return;
            uint[] indices = source.Triangles.Where(t => t.MaterialSlice == slice).SelectMany(t =>
                new[] { (uint)source.Vertices[t.A].SliceVertex, (uint)source.Vertices[t.B].SliceVertex,
                    (uint)source.Vertices[t.C].SliceVertex }).ToArray();
            Slices.Add(slice, CoverageUploadEvidence.Hash(positions, indices));
        }
        Supported = source.Materials.All(m => m.AlphaMode == 0) && Slices.Count > 0;
    }
}
