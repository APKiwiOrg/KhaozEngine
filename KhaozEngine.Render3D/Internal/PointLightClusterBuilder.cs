using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Builds the fixed clustered point-light index image consumed by lit receiver shaders.</summary>
internal sealed class PointLightClusterBuilder
{
    internal const int ClusterCountX = 16;
    internal const int ClusterCountY = 9;
    internal const int ClusterCountZ = 24;
    internal const int MaxLightsPerCluster = 64;
    internal const int HeaderUInts = 4;
    internal const int ClusterStrideUInts = HeaderUInts + MaxLightsPerCluster;
    internal const int ClusterCount = ClusterCountX * ClusterCountY * ClusterCountZ;
    internal const int ImageUIntCount = ClusterCount * ClusterStrideUInts;

    const int BoundaryCount = (ClusterCountX + 1) * (ClusterCountY + 1);
    const float GeometryEpsilonScale = 1e-4f;

    readonly Vector3[] _clipNear = new Vector3[BoundaryCount];
    readonly Vector3[] _clipFar = new Vector3[BoundaryCount];
    readonly float[] _nearDepth = new float[BoundaryCount];
    readonly float[] _farDepth = new float[BoundaryCount];
    readonly float[] _sliceDepth = new float[ClusterCountZ + 1];

    internal PointLightClusterBuilder()
    {
        Image = new uint[ImageUIntCount];
    }

    internal uint[] Image { get; }
    internal Vector4 Depth { get; private set; }
    internal Vector4 CameraForward { get; private set; }
    internal int OverflowedClusters { get; private set; }
    internal int LightReferenceCount { get; private set; }

    internal void Build(ReadOnlySpan<ModelRenderer.PointLightData> lights,
        Matrix4x4 gpuCorrectedRenderViewProjection, Vector3 eyeRender, Vector3 forward,
        Matrix4x4 projection, Vector3 renderOrigin)
    {
        Array.Clear(Image);
        OverflowedClusters = 0;
        LightReferenceCount = 0;
        if (!TryPrepare(gpuCorrectedRenderViewProjection, eyeRender, forward, projection, renderOrigin,
                out Vector3 cameraForward))
        {
            MarkInvalid();
            return;
        }

        CameraForward = new Vector4(cameraForward, 0f);
        for (int z = 0; z < ClusterCountZ; z++)
        {
            float depthNear = _sliceDepth[z];
            float depthFar = _sliceDepth[z + 1];
            for (int y = 0; y < ClusterCountY; y++)
            {
                for (int x = 0; x < ClusterCountX; x++)
                {
                    if (!TryBuildPlanes(x, y, depthNear, depthFar, out ClusterPlanes planes,
                            out float clusterScale))
                    {
                        MarkInvalid();
                        return;
                    }
                    PackCluster(lights, renderOrigin, x, y, z, in planes, clusterScale);
                }
            }
        }
    }

