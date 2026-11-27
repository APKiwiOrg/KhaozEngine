using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

// Preserve the orientation of the fitted boundary even when the live sheet folds against it.
internal sealed class ClothPinnedDirections
{
    private readonly Vector3[] _directions;

    internal ClothPinnedDirections(ClothRig rig, SkinnedVertex[] source, ResolvedClothPoint[] samples,
        ReadOnlySpan<Vector3> fittedNodes, ReadOnlySpan<Vector3> fittedSource, ReadOnlySpan<Matrix4x4> composed)
    {
        _directions = new Vector3[samples.Length];
        Span<Vector3> normals = stackalloc Vector3[fittedNodes.Length];
        normals.Clear();
        foreach (ClothTriangle face in rig.Triangles)
        {
            Vector3 area = Vector3.Cross(fittedNodes[face.B] - fittedNodes[face.A], fittedNodes[face.C] - fittedNodes[face.A]);
            normals[face.A] += area;
            normals[face.B] += area;
            normals[face.C] += area;
        }

        for (int pin = 0; pin < samples.Length; pin++)
        {
            ResolvedClothPoint sample = samples[pin];
            Vector3 fittedNormal = normals[rig.Pins[pin]];
            if (sample.IsTriangle)
            {
                if (ClothFittedFrame.TryCreate(fittedSource[sample.A], fittedSource[sample.B], fittedSource[sample.C], out var frame, out _))
                    _directions[pin] = frame.Coordinates(fittedNormal);
                continue;
            }
            Matrix4x4 skin = SkinMatrix(source[sample.A], composed);
            _directions[pin] = Matrix4x4.Invert(skin, out Matrix4x4 inverse)
                ? Vector3.TransformNormal(fittedNormal, inverse) : Vector3.Zero;
        }
    }

    internal void Pose(ClothRig rig, SkinnedVertex[] source, ResolvedClothPoint[] samples,
        ReadOnlySpan<Matrix4x4> composed, Span<Vector3> normals)
    {
        for (int pin = 0; pin < samples.Length; pin++)
        {
            ResolvedClothPoint sample = samples[pin];
            Vector3 direction = sample.IsTriangle
                ? PosedTriangleDirection(sample, source, composed, _directions[pin])
                : Vector3.TransformNormal(_directions[pin], SkinMatrix(source[sample.A], composed));
            if (direction != Vector3.Zero)
                normals[rig.Pins[pin]] = direction;
        }
    }

    private static Vector3 PosedTriangleDirection(ResolvedClothPoint sample, SkinnedVertex[] source,
        ReadOnlySpan<Matrix4x4> composed, Vector3 direction) =>
        ClothFittedFrame.TryCreate(BoundClothSurface.Skin(source[sample.A], composed),
            BoundClothSurface.Skin(source[sample.B], composed), BoundClothSurface.Skin(source[sample.C], composed),
            out var frame, out _) ? ClothDetailedReconstruction.Directions(frame, direction) : Vector3.Zero;

    private static Matrix4x4 SkinMatrix(in SkinnedVertex vertex, ReadOnlySpan<Matrix4x4> composed) =>
        SkinningMath.BlendSkinMatrix(composed, vertex.BoneIndices, vertex.BoneWeights);
}
