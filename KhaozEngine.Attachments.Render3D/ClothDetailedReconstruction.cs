using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Each pose has one frame per rig triangle and one physical normal per rig node, shared by all draws.
internal sealed class ClothDetailedReconstruction(BoundClothSurface profile)
{
    private ModelVertex[][] _vertices = Allocate(profile);
    private readonly Vector3[] _nodes = new Vector3[profile.Rig.RestPositions.Length];
    private readonly Vector3[] _normals = new Vector3[profile.Rig.RestPositions.Length];
    private readonly ClothFittedFrame[] _frames = new ClothFittedFrame[profile.Rig.Triangles.Length];
    private readonly Matrix4x4[] _composed = new Matrix4x4[profile.JointCount];

    internal static ModelVertex[][] Allocate(BoundClothSurface surface)
    {
        var storage = new ModelVertex[surface.Materials.Length][];
        for (int i = 0; i < storage.Length; i++) storage[i] = new ModelVertex[surface.Materials[i].Vertices.Length];
        return storage;
    }

    internal Span<ModelVertex> Vertices(int material) => _vertices[material];
    internal ModelVertex[][] Storage => _vertices;

    internal ModelVertex[][] Exchange(ModelVertex[][] storage)
    {
        ModelVertex[][] result = _vertices;
        _vertices = storage;
        return result;
    }

    internal void Cheap(BoundClothSurface surface, ReadOnlySpan<Matrix4x4> garmentPalette)
    {
        surface.ComposeGarment(garmentPalette, _composed);
        for (int material = 0; material < _vertices.Length; material++)
        {
            var source = surface.Materials[material].Vertices.AsSpan();
            for (int i = 0; i < source.Length; i++)
                _vertices[material][i] = SkinningMath.SkinVertex(source[i], _composed);
        }
    }

    internal bool Fitted(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes, in Matrix4x4 model,
        ReadOnlySpan<Matrix4x4> garmentPalette) => Reconstruct(surface, nodes, model, garmentPalette, intrinsic: false);

    internal bool Material(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes, in Matrix4x4 model,
        ReadOnlySpan<Matrix4x4> garmentPalette) => Reconstruct(surface, nodes, model, garmentPalette, intrinsic: true);

    private bool Reconstruct(BoundClothSurface surface, ReadOnlySpan<Vector3> nodes, in Matrix4x4 model,
        ReadOnlySpan<Matrix4x4> garmentPalette, bool intrinsic)
    {
        if (!Matrix4x4.Invert(model, out Matrix4x4 toLocal)) return false;
        for (int i = 0; i < nodes.Length; i++) _nodes[i] = Vector3.Transform(nodes[i], toLocal);
        surface.ComposeGarment(garmentPalette, _composed);
        if (!BuildFrames(surface, intrinsic)) return false;

        for (int material = 0; material < _vertices.Length; material++)
        {
            BoundClothMaterial view = surface.Materials[material];
            Span<ModelVertex> vertices = _vertices[material];
            var source = view.Vertices.AsSpan();
            for (int i = 0; i < source.Length; i++)
                if (!view.Moves(i)) vertices[i] = SkinningMath.SkinVertex(source[i], _composed);

            for (int i = 0; i < view.Embeddings.Length; i++)
            {
                BoundClothEmbedding fitted = view.Embeddings[i];
                BoundClothMaterialEmbedding attachment = intrinsic ? view.MaterialEmbeddings[i] :
                    new(fitted.VertexIndex, fitted.Triangle, fitted.Barycentric, fitted.SignedOffset);
                ClothTriangle triangle = surface.Rig.Triangles[attachment.Triangle];
                ClothFittedFrame frame = _frames[attachment.Triangle];
                Vector3 midpoint = CheckedClothSurface.Blend(triangle, _nodes, attachment.Barycentric);
                Vector3 position = intrinsic
                    ? midpoint + attachment.SignedOffset * Unit(CheckedClothSurface.Blend(triangle, _normals, attachment.Barycentric), frame.Normal)
                    : midpoint + Directions(frame, fitted.FittedOffset);
                if (!ChainMath.IsFinite(position)) return false;
                var authored = source[fitted.VertexIndex];
                vertices[fitted.VertexIndex] = view.Shading[i].Pose(position, authored, frame);
            }
        }
        return true;
    }

    private bool BuildFrames(BoundClothSurface surface, bool intrinsic)
    {
        if (intrinsic) Array.Clear(_normals);
        ReadOnlySpan<ClothTriangle> faces = surface.Rig.Triangles;
        for (int i = 0; i < faces.Length; i++)
        {
            ClothTriangle face = faces[i];
            if (!ClothFittedFrame.TryCreate(face, _nodes, out _frames[i], out Vector3 area)) return false;
            if (!intrinsic) continue;
            _normals[face.A] += area;
            _normals[face.B] += area;
            _normals[face.C] += area;
        }
        if (!intrinsic) return true;
        surface.PosePinDirections(_composed, _normals);
        for (int i = 0; i < _normals.Length; i++) _normals[i] = Unit(_normals[i], Vector3.Zero);
        return true;
    }

    internal static Vector3 Directions(ClothFittedFrame frame, Vector3 coordinates) =>
        coordinates.X * frame.U + coordinates.Y * frame.V + coordinates.Z * frame.Normal;

    internal static Vector3 Unit(Vector3 direction, Vector3 fallback)
    {
        float length = direction.Length();
        return length > 0 && float.IsFinite(length) ? direction / length : fallback;
    }
}

internal readonly record struct ClothShading(Vector3 Normal, Vector3 Tangent, float Handedness)
{
    internal ClothShading(in ModelVertex fitted, ClothFittedFrame reference)
        : this(reference.Coordinates(fitted.Normal), reference.Coordinates(new(fitted.Tangent.X, fitted.Tangent.Y, fitted.Tangent.Z)),
            fitted.Tangent.W) { }

    internal ModelVertex Pose(Vector3 position, in SkinnedVertex source, ClothFittedFrame frame)
    {
        Vector3 normal = ClothDetailedReconstruction.Unit(ClothDetailedReconstruction.Directions(frame, Normal), source.Normal);
        Vector3 tangent = ClothDetailedReconstruction.Unit(ClothDetailedReconstruction.Directions(frame, Tangent), Vector3.Zero);
        return new ModelVertex(position, normal, source.Color, source.Uv, new(tangent, Handedness));
    }
}
