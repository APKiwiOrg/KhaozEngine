using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal readonly record struct CapsuleFeatureMeshEdge(int A, int B, int FirstFace, int SecondFace,
    CapsuleFeatureKind Kind);

/// <summary>One uncached unit-scale installed mesh, with complete exact source incidence. Raw
/// triangle IDs survive coplanar seams. Signed zero is canonicalized, but no other vertices weld.</summary>
internal sealed class CapsuleFeatureMesh(Vector3[] localVertices, FeaturePoint[] vertices, int[][] faces)
{
    internal const int MaximumTriangles = 65536;
    internal const int MaximumIncidentFaces = 256;
    internal Vector3[] LocalVertices { get; } = localVertices;
    internal FeaturePoint[] Vertices { get; } = vertices;
    internal int[][] Faces { get; } = faces;
    internal FeaturePoint[] Normals { get; private set; } = [];
    internal CapsuleFeatureMeshEdge[] Edges { get; private set; } = [];
    internal int[][] VertexFaces { get; private set; } = [];
    internal CapsuleFeatureKind[] VertexKinds { get; private set; } = [];

    internal static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex index, in RigidPose pose,
        out CapsuleFeatureMesh? captured)
    {
        captured = null;
        if (!CapsuleFeatureGeometry.ProveRigidPose(pose)) return CapsuleFeatureStatus.Unsupported;
        ref Mesh installed = ref simulation.Shapes.GetShape<Mesh>(index.Index);
        if (installed.Scale != Vector3.One) return CapsuleFeatureStatus.Unsupported;
        int count = installed.Triangles.Length;
        if (count > MaximumTriangles) return CapsuleFeatureStatus.CapacityExceeded;
        if (count < 1) return CapsuleFeatureStatus.Unsupported;
        var local = new List<Vector3>();
        var ids = new Dictionary<Vector3, int>();
        var faces = new int[count][];
        for (int i = 0; i < count; i++)
        {
            ref Triangle triangle = ref installed.Triangles[i];
            Vector3[] source = [triangle.A, triangle.B, triangle.C];
            faces[i] = new int[3];
            for (int j = 0; j < 3; j++)
            {
                Vector3 p = source[j];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) ||
                    MathF.Abs(p.X) > 64 || MathF.Abs(p.Y) > 64 || MathF.Abs(p.Z) > 64)
                    return CapsuleFeatureStatus.Unsupported;
                p = new(p.X == 0 ? 0 : p.X, p.Y == 0 ? 0 : p.Y, p.Z == 0 ? 0 : p.Z);
                if (!ids.TryGetValue(p, out int vertex))
                {
                    vertex = local.Count;
                    ids.Add(p, vertex);
                    local.Add(p);
                }
                faces[i][j] = vertex;
            }
        }
        Vector3[] localArray = local.ToArray();
        CapsuleFeatureStatus transformed = CapsuleFeatureGeometry.TransformVertices(localArray, pose,
            out FeaturePoint[] world);
        if (transformed != CapsuleFeatureStatus.Complete) return transformed;
        var mesh = new CapsuleFeatureMesh(localArray, world, faces);
        CapsuleFeatureStatus validated = mesh.Validate();
        if (validated == CapsuleFeatureStatus.Complete) captured = mesh;
        return validated;
    }

    CapsuleFeatureStatus Validate()
    {
        var duplicates = new HashSet<(int, int, int)>();
        var edgeIds = new Dictionary<(int, int), int>();
        var edges = new List<CapsuleFeatureMeshEdge>();
        var faceEdges = new int[Faces.Length][];
        var incident = new List<int>[Vertices.Length];
        for (int i = 0; i < incident.Length; i++) incident[i] = [];
        Normals = new FeaturePoint[Faces.Length];
        for (int id = 0; id < Faces.Length; id++)
        {
            int[] face = Faces[id];
            if (face[0] == face[1] || face[1] == face[2] || face[2] == face[0])
                return CapsuleFeatureStatus.Unresolved;
            int[] sorted = (int[])face.Clone();
            Array.Sort(sorted);
            if (!duplicates.Add((sorted[0], sorted[1], sorted[2]))) return CapsuleFeatureStatus.Ambiguous;
            FeaturePoint ab = FeaturePoint.Subtract(Vertices[face[1]], Vertices[face[0]]);
            FeaturePoint ac = FeaturePoint.Subtract(Vertices[face[2]], Vertices[face[0]]);
            // Bepu 2.4 Triangle.RayTest defines the front as Cross(ac, ab).
            Normals[id] = FeaturePoint.Cross(ac, ab);
            if (FeaturePoint.Dot(Normals[id], Normals[id]).Sign != GeometrySign.Positive)
                return CapsuleFeatureStatus.Unresolved;
            faceEdges[id] = new int[3];
            for (int j = 0; j < 3; j++)
            {
                int a = face[j], b = face[(j + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (edgeIds.TryGetValue(key, out int edgeId))
                {
                    CapsuleFeatureMeshEdge edge = edges[edgeId];
                    if (edge.SecondFace >= 0 || edge.A != b || edge.B != a)
                        return CapsuleFeatureStatus.Ambiguous;
                    edges[edgeId] = edge with { SecondFace = id };
                }
                else
                {
                    edgeId = edges.Count;
                    edgeIds.Add(key, edgeId);
                    edges.Add(new(a, b, id, -1, CapsuleFeatureKind.OpenBoundary));
                }
                faceEdges[id][j] = edgeId;
                if (incident[a].Count == MaximumIncidentFaces) return CapsuleFeatureStatus.CapacityExceeded;
                incident[a].Add(id);
            }
        }
        for (int id = 0; id < edges.Count; id++)
        {
            CapsuleFeatureMeshEdge edge = edges[id];
            if (edge.SecondFace < 0) continue;
            int[] first = Faces[edge.FirstFace], second = Faces[edge.SecondFace];
            int opposite = second[0] != edge.A && second[0] != edge.B ? second[0]
                : second[1] != edge.A && second[1] != edge.B ? second[1] : second[2];
            GeometrySign side = BoundedGeometryArithmetic.Orient3D(LocalVertices[first[0]],
                LocalVertices[first[1]], LocalVertices[first[2]], LocalVertices[opposite]);
            if (side == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
            CapsuleFeatureKind kind;
            if (side == GeometrySign.Zero)
            {
                // Opposite edge directions plus matching plane normals prove a disjoint-interior
                // finite union across this edge. Same-side coplanar folds cannot become patches.
                GeometrySign agreement = FeaturePoint.Dot(Normals[edge.FirstFace], Normals[edge.SecondFace]).Sign;
                if (agreement == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
                if (agreement != GeometrySign.Positive) return CapsuleFeatureStatus.Ambiguous;
                kind = CapsuleFeatureKind.FaceInterior;
            }
            else kind = side == GeometrySign.Positive ? CapsuleFeatureKind.ConvexCrease : CapsuleFeatureKind.ConcaveCrease;
            edges[id] = edge with { Kind = kind };
        }
        Edges = edges.ToArray();
        VertexFaces = new int[Vertices.Length][];
        VertexKinds = new CapsuleFeatureKind[Vertices.Length];
        for (int vertex = 0; vertex < Vertices.Length; vertex++)
        {
            VertexFaces[vertex] = incident[vertex].ToArray();
            CapsuleFeatureStatus link = ValidateLink(vertex, faceEdges, out bool interiorPatch);
            if (link != CapsuleFeatureStatus.Complete) return link;
            if (interiorPatch)
            {
                CapsuleFeatureStatus flat = ProveFlatCycle(vertex);
                if (flat != CapsuleFeatureStatus.Complete) return flat;
            }
            VertexKinds[vertex] = interiorPatch ? CapsuleFeatureKind.FaceInterior : CapsuleFeatureKind.Vertex;
            if (incident[vertex].Count == 2 && !interiorPatch)
            {
                CapsuleFeatureStatus boundary = ClassifyFlatBoundary(vertex, faceEdges, out CapsuleFeatureKind kind);
                if (boundary != CapsuleFeatureStatus.Complete) return boundary;
                VertexKinds[vertex] = kind;
            }
        }
        return CapsuleFeatureStatus.Complete;
    }

    CapsuleFeatureStatus ProveFlatCycle(int vertex)
    {
        int[] incident = VertexFaces[vertex];
        int[] first = Faces[incident[0]];
        int projection = 0;
        GeometrySign winding = GeometrySign.Zero;
        for (; projection < 3; projection++)
        {
            winding = ProjectedOrient(LocalVertices[first[0]], LocalVertices[first[1]], LocalVertices[first[2]], projection);
            if (winding is GeometrySign.Positive or GeometrySign.Negative) break;
            if (winding == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
        }
        if (projection == 3) return CapsuleFeatureStatus.Unresolved;
        int revolutions = 0;
        Vector3 center = LocalVertices[vertex];
        foreach (int id in incident)
        {
            int[] face = Faces[id];
            int at = Array.IndexOf(face, vertex);
            Vector3 a = LocalVertices[face[(at + 1) % 3]], b = LocalVertices[face[(at + 2) % 3]];
            GeometrySign turn = ProjectedOrient(center, a, b, projection);
            if (turn == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
            if (turn != winding) return CapsuleFeatureStatus.Ambiguous;
            int firstHalf = PolarHalf(center, a, projection), secondHalf = PolarHalf(center, b, projection);
            if (winding == GeometrySign.Positive ? firstHalf > secondHalf : firstHalf < secondHalf) revolutions++;
        }
        // Every sector turns strictly in the same direction through less than half a revolution.
        // A connected closed link covers a finite disk once only when this winding count is one.
        return revolutions == 1 ? CapsuleFeatureStatus.Complete : CapsuleFeatureStatus.Ambiguous;
    }

    CapsuleFeatureStatus ClassifyFlatBoundary(int vertex, int[][] faceEdges, out CapsuleFeatureKind kind)
    {
        kind = CapsuleFeatureKind.Vertex;
        int[] incident = VertexFaces[vertex];
        var open = new List<int>();
        bool flat = false;
        foreach (int face in incident)
            foreach (int id in faceEdges[face])
            {
                CapsuleFeatureMeshEdge edge = Edges[id];
                if (edge.A != vertex && edge.B != vertex) continue;
                if (edge.SecondFace < 0) open.Add(edge.A == vertex ? edge.B : edge.A);
                else flat |= edge.Kind == CapsuleFeatureKind.FaceInterior;
            }
        if (!flat || open.Count != 2) return CapsuleFeatureStatus.Complete;
        FeaturePoint a = FeaturePoint.Subtract(Vertices[open[0]], Vertices[vertex]);
        FeaturePoint b = FeaturePoint.Subtract(Vertices[open[1]], Vertices[vertex]);
        FeaturePoint cross = FeaturePoint.Cross(a, b);
        GeometrySign collinear = FeaturePoint.Dot(cross, cross).Sign;
        if (collinear == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unresolved;
        if (collinear != GeometrySign.Zero) return CapsuleFeatureStatus.Complete;
        GeometrySign opposite = FeaturePoint.Dot(a, b).Sign;
        if (opposite != GeometrySign.Negative) return CapsuleFeatureStatus.Unresolved;
        kind = CapsuleFeatureKind.OpenBoundary;
        return CapsuleFeatureStatus.Complete;
    }

    static int PolarHalf(Vector3 center, Vector3 point, int projection)
    {
        float x = projection == 0 ? point.Y : projection == 1 ? point.Z : point.X;
        float y = projection == 0 ? point.Z : projection == 1 ? point.X : point.Y;
        float cx = projection == 0 ? center.Y : projection == 1 ? center.Z : center.X;
        float cy = projection == 0 ? center.Z : projection == 1 ? center.X : center.Y;
        return y > cy || y == cy && x > cx ? 0 : 1;
    }

    static GeometrySign ProjectedOrient(Vector3 a, Vector3 b, Vector3 c, int projection) => projection switch
    {
        0 => BoundedGeometryArithmetic.Orient2D(a.Y, a.Z, b.Y, b.Z, c.Y, c.Z),
        1 => BoundedGeometryArithmetic.Orient2D(a.Z, a.X, b.Z, b.X, c.Z, c.X),
        _ => BoundedGeometryArithmetic.Orient2D(a.X, a.Y, b.X, b.Y, c.X, c.Y),
    };

    CapsuleFeatureStatus ValidateLink(int vertex, int[][] faceEdges, out bool interiorPatch)
    {
        interiorPatch = true;
        int[] incident = VertexFaces[vertex];
        var visited = new HashSet<int> { incident[0] };
        var queue = new Queue<int>();
        queue.Enqueue(incident[0]);
        int boundary = 0;
        while (queue.TryDequeue(out int face))
        {
            foreach (int id in faceEdges[face])
            {
                CapsuleFeatureMeshEdge edge = Edges[id];
                if (edge.A != vertex && edge.B != vertex) continue;
                interiorPatch &= edge.Kind == CapsuleFeatureKind.FaceInterior;
                int other = edge.FirstFace == face ? edge.SecondFace : edge.FirstFace;
                if (other < 0) boundary++;
                else if (visited.Add(other)) queue.Enqueue(other);
            }
        }
        // A manifold triangle link is one cycle or one path. Edge admission gives maximum degree
        // two, connectedness excludes pinched/disconnected fans, and a path has two open ends.
        return visited.Count == incident.Length && boundary is 0 or 2
            ? CapsuleFeatureStatus.Complete : CapsuleFeatureStatus.Ambiguous;
    }

    internal GeometrySign FaceMembership(int id, FeaturePoint point)
    {
        int[] face = Faces[id];
        bool boundary = false;
        for (int i = 0; i < 3; i++)
        {
            FeaturePoint a = Vertices[face[i]], b = Vertices[face[(i + 1) % 3]];
            // The stored front normal reverses the usual oriented-edge cross convention.
            GeometrySign side = FeaturePoint.Dot(FeaturePoint.Cross(FeaturePoint.Subtract(point, a),
                FeaturePoint.Subtract(b, a)), Normals[id]).Sign;
            if (side == GeometrySign.Negative) return side;
            if (side == GeometrySign.Unresolved) return side;
            boundary |= side == GeometrySign.Zero;
        }
        return boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    internal CapsuleFeatureStatus ProveIncidence(FeaturePoint witness, int[] expected)
    {
        var known = new HashSet<int>(expected);
        for (int id = 0; id < Faces.Length; id++)
        {
            if (known.Contains(id)) continue; // This candidate's construction proves its own strata.
            int[] face = Faces[id];
            if (CapsuleFeatureMeshBounds.Disjoint(witness, Vertices[face[0]], Vertices[face[1]], Vertices[face[2]]))
                continue;
            FeatureNumber plane = FeaturePoint.Dot(Normals[id], FeaturePoint.Subtract(witness, Vertices[face[0]]));
            if (plane.Sign is GeometrySign.Positive or GeometrySign.Negative) continue;
            GeometrySign membership = FaceMembership(id, witness);
            if (membership == GeometrySign.Negative) continue;
            if (plane.Sign != GeometrySign.Zero || membership == GeometrySign.Unresolved)
                return CapsuleFeatureStatus.Unresolved;
            // Geometric touching/overlap without the exact admitted edge/vertex link cannot be
            // omitted as an unrelated neighbor, even if this additional triangle faces backward.
            return CapsuleFeatureStatus.Ambiguous;
        }
        return CapsuleFeatureStatus.Complete;
    }
}
