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
/// The common positive-determinant affine map preserves topology without rounded world vertices.</summary>
internal sealed class CapsuleFeaturePolyhedron
{
    InstalledPoseOperator _transform;

    internal CapsuleFeaturePolyhedron(TypedIndex shape, RigidPose localPose, RigidPose worldPose, int leafId,
        Vector3[] localVertices, FeaturePoint[] vertices, int[][] faces, InstalledPoseOperator transform) =>
        Reset(shape, localPose, worldPose, leafId, localVertices, vertices, faces, transform);

    /// <summary>An empty leaf that a <see cref="CapsuleFeatureCaptureScratch"/> reuses through
    /// <see cref="Reset"/>.</summary>
    internal CapsuleFeaturePolyhedron() { }

    internal void Reset(TypedIndex shape, RigidPose localPose, RigidPose worldPose, int leafId,
        Vector3[] localVertices, FeaturePoint[] vertices, int[][] faces, InstalledPoseOperator transform)
    {
        InstalledShape = shape;
        InstalledLocalPose = localPose;
        InstalledWorldPose = worldPose;
        LeafId = leafId;
        LocalVertices = localVertices;
        Vertices = vertices;
        Faces = faces;
        _transform = transform;
        Edges = [];
        Normals = [];
        VertexFaces = [];
    }

    internal TypedIndex InstalledShape { get; private set; }
    internal RigidPose InstalledLocalPose { get; private set; }
    internal RigidPose InstalledWorldPose { get; private set; }
    internal int LeafId { get; private set; }
    internal Vector3[] LocalVertices { get; private set; } = [];
    internal FeaturePoint[] Vertices { get; private set; } = [];
    internal int[][] Faces { get; private set; } = [];
    internal CapsuleFeaturePolyhedronEdge[] Edges { get; private set; } = [];
    internal FeaturePoint[] Normals { get; private set; } = [];
    internal int[][] VertexFaces { get; private set; } = [];
    internal FeaturePoint EdgeDirection(int a, int b) => _transform.Edge(LocalVertices[a], LocalVertices[b]);

    /// <summary>Admits the leaf. With <paramref name="scratch"/> every working collection and the normals come from
    /// it, and the edge and vertex incidence arrays are not published. The decision is the same either way.</summary>
    internal CapsuleFeatureStatus Validate(CapsuleFeatureCaptureScratch? scratch = null)
    {
        if (Vertices.Length < 4 || Faces.Length < 4 || LocalVertices.Length != Vertices.Length)
            return CapsuleFeatureStatus.Unsupported;
        if (Faces.Length > CapsuleFeatureGeometry.MaximumFaces || Vertices.Length > CapsuleFeatureGeometry.MaximumVertices)
            return CapsuleFeatureStatus.CapacityExceeded;
        if (Distinct(LocalVertices, scratch) != Vertices.Length) return CapsuleFeatureStatus.Ambiguous;
        List<CapsuleFeaturePolyhedronEdge> edges = scratch?.Edges ?? [];
        Dictionary<(int, int), int> edgeIds = scratch?.EdgeIds ?? [];
        edges.Clear();
        edgeIds.Clear();
        var incidence = scratch is null ? new List<int>[Vertices.Length] : null;
        for (int i = 0; i < Vertices.Length; i++)
        {
            if (incidence is not null) incidence[i] = [];
            else scratch!.ClearedIncidence(i);
        }
        List<int> Incident(int vertex) => incidence is not null ? incidence[vertex] : scratch!.Incidence[vertex];
        FeaturePoint[] normals = scratch?.Points(Faces.Length) ?? new FeaturePoint[Faces.Length];
        for (int faceId = 0; faceId < Faces.Length; faceId++)
        {
            int[] face = Faces[faceId];
            if (face.Length < 3 || Distinct(face, scratch) != face.Length)
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
                Incident(first).Add(faceId);
            }
            FeaturePoint ab = EdgeDirection(face[0], face[1]);
            FeaturePoint ac = EdgeDirection(face[0], face[2]);
            normals[faceId] = FeaturePoint.Cross(ab, ac);
            if (FeaturePoint.Dot(normals[faceId], normals[faceId]).Sign != GeometrySign.Positive)
                return CapsuleFeatureStatus.Unresolved;
        }
        if (Vertices.Length - edges.Count + Faces.Length != 2) return CapsuleFeatureStatus.Ambiguous;
        foreach (CapsuleFeaturePolyhedronEdge edge in edges)
            if (edge.SecondFace < 0) return CapsuleFeatureStatus.Ambiguous;
        // A complete cyclic vertex link is required, not just two faces per edge.
        HashSet<int> reached = scratch?.Reached ?? [];
        Queue<int> queue = scratch?.Pending ?? new();
        for (int vertex = 0; vertex < Vertices.Length; vertex++)
        {
            if (Incident(vertex).Count < 3) return CapsuleFeatureStatus.Ambiguous;
            if (!Connected(Incident(vertex), edges, vertex, reached, queue)) return CapsuleFeatureStatus.Ambiguous;
        }
        List<int> all = scratch?.AllFaces ?? [];
        all.Clear();
        for (int i = 0; i < Faces.Length; i++) all.Add(i);
        if (!Connected(all, edges, -1, reached, queue)) return CapsuleFeatureStatus.Ambiguous;
        Normals = normals;
        if (incidence is null) return CapsuleFeatureStatus.Complete;
        Edges = edges.ToArray();
        VertexFaces = new int[Vertices.Length][];
        for (int i = 0; i < incidence.Length; i++) VertexFaces[i] = incidence[i].ToArray();
        return CapsuleFeatureStatus.Complete;
    }

    // The number of distinct values, as a HashSet built from them counts them.
    static int Distinct(Vector3[] values, CapsuleFeatureCaptureScratch? scratch)
    {
        if (scratch is null) return new HashSet<Vector3>(values).Count;
        scratch.Positions.Clear();
        foreach (Vector3 value in values) scratch.Positions.Add(value);
        return scratch.Positions.Count;
    }

    static int Distinct(int[] values, CapsuleFeatureCaptureScratch? scratch)
    {
        if (scratch is null) return new HashSet<int>(values).Count;
        scratch.FaceVertices.Clear();
        foreach (int value in values) scratch.FaceVertices.Add(value);
        return scratch.FaceVertices.Count;
    }

    static bool Connected(List<int> faces, List<CapsuleFeaturePolyhedronEdge> edges, int vertex,
        HashSet<int> reached, Queue<int> queue)
    {
        reached.Clear();
        queue.Clear();
        reached.Add(faces[0]);
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
            FeaturePoint edge = EdgeDirection(face[i], face[(i + 1) % face.Length]);
            FeatureNumber side = FeaturePoint.Dot(FeaturePoint.Cross(edge,
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