    bool TryPrepare(in Matrix4x4 viewProjection, Vector3 eye, Vector3 forward, in Matrix4x4 projection,
        Vector3 renderOrigin, out Vector3 cameraForward)
    {
        cameraForward = default;
        if (!Finite(viewProjection) || !Finite(projection) || !Finite(eye) || !Finite(forward)
            || !Finite(renderOrigin))
            return false;

        float forwardLengthSquared = forward.LengthSquared();
        if (!float.IsFinite(forwardLengthSquared) || forwardLengthSquared <= 1e-12f) return false;
        cameraForward = forward / MathF.Sqrt(forwardLengthSquared);

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

        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse) || !Finite(inverse)) return false;
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

                float nearDepth = Vector3.Dot(_clipNear[boundary] - eye, cameraForward);
                float farDepth = Vector3.Dot(_clipFar[boundary] - eye, cameraForward);
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
        return true;
    }

    bool TryBuildPlanes(int x, int y, float depthNear, float depthFar, out ClusterPlanes planes,
        out float clusterScale)
    {
        planes = default;
        Vector3 n00 = PointAtDepth(BoundaryIndex(x, y), depthNear);
        Vector3 n10 = PointAtDepth(BoundaryIndex(x + 1, y), depthNear);
        Vector3 n01 = PointAtDepth(BoundaryIndex(x, y + 1), depthNear);
        Vector3 n11 = PointAtDepth(BoundaryIndex(x + 1, y + 1), depthNear);
        Vector3 f00 = PointAtDepth(BoundaryIndex(x, y), depthFar);
        Vector3 f10 = PointAtDepth(BoundaryIndex(x + 1, y), depthFar);
        Vector3 f01 = PointAtDepth(BoundaryIndex(x, y + 1), depthFar);
        Vector3 f11 = PointAtDepth(BoundaryIndex(x + 1, y + 1), depthFar);
        Vector3 center = (n00 + n10 + n01 + n11 + f00 + f10 + f01 + f11) * 0.125f;
        clusterScale = MaxAbs(n00, n10, n01, n11, f00, f10, f01, f11);

        bool valid = TryPlane(n00, f00, f01, center, out planes.Left)
            && TryPlane(n10, n11, f11, center, out planes.Right)
            && TryPlane(n00, n10, f10, center, out planes.Bottom)
            && TryPlane(n01, f01, f11, center, out planes.Top)
            && TryPlane(n00, n01, n11, center, out planes.Near)
            && TryPlane(f00, f10, f11, center, out planes.Far);
        return valid && float.IsFinite(clusterScale);
    }

    void PackCluster(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin,
        int x, int y, int z, in ClusterPlanes planes, float clusterScale)
    {
        int offset = ((z * ClusterCountY + y) * ClusterCountX + x) * ClusterStrideUInts;
        int count = 0;
        for (int light = 0; light < lights.Length; light++)
        {
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            if (!Finite(center) || !float.IsFinite(radius) || radius < 0f) continue;
            float epsilon = GeometryEpsilonScale * MathF.Max(1f,
                MathF.Max(clusterScale, MathF.Max(MaxAbs(center), radius)));
            if (!planes.IntersectsSphere(center, radius, epsilon)) continue;
            if (count == MaxLightsPerCluster)
            {
                Image[offset + 1] = 1u;
                OverflowedClusters++;
                break;
            }
            Image[offset + HeaderUInts + count] = (uint)light;
            count++;
        }
        Image[offset] = (uint)count;
        LightReferenceCount += count;
    }

    Vector3 PointAtDepth(int boundary, float depth)
    {
        float range = _farDepth[boundary] - _nearDepth[boundary];
        float amount = (depth - _nearDepth[boundary]) / range;
        return Vector3.Lerp(_clipNear[boundary], _clipFar[boundary], amount);
    }

    void MarkInvalid()
    {
        Array.Clear(Image);
        Depth = new Vector4(0f, 0f, 0f, -1f);
        CameraForward = Vector4.Zero;
        OverflowedClusters = ClusterCount;
        LightReferenceCount = 0;
        for (int cluster = 0; cluster < ClusterCount; cluster++)
            Image[cluster * ClusterStrideUInts + 1] = 1u;
    }

    static bool TryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, out Plane plane)
    {
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-16f)
        {
            plane = default;
            return false;
        }
        normal /= MathF.Sqrt(lengthSquared);
        float distance = -Vector3.Dot(normal, a);
        if (!float.IsFinite(distance))
        {
            plane = default;
            return false;
        }
        if (Vector3.Dot(normal, inside) + distance < 0f)
        {
            normal = -normal;
            distance = -distance;
        }
        plane = new Plane(normal, distance);
        return true;
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

    static int BoundaryIndex(int x, int y) => y * (ClusterCountX + 1) + x;

    static float MaxAbs(Vector3 value) =>
        MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    static float MaxAbs(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector3 e, Vector3 f, Vector3 g, Vector3 h) =>
        MathF.Max(MathF.Max(MathF.Max(MaxAbs(a), MaxAbs(b)), MathF.Max(MaxAbs(c), MaxAbs(d))),
            MathF.Max(MathF.Max(MaxAbs(e), MaxAbs(f)), MathF.Max(MaxAbs(g), MaxAbs(h))));

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

    struct ClusterPlanes
    {
        public Plane Left;
        public Plane Right;
        public Plane Bottom;
        public Plane Top;
        public Plane Near;
        public Plane Far;

        public readonly bool IntersectsSphere(Vector3 center, float radius, float epsilon) =>
            Left.Includes(center, radius, epsilon) && Right.Includes(center, radius, epsilon)
            && Bottom.Includes(center, radius, epsilon) && Top.Includes(center, radius, epsilon)
            && Near.Includes(center, radius, epsilon) && Far.Includes(center, radius, epsilon);
    }

    readonly struct Plane(Vector3 normal, float distance)
    {
        public bool Includes(Vector3 center, float radius, float epsilon) =>
            Vector3.Dot(normal, center) + distance + radius + epsilon >= 0f;
    }
}
