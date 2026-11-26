using System;
using System.Collections.Immutable;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The original GLB logical mesh, primitive and vertex indices before material regrouping.</summary>
public readonly record struct ClothSourceVertex(int Mesh, int Primitive, int Vertex);

/// <summary>A sample on the original GLB. Element names a vertex without weights, or a triangle with weights.</summary>
public readonly record struct ClothSourcePoint(int Mesh, int Primitive, int Element, Vector3? Barycentric = null,
    float SignedOffset = 0);

/// <summary>A pinned material node and its sample on the host-skinned garment.</summary>
public readonly record struct ClothPinSource(int Node, ClothSourcePoint Point);

/// <summary>A moving GLB vertex embedded on a material triangle and offset along its oriented normal.</summary>
public readonly record struct ClothRenderEmbedding(ClothSourceVertex Vertex, int Triangle, Vector3 Barycentric, float SignedOffset);

/// <summary>Owned inputs for the checked material rig and the two-way transfer between cloth and its garment.</summary>
public sealed record ClothSurfaceFile(string MeshSha256, int SourceVertexCount, int SourceTriangleCount, ClothMaterial Material,
    ImmutableArray<Vector3> RestPositions, ImmutableArray<Vector2> MaterialCoordinates, ImmutableArray<ClothTriangle> Triangles,
    ImmutableArray<ClothRestHinge> Hinges, ImmutableArray<ClothPinSource> Pins,
    ImmutableArray<ClothRenderEmbedding> Render, ImmutableArray<ClothSourcePoint> Fallback)
{
    public const int NodeLimit = 128;
    public const int TriangleLimit = 256;
    public const int SourceVertexLimit = 4096;
    public const int SourceTriangleLimit = 8192;

    /// <summary>Maximum authored rest round-trip error in metres for a source seam or render embedding.</summary>
    public const float RoundTripTolerance = .0005f;

    /// <summary>Check the file's intrinsic data and build the immutable numerical rig.</summary>
    public ClothRig CreateRig(string sourceName, uint? partId = null)
    {
        if (!AttachmentSetDefinition.ValidHash(MeshSha256))
            throw Refuse(ClothSurfaceRefusal.MeshDigest, "$.meshSha256", "The mesh needs a lowercase SHA-256.");
        if (RestPositions.IsDefault || MaterialCoordinates.IsDefault || Triangles.IsDefault ||
            Pins.IsDefault || Render.IsDefault || Fallback.IsDefault)
            throw Refuse(ClothSurfaceRefusal.Count, "$", "Required cloth arrays must be initialized.");
        CheckCount(SourceVertexCount, 1, SourceVertexLimit, "$.sourceVertices");
        CheckCount(SourceTriangleCount, 1, SourceTriangleLimit, "$.sourceTriangles");
        CheckCount(RestPositions.Length, 1, NodeLimit, "$.nodes");
        CheckCount(Triangles.Length, 1, TriangleLimit, "$.triangles");
        CheckCount(Hinges.IsDefault ? 0 : Hinges.Length, 0, 3 * Triangles.Length / 2, "$.hinges");
        CheckCount(Pins.Length, 1, RestPositions.Length, "$.pins");
        CheckCount(Render.Length, 1, SourceVertexCount, "$.render");
        CheckCount(MaterialCoordinates.Length, RestPositions.Length, RestPositions.Length, "$.nodes");
        CheckCount(Fallback.Length, RestPositions.Length, RestPositions.Length, "$.fallback");

        var pins = new int[Pins.Length];
        for (int i = 0; i < pins.Length; i++)
        {
            pins[i] = Pins[i].Node;
            CheckPoint(Pins[i].Point, "$.pins", i);
            if (Pins[i].Point.SignedOffset != 0)
                throw Refuse(ClothSurfaceRefusal.SourcePoint, "$.pins", "A pin must lie on its fitted source.", i);
        }

        for (int i = 0; i < Fallback.Length; i++) CheckPoint(Fallback[i], "$.fallback", i);
        for (int i = 0; i < Render.Length; i++)
        {
            ClothRenderEmbedding entry = Render[i];
            if (entry.Vertex.Mesh < 0 || entry.Vertex.Primitive < 0 || entry.Vertex.Vertex < 0
                || (uint)entry.Triangle >= Triangles.Length || !ValidWeights(entry.Barycentric) || !float.IsFinite(entry.SignedOffset))
                throw Refuse(ClothSurfaceRefusal.RenderEmbedding, "$.render", "A render embedding needs a source vertex, material triangle and finite weights and offset.", i);
            if (i > 0 && Compare(Render[i - 1].Vertex, entry.Vertex) >= 0)
                throw Refuse(ClothSurfaceRefusal.RenderOrder, "$.render", "Render vertices need distinct ascending source ordinals.", i);
        }

        try { return ClothRig.Create(RestPositions.AsSpan(), MaterialCoordinates.AsSpan(), Triangles.AsSpan(), pins, Hinges.AsSpan(), Material); }
        catch (ClothRigException error)
        {
            string path = error.Reason switch
            {
                ClothRigRefusal.InvalidPin => "$.pins",
                ClothRigRefusal.InvalidHinge => "$.hinges",
                ClothRigRefusal.InvalidTriangle or ClothRigRefusal.InvalidWinding or ClothRigRefusal.NonManifoldEdge => "$.triangles",
                _ => "$.nodes",
            };
            throw Refuse(ClothSurfaceRefusal.Topology, path, error.Message, error.ElementIndex, error);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw Refuse(ClothSurfaceRefusal.Material, "$.material", "Cloth material is outside its finite physical domain.", inner: error);
        }

        void CheckCount(int count, int minimum, int maximum, string path)
        {
            if (count < minimum || count > maximum)
                throw Refuse(ClothSurfaceRefusal.Count, path, $"Cloth count {count} is outside [{minimum}, {maximum}].");
        }

        void CheckPoint(ClothSourcePoint point, string path, int index)
        {
            if (point.Mesh < 0 || point.Primitive < 0 || point.Element < 0 || !float.IsFinite(point.SignedOffset) ||
                (point.Barycentric is { } weights ? !ValidWeights(weights) : point.SignedOffset != 0))
                throw Refuse(ClothSurfaceRefusal.SourcePoint, path, "A source sample needs valid indices, barycentric weights and offset.", index);
        }

        ClothSurfaceDataException Refuse(ClothSurfaceRefusal reason, string path, string message, int? index = null,
            Exception? inner = null) => new(reason, sourceName, message, inner)
            { PartId = partId, ElementIndex = index, JsonPath = index is int i ? $"{path}[{i}]" : path };
    }

    static bool ValidWeights(Vector3 w) => w.X >= 0 && w.Y >= 0 && w.Z >= 0
        && MathF.Abs(w.X + w.Y + w.Z - 1) <= .00001f;

    static int Compare(ClothSourceVertex first, ClothSourceVertex second) =>
        (first.Mesh, first.Primitive, first.Vertex).CompareTo((second.Mesh, second.Primitive, second.Vertex));
}
