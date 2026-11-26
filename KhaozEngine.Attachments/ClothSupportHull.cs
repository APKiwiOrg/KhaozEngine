using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The owning source, part, motion layer and optional detailed geometry group.</summary>
public readonly record struct ClothSupportIdentity(int SourceId, uint PartId, int FeatureGroup, AttachmentLayer Layer)
{
    internal bool SameGroup(in ClothSupportIdentity other) =>
        FeatureGroup != 0 && FeatureGroup == other.FeatureGroup && SourceId == other.SourceId && PartId == other.PartId &&
        Layer == other.Layer;
}

/// <summary>One rigid lower piece's detailed contact geometry and opening and closing transforms.</summary>
public readonly record struct ClothHullSupport(ClothSupportIdentity Identity, ClothSupportHull Hull,
    Matrix4x4 Opening, Matrix4x4 Closing);

public enum ClothSupportHullRefusal
{
    FaceCount,
    InvalidVertex,
    DuplicateVertex,
    InvalidFaceVertex,
    DegenerateFace,
    InvalidWinding,
    Nonconvex,
    RepeatedDirectedEdge,
    UnusedVertex,
    UnpairedEdge,
}

/// <summary>A malformed convex hull and the indices involved in the refusal.</summary>
public sealed class ClothSupportHullException : ArgumentException
{
    internal ClothSupportHullException(ClothSupportHullRefusal reason, string message, string parameter,
        int? faceIndex = null, int? vertexIndex = null, int? otherVertexIndex = null) : base(message, parameter)
    {
        Reason = reason;
        FaceIndex = faceIndex;
        VertexIndex = vertexIndex;
        OtherVertexIndex = otherVertexIndex;
    }

    public ClothSupportHullRefusal Reason { get; }
    public int? FaceIndex { get; }
    public int? VertexIndex { get; }
    public int? OtherVertexIndex { get; }
}

/// <summary>An immutable triangulated convex solid in joint-local metres.</summary>
public sealed class ClothSupportHull
{
    public const int VertexLimit = 16;

    private readonly Vector3[] _vertices;
    private readonly ClothTriangle[] _faces;

    private ClothSupportHull(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<ClothTriangle> faces, float radius)
    {
        _vertices = vertices.ToArray();
        _faces = faces.ToArray();
        Radius = radius;
    }

    public ReadOnlySpan<Vector3> Vertices => _vertices;
    public ReadOnlySpan<ClothTriangle> Faces => _faces;
    internal float Radius { get; }

    /// <summary>Copies the vertices and the closed outward triangle winding of a convex solid.</summary>
    public static ClothSupportHull Create(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<ClothTriangle> faces)
    {
        if (vertices.Length is < 4 or > VertexLimit)
            throw new ArgumentOutOfRangeException(nameof(vertices));
        if (faces.Length is < 4 or > 2 * VertexLimit - 4)
            throw new ClothSupportHullException(ClothSupportHullRefusal.FaceCount,
                "A hull needs between four and twenty-eight faces.", nameof(faces));

        float radius = 0;
        Vector3 center = Vector3.Zero;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 vertex = vertices[i];
            float length = vertex.Length();
            if (!float.IsFinite(length))
                throw new ClothSupportHullException(ClothSupportHullRefusal.InvalidVertex,
                    "Hull vertices require a finite distance from the joint origin.", nameof(vertices), vertexIndex: i);
            int duplicate = vertices[..i].IndexOf(vertex);
            if (duplicate >= 0)
                throw new ClothSupportHullException(ClothSupportHullRefusal.DuplicateVertex,
                    "Hull vertex positions must be distinct.", nameof(vertices), vertexIndex: i, otherVertexIndex: duplicate);
            radius = MathF.Max(radius, length);
            center += vertex / vertices.Length;
        }

        Span<byte> directedEdges = stackalloc byte[VertexLimit * VertexLimit];
        Span<bool> used = stackalloc bool[VertexLimit];
        directedEdges.Clear();
        used.Clear();
        for (int index = 0; index < faces.Length; index++)
        {
            ClothTriangle face = faces[index];
            CheckVertex(face.A, vertices.Length, index);
            CheckVertex(face.B, vertices.Length, index);
            CheckVertex(face.C, vertices.Length, index);
            Vector3 a = vertices[face.A], b = vertices[face.B], c = vertices[face.C];
            Vector3 area = Vector3.Cross(b - a, c - a);
            float magnitude = area.Length();
            if (magnitude <= 0 || !float.IsFinite(magnitude))
                throw new ClothSupportHullException(ClothSupportHullRefusal.DegenerateFace,
                    "Hull faces must have finite positive area.", nameof(faces), index);
            Vector3 normal = area / magnitude;
            float tolerance = .000001f * MathF.Max((b - a).Length(), (c - a).Length());
            if (Vector3.Dot(center - a, normal) >= -tolerance)
                throw new ClothSupportHullException(ClothSupportHullRefusal.InvalidWinding,
                    "Hull faces must wind outwards from the interior.", nameof(faces), index);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
                if (Vector3.Dot(vertices[vertex] - a, normal) > tolerance)
                    throw new ClothSupportHullException(ClothSupportHullRefusal.Nonconvex,
                        "A hull vertex lies outside a face plane.", nameof(faces), index, vertex);
            AddEdge(face.A, face.B, index, directedEdges, used);
            AddEdge(face.B, face.C, index, directedEdges, used);
            AddEdge(face.C, face.A, index, directedEdges, used);
        }

