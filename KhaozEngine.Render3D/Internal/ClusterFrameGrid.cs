using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Camera-derived cluster boundaries and depth slices, prepared once per frame and shared by its binners.</summary>
internal sealed class ClusterFrameGrid
{
    internal const int ClusterCountX = 16;
    internal const int ClusterCountY = 9;
    internal const int ClusterCountZ = 24;
    internal const float GeometryEpsilonScale = 1e-4f;
    const int BoundaryCount = (ClusterCountX + 1) * (ClusterCountY + 1);

    readonly Vector3[] _clipNear = new Vector3[BoundaryCount];
    readonly Vector3[] _clipFar = new Vector3[BoundaryCount];
    readonly float[] _nearDepth = new float[BoundaryCount];
    readonly float[] _farDepth = new float[BoundaryCount];
    readonly float[] _sliceDepth = new float[ClusterCountZ + 1];

    internal bool IsValid { get; private set; }
    internal Vector4 Depth { get; private set; } = new(0f, 0f, 0f, -1f);
    internal Vector4 CameraForward { get; private set; }
    internal Vector3 EyeRender { get; private set; }
    internal ReadOnlySpan<Vector3> ClipNear => _clipNear;
    internal ReadOnlySpan<Vector3> ClipFar => _clipFar;
    internal ReadOnlySpan<float> SliceDepth => _sliceDepth;
    internal float GeometryScale { get; private set; }

    internal bool TryPrepare(in Matrix4x4 gpuCorrectedRenderViewProjection, Vector3 eyeRender, Vector3 forward,
        in Matrix4x4 projection, Vector3 renderOrigin)
    {
        IsValid = false;
        Depth = new Vector4(0f, 0f, 0f, -1f);
        CameraForward = Vector4.Zero;
        EyeRender = Vector3.Zero;
        GeometryScale = 0f;
        if (!Finite(gpuCorrectedRenderViewProjection) || !Finite(projection) || !Finite(eyeRender) || !Finite(forward)
            || !Finite(renderOrigin))
            return false;

        float forwardLengthSquared = forward.LengthSquared();
        if (!float.IsFinite(forwardLengthSquared) || forwardLengthSquared <= 1e-12f) return false;
        Vector3 cameraForward = forward / MathF.Sqrt(forwardLengthSquared);

        bool perspective;
        const float projectionKindEpsilon = 1e-5f;
        if (MathF.Abs(projection.M44) <= projectionKindEpsilon
            && MathF.Abs(projection.M34) > projectionKindEpsilon)
            perspective = true;
        else if (MathF.Abs(projection.M34) <= projectionKindEpsilon
                 && MathF.Abs(projection.M44) > projectionKindEpsilon)
            perspective = false;
        else
            return false;

        if (!Matrix4x4.Invert(gpuCorrectedRenderViewProjection, out Matrix4x4 inverse) || !Finite(inverse)) return false;
        float minNear = float.PositiveInfinity;
        float maxFar = float.NegativeInfinity;
        for (int y = 0; y <= ClusterCountY; y++)
        {
            float ndcY = -1f + 2f * y / ClusterCountY;
            for (int x = 0; x <= ClusterCountX; x++)
            {
                float ndcX = -1f + 2f * x / ClusterCountX;
                int boundary = BoundaryIndex(x, y);
                if (!TryUnproject(ndcX, ndcY, 0f, inverse, out _clipNear[boundary])
                    || !TryUnproject(ndcX, ndcY, 1f, inverse, out _clipFar[boundary]))
                    return false;

                float nearDepth = Vector3.Dot(_clipNear[boundary] - eyeRender, cameraForward);
                float farDepth = Vector3.Dot(_clipFar[boundary] - eyeRender, cameraForward);
                if (!float.IsFinite(nearDepth) || !float.IsFinite(farDepth)) return false;
                float localScale = MathF.Max(1f, MathF.Max(MathF.Abs(nearDepth), MathF.Abs(farDepth)));
                if (farDepth - nearDepth <= GeometryEpsilonScale * localScale) return false;
                _nearDepth[boundary] = nearDepth;
                _farDepth[boundary] = farDepth;
                minNear = MathF.Min(minNear, nearDepth);
                maxFar = MathF.Max(maxFar, farDepth);
            }
        }

        if (!float.IsFinite(minNear) || !float.IsFinite(maxFar) || maxFar <= minNear) return false;
        if (perspective && minNear <= 0f) return false;
        float logRange = perspective ? MathF.Log(maxFar / minNear) : 0f;
        if (perspective && (!float.IsFinite(logRange) || logRange <= 0f)) return false;

        Depth = new Vector4(minNear, maxFar, logRange, perspective ? 1f : 0f);
        for (int slice = 0; slice <= ClusterCountZ; slice++)
        {
            float fraction = (float)slice / ClusterCountZ;
            _sliceDepth[slice] = perspective
                ? minNear * MathF.Exp(logRange * fraction)
                : minNear + (maxFar - minNear) * fraction;
        }
        _sliceDepth[0] = minNear;
        _sliceDepth[ClusterCountZ] = maxFar;
        CameraForward = new Vector4(cameraForward, 0f);
        EyeRender = eyeRender;
        GeometryScale = FrameGeometryScale();
        IsValid = true;
        return true;
    }

    // The largest coordinate any cluster corner can take this frame. Corners lie on the boundary rays at slice depths
    // inside [minNear, maxFar], and a coordinate is affine in depth along a ray, so its extremes sit at the two ends of
    // that interval. It bounds every cluster's own scale in the exact test's epsilon.
    float FrameGeometryScale()
    {
        float scale = 0f;
        for (int boundary = 0; boundary < BoundaryCount; boundary++)
        {
            scale = MathF.Max(scale, MaxAbs(PointAtDepth(boundary, _sliceDepth[0])));
            scale = MathF.Max(scale, MaxAbs(PointAtDepth(boundary, _sliceDepth[ClusterCountZ])));
        }
        return scale;
    }

    internal Vector3 PointAtDepth(int boundary, float depth)
    {
        float range = _farDepth[boundary] - _nearDepth[boundary];
        float amount = (depth - _nearDepth[boundary]) / range;
        return Vector3.Lerp(_clipNear[boundary], _clipFar[boundary], amount);
    }

    static bool TryUnproject(float x, float y, float z, in Matrix4x4 inverse, out Vector3 point)
    {
        Vector4 homogeneous = Vector4.Transform(new Vector4(x, y, z, 1f), inverse);
        if (!Finite(homogeneous) || MathF.Abs(homogeneous.W) <= 1e-8f)
        {
            point = default;
            return false;
        }
        point = new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
        return Finite(point);
    }

    internal static int BoundaryIndex(int x, int y) => y * (ClusterCountX + 1) + x;

    static float MaxAbs(Vector3 value) =>
        MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    static bool Finite(in Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13)
        && float.IsFinite(value.M14) && float.IsFinite(value.M21) && float.IsFinite(value.M22)
        && float.IsFinite(value.M23) && float.IsFinite(value.M24) && float.IsFinite(value.M31)
        && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43)
        && float.IsFinite(value.M44);
}
