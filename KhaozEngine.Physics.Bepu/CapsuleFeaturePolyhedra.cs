using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal readonly record struct CapsuleFeaturePolyhedronEdge(int A, int B, int FirstFace, int SecondFace);

/// <summary>A strictly convex, closed, consistently wound finite solid. Admission checks all faces,
/// all source vertices and the complete edge/vertex neighborhoods before any closest query.
/// The proved rigid affine map preserves that topology without rounding world vertices.</summary>
internal sealed class CapsuleFeaturePolyhedron(TypedIndex shape, RigidPose localPose, RigidPose worldPose,
    int leafId, Vector3[] localVertices, FeaturePoint[] vertices, int[][] faces)
{
    internal TypedIndex InstalledShape { get; } = shape;
    internal RigidPose InstalledLocalPose { get; } = localPose;
    internal RigidPose InstalledWorldPose { get; } = worldPose;
    internal int LeafId { get; } = leafId;
    internal Vector3[] LocalVertices { get; } = localVertices;
    internal FeaturePoint[] Vertices { get; } = vertices;
    internal int[][] Faces { get; } = faces;
    internal CapsuleFeaturePolyhedronEdge[] Edges { get; private set; } = [];
    internal FeaturePoint[] Normals { get; private set; } = [];
    internal int[][] VertexFaces { get; private set; } = [];

    internal CapsuleFeatureStatus Validate()
    {
        if (Vertices.Length < 4 || Faces.Length < 4 || LocalVertices.Length != Vertices.Length)
            return CapsuleFeatureStatus.Unsupported;
        if (Faces.Length > CapsuleFeatureGeometry.MaximumFaces || Vertices.Length > CapsuleFeatureGeometry.MaximumVertices)
            return CapsuleFeatureStatus.CapacityExceeded;
        if (new HashSet<Vector3>(LocalVertices).Count != Vertices.Length) return CapsuleFeatureStatus.Ambiguous;
        var edges = new List<CapsuleFeaturePolyhedronEdge>();
        var edgeIds = new Dictionary<(int, int), int>();
        var incidence = new List<int>[Vertices.Length];
        for (int i = 0; i < incidence.Length; i++) incidence[i] = [];
        var normals = new FeaturePoint[Faces.Length];
        for (int faceId = 0; faceId < Faces.Length; faceId++)
        {
            int[] face = Faces[faceId];
            if (face.Length < 3 || new HashSet<int>(face).Count != face.Length)
                return CapsuleFeatureStatus.Ambiguous;
            for (int i = 0; i < face.Length; i++)
                if ((uint)face[i] >= (uint)Vertices.Length) return CapsuleFeatureStatus.Unsupported;
            Vector3 a = LocalVertices[face[0]], b = LocalVertices[face[1]], c = LocalVertices[face[2]];
            int projection = Projection(a, b, c);
            if (projection < 0) return CapsuleFeatureStatus.Unsupported;
            GeometrySign winding = ProjectedOrient(a, b, c, projection);
            for (int vertex = 0; vertex < Vertices.Length; vertex++)
            {
                GeometrySign side = BoundedGeometryArithmetic.Orient3D(a, b, c, LocalVertices[vertex]);
                if (side == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
                bool member = Array.IndexOf(face, vertex) >= 0;
                if (member ? side != GeometrySign.Zero : side != GeometrySign.Negative)
                    return CapsuleFeatureStatus.Ambiguous;
            }
            for (int i = 0; i < face.Length; i++)
            {
                int first = face[i], second = face[(i + 1) % face.Length];
                for (int j = 0; j < face.Length; j++)
                {
                    int tested = face[j];
                    if (tested == first || tested == second) continue;
                    GeometrySign side = ProjectedOrient(LocalVertices[first], LocalVertices[second], LocalVertices[tested], projection);
                    if (side == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
                    // Strictness refuses collinear aliases and self-intersecting or concave polygons.
                    if (side != winding) return CapsuleFeatureStatus.Ambiguous;
                }
                var key = first < second ? (first, second) : (second, first);
                if (edgeIds.TryGetValue(key, out int edgeId))
                {
                    CapsuleFeaturePolyhedronEdge edge = edges[edgeId];
                    if (edge.SecondFace >= 0 || edge.A != second || edge.B != first)
                        return CapsuleFeatureStatus.Ambiguous;
                    edges[edgeId] = edge with { SecondFace = faceId };
                }
                else
                {
                    edgeIds.Add(key, edges.Count);
                    edges.Add(new(first, second, faceId, -1));
                }
                incidence[first].Add(faceId);
            }
            FeaturePoint ab = FeaturePoint.Subtract(Vertices[face[1]], Vertices[face[0]]);
            FeaturePoint ac = FeaturePoint.Subtract(Vertices[face[2]], Vertices[face[0]]);
            normals[faceId] = FeaturePoint.Cross(ab, ac);
            if (FeaturePoint.Dot(normals[faceId], normals[faceId]).Sign != GeometrySign.Positive)
                return CapsuleFeatureStatus.Unresolved;
        }
        if (Vertices.Length - edges.Count + Faces.Length != 2) return CapsuleFeatureStatus.Ambiguous;
        foreach (CapsuleFeaturePolyhedronEdge edge in edges)
            if (edge.SecondFace < 0) return CapsuleFeatureStatus.Ambiguous;
        // A complete cyclic vertex link is required, not just two faces per edge.
        for (int vertex = 0; vertex < Vertices.Length; vertex++)
        {
            if (incidence[vertex].Count < 3) return CapsuleFeatureStatus.Ambiguous;
            if (!Connected(incidence[vertex], edges, vertex)) return CapsuleFeatureStatus.Ambiguous;
        }
        var all = new List<int>();
        for (int i = 0; i < Faces.Length; i++) all.Add(i);
        if (!Connected(all, edges, -1)) return CapsuleFeatureStatus.Ambiguous;
        Edges = edges.ToArray();
        Normals = normals;
        VertexFaces = new int[Vertices.Length][];
        for (int i = 0; i < incidence.Length; i++) VertexFaces[i] = incidence[i].ToArray();
        return CapsuleFeatureStatus.Complete;
    }

    static bool Connected(List<int> faces, List<CapsuleFeaturePolyhedronEdge> edges, int vertex)
    {
        var reached = new HashSet<int> { faces[0] };
        var queue = new Queue<int>();
        queue.Enqueue(faces[0]);
        while (queue.TryDequeue(out int face))
        {
            int degree = 0;
            foreach (CapsuleFeaturePolyhedronEdge edge in edges)
            {
                if (vertex >= 0 && edge.A != vertex && edge.B != vertex) continue;
                int other = edge.FirstFace == face ? edge.SecondFace : edge.SecondFace == face ? edge.FirstFace : -1;
                if (other < 0) continue;
                degree++;
                if (reached.Add(other)) queue.Enqueue(other);
            }
            if (vertex >= 0 && degree != 2) return false;
        }
        return reached.Count == faces.Count;
    }

    internal GeometrySign Contains(FeaturePoint point)
    {
        bool outside = false;
        for (int faceId = 0; faceId < Faces.Length; faceId++)
        {
            FeaturePoint delta = FeaturePoint.Subtract(point, Vertices[Faces[faceId][0]]);
            GeometrySign side = FeaturePoint.Dot(Normals[faceId], delta).Sign;
            if (side == GeometrySign.Unresolved) return GeometrySign.Unresolved;
            outside |= side == GeometrySign.Positive;
        }
        // Zero includes the solid boundary. The query has no certified zero-distance normal there.
        return outside ? GeometrySign.Negative : GeometrySign.Zero;
    }

    internal GeometrySign FaceInterior(int faceId, FeaturePoint point)
    {
        int[] face = Faces[faceId];
        bool boundary = false;
        for (int i = 0; i < face.Length; i++)
        {
            FeaturePoint a = Vertices[face[i]];
            FeaturePoint b = Vertices[face[(i + 1) % face.Length]];
            FeatureNumber side = FeaturePoint.Dot(FeaturePoint.Cross(FeaturePoint.Subtract(b, a),
                FeaturePoint.Subtract(point, a)), Normals[faceId]);
            GeometrySign sign = side.Sign;
            if (sign == GeometrySign.Unresolved) return sign;
            if (sign == GeometrySign.Negative) return sign;
            boundary |= sign == GeometrySign.Zero;
        }
        return boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    static int Projection(Vector3 a, Vector3 b, Vector3 c)
    {
        for (int i = 0; i < 3; i++)
        {
            GeometrySign winding = ProjectedOrient(a, b, c, i);
            if (winding is GeometrySign.Positive or GeometrySign.Negative) return i;
            if (winding == GeometrySign.Unresolved) return -1;
        }
        return -1;
    }

    static GeometrySign ProjectedOrient(Vector3 a, Vector3 b, Vector3 c, int projection) => projection switch
    {
        0 => BoundedGeometryArithmetic.Orient2D(a.Y, a.Z, b.Y, b.Z, c.Y, c.Z),
        1 => BoundedGeometryArithmetic.Orient2D(a.Z, a.X, b.Z, b.X, c.Z, c.X),
        _ => BoundedGeometryArithmetic.Orient2D(a.X, a.Y, b.X, b.Y, c.X, c.Y),
    };
}