        for (int a = 0; a < vertices.Length; a++)
        {
            if (!used[a])
                throw new ClothSupportHullException(ClothSupportHullRefusal.UnusedVertex,
                    "Each hull vertex must belong to a face.", nameof(vertices), vertexIndex: a);
            for (int b = 0; b < a; b++)
            {
                int forward = directedEdges[a * VertexLimit + b], reverse = directedEdges[b * VertexLimit + a];
                if (forward > 0 && reverse == 0) throw Unpaired(forward - 1, a, b);
                if (reverse > 0 && forward == 0) throw Unpaired(reverse - 1, b, a);
            }
        }
        if (faces.Length != 2 * vertices.Length - 4)
            throw new ClothSupportHullException(ClothSupportHullRefusal.FaceCount,
                "A closed convex hull needs two triangles per vertex minus four.", nameof(faces));
        return new ClothSupportHull(vertices, faces, radius);
    }

    private static void CheckVertex(int vertex, int count, int face)
    {
        if ((uint)vertex >= count)
            throw new ClothSupportHullException(ClothSupportHullRefusal.InvalidFaceVertex,
                "A hull face references an unknown vertex.", "faces", face, vertex);
    }

    private static ClothSupportHullException Unpaired(int face, int from, int to) =>
        new(ClothSupportHullRefusal.UnpairedEdge, "A hull edge needs an opposed incident face.", "faces", face, from, to);

    private static void AddEdge(int from, int to, int face, Span<byte> directions, Span<bool> used)
    {
        int slot = from * VertexLimit + to;
        if (directions[slot] != 0)
            throw new ClothSupportHullException(ClothSupportHullRefusal.RepeatedDirectedEdge,
                "A hull edge is repeated in the same direction.", "faces", face, from, to);
        directions[slot] = (byte)(face + 1);
        used[from] = true;
    }
}

internal readonly record struct ClothHullPose(Quaternion Rotation, Vector3 Translation, float Scale)
{
    internal static bool TryCreate(in Matrix4x4 matrix, out ClothHullPose pose)
    {
        const float tolerance = .00001f;
        pose = default;
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1 || !ChainMath.IsFinite(matrix.Translation))
            return false;

        var rowX = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var rowY = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var rowZ = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        float scale = rowX.Length();
        if (!float.IsFinite(scale) || scale <= 0 || !ChainMath.IsFinite(rowY) || !ChainMath.IsFinite(rowZ))
            return false;

        rowX /= scale;
        rowY /= scale;
        rowZ /= scale;
        if (MathF.Abs(rowY.LengthSquared() - 1) > tolerance || MathF.Abs(rowZ.LengthSquared() - 1) > tolerance ||
            MathF.Abs(Vector3.Dot(rowX, rowY)) > tolerance || MathF.Abs(Vector3.Dot(rowX, rowZ)) > tolerance ||
            MathF.Abs(Vector3.Dot(rowY, rowZ)) > tolerance || Vector3.Dot(Vector3.Cross(rowX, rowY), rowZ) < 1 - tolerance)
            return false;

        var rotation = new Matrix4x4(rowX.X, rowX.Y, rowX.Z, 0, rowY.X, rowY.Y, rowY.Z, 0,
            rowZ.X, rowZ.Y, rowZ.Z, 0, 0, 0, 0, 1);
        pose = new ClothHullPose(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotation)), matrix.Translation, scale);
        return true;
    }

    internal ClothHullPose At(in ClothHullPose other, float time) =>
        new(ClothContactSweep.Interpolate(Rotation, other.Rotation, time), Vector3.Lerp(Translation, other.Translation, time),
            float.Lerp(Scale, other.Scale, time));

    internal Vector3 World(Vector3 local) => Translation + Vector3.Transform(Scale * local, Rotation);
    internal Vector3 Local(Vector3 world) => Vector3.Transform(world - Translation, Quaternion.Conjugate(Rotation)) / Scale;
}
