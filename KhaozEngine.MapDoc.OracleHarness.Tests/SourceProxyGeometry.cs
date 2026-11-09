using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Non-welded expectations from source placements and meshes, rounded once in the cluster frame.</summary>
internal static class SourceProxyGeometry
{
    internal static GltfMesh Build(IReadOnlyList<PropPlacement> placements,
        IReadOnlyDictionary<string, GltfMesh> meshes, MapSubmissionAnchor anchor)
    {
        var vertices = new List<ModelVertex>(); var indices = new List<uint>();
        foreach (var placement in placements)
        {
            if (!meshes.TryGetValue(placement.Id, out var mesh) || mesh.Vertices.Length == 0) continue;
            uint start = checked((uint)vertices.Count);
            double c = Math.Cos(placement.Yaw), s = Math.Sin(placement.Yaw), scale = placement.Scale;
            double x = (double)placement.X - anchor.X, y = (double)placement.Y - anchor.Y, z = (double)placement.Z - anchor.Z;
            foreach (var source in mesh.Vertices)
            {
                var output = source; var p = source.Position; var n = source.Normal;
                output.Position = new((float)(x + scale * (p.X * c + p.Z * s)),
                    (float)(y + scale * p.Y), (float)(z + scale * (-p.X * s + p.Z * c)));
                double nx = n.X * c + n.Z * s, ny = n.Y, nz = -n.X * s + n.Z * c;
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                output.Normal = new((float)(nx / length), (float)(ny / length), (float)(nz / length));
                output.Tangent = Vector4.Zero;
                vertices.Add(output);
            }
            foreach (uint index in mesh.Indices32)
            {
                if (index >= mesh.Vertices.Length) throw new InvalidOperationException("Invalid source proxy corner.");
                indices.Add(checked(start + index));
            }
        }
        return new(vertices.ToArray(), indices.ToArray());
    }
}
