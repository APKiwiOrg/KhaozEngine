using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Mesh triangles of one installed static, captured and validated exactly as the feature query captures
/// them, with each triangle published as its own polygon. Element id is the triangle index. Coplanar triangles are
/// not merged.</summary>
internal static class SupportNeighborhoodMesh
{
    internal static bool IsMesh(TypedIndex shape) => shape.Type == default(Mesh).TypeId;

    /// <summary>Captures the mesh with the feature query's topology validation. A non-manifold mesh is ambiguous.
    /// Every other admission failure lies outside the proven pose, size or numerical domain.</summary>
    internal static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex shape, in RigidPose pose,
        out CapsuleFeatureMesh? mesh)
    {
        CapsuleFeatureStatus status = CapsuleFeatureMesh.Capture(simulation, shape, pose, out mesh);
        if (status == CapsuleFeatureStatus.Complete) return status;
        mesh = null;
        return status == CapsuleFeatureStatus.Ambiguous ? CapsuleFeatureStatus.Ambiguous : CapsuleFeatureStatus.Unsupported;
    }

    /// <summary>Every triangle whose bounds may reach the probe segment within <paramref name="reach"/>. A triangle
    /// is wound A, C, B so the polygon's inside convention carries the mesh front Cross(C - A, B - A), with edges
    /// and normal from the mesh's common installed operator.</summary>
    internal static void Polygons(StaticHandle owner, CapsuleFeatureMesh mesh, FeaturePoint lower, FeaturePoint upper,
        FeatureNumber reach, List<SupportPolygon> output)
    {
        FeatureNumber reachSquared = reach.Multiply(reach);
        for (int id = 0; id < mesh.Faces.Length; id++)
        {
            int[] face = mesh.Faces[id];
            int a = face[0], b = face[1], c = face[2];
            FeaturePoint[] vertices = [mesh.Vertices[a], mesh.Vertices[c], mesh.Vertices[b]];
            if (CapsuleFeatureMeshBounds.Far(lower, upper, reachSquared, vertices)) continue;
            FeaturePoint[] edges = [mesh.EdgeDirection(a, c), mesh.EdgeDirection(c, b), mesh.EdgeDirection(b, a)];
            output.Add(new SupportPolygon(owner, id, vertices, edges, mesh.Normals[id]));
        }
    }
}
