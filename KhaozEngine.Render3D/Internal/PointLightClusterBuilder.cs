using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Builds the compact clustered point-light image consumed by lit receiver shaders: one header uint per
/// cluster, then each cluster's light indices, contiguous and in ascending submitted order. Each light first gets a
/// conservative cluster range from <see cref="PointLightClusterRanges"/>, and each cluster runs the exact plane test on
/// only the lights whose range holds it (issue #1112).</summary>
internal sealed class PointLightClusterBuilder
{
    internal const int ClusterCountX = ClusterFrameGrid.ClusterCountX;
    internal const int ClusterCountY = ClusterFrameGrid.ClusterCountY;
    internal const int ClusterCountZ = ClusterFrameGrid.ClusterCountZ;
    internal const int MaxLightsPerCluster = 64;
    internal const int ClusterCount = ClusterCountX * ClusterCountY * ClusterCountZ;

    /// <summary>The image keeps the size of the fixed layout it replaced, seventeen uvec4 per cluster or 940,032 bytes,
    /// so the buffer, its binding, its stride and every size pin stay as they were.</summary>
    internal const int ImageUIntCount = ClusterCount * 17 * 4;

    /// <summary>One header uint per cluster at the start of the image, four to a uvec4.</summary>
    internal const int HeaderRegionUInts = ClusterCount;
    internal const int HeaderRegionUvec4s = HeaderRegionUInts / 4;

    /// <summary>Everything after the headers holds light indices, four to a uvec4. It is at least
    /// <see cref="MaxLightsPerCluster"/> for every cluster, so a full grid can never run it out.</summary>
    internal const int IndexRegionUInts = ImageUIntCount - HeaderRegionUInts;

    /// <summary>A header's low <see cref="CountBits"/> bits hold the cluster's light count and the high bits hold the
    /// offset of its first index in the index region.</summary>
    internal const int CountBits = 8;
    internal const uint CountMask = (1u << CountBits) - 1u;

    /// <summary>The count of a cluster that passed more than <see cref="MaxLightsPerCluster"/> lights. It stores no
    /// indices and its fragments walk the complete list.</summary>
    internal const uint OverflowCount = CountMask;

    const float GeometryEpsilonScale = ClusterFrameGrid.GeometryEpsilonScale;

    readonly PointLightClusterRanges _ranges = new();
    int[] _sliceCandidates = new int[16];
    int[] _rowCandidates = new int[16];

    internal PointLightClusterBuilder()
    {
        Image = new uint[ImageUIntCount];
    }

    internal uint[] Image { get; }
    internal Vector4 Depth { get; private set; }
    internal Vector4 CameraForward { get; private set; }
    internal int OverflowedClusters { get; private set; }
    internal int LightReferenceCount { get; private set; }

    /// <summary>Cluster plane sets the latest build made. Tests read it to prove a cluster no light's range reaches is
    /// never built.</summary>
    internal int PlaneSetsBuilt { get; private set; }

    /// <summary>Exact sphere tests the latest build ran, counted for the same reason.</summary>
    internal int SphereTests { get; private set; }

    /// <summary>How many uints of <see cref="Image"/> the latest build wrote, the headers plus the stored indices rounded
    /// up to a whole uvec4. Only this prefix is uploaded.</summary>
    internal int UsedUIntCount { get; private set; }

    int _indexCursor;

    internal void Build(ReadOnlySpan<ModelRenderer.PointLightData> lights, ClusterFrameGrid grid,
        Vector3 renderOrigin)
    {
        // Only the headers are cleared. Indices are written behind a cursor and the upload stops at it, so whatever an
        // earlier frame left past the cursor is never uploaded and never read.
        Array.Clear(Image, 0, HeaderRegionUInts);
        _indexCursor = 0;
        OverflowedClusters = 0;
        LightReferenceCount = 0;
        PlaneSetsBuilt = 0;
        SphereTests = 0;
        if (!grid.IsValid)
        {
            MarkInvalid();
            return;
        }

        Depth = grid.Depth;
        CameraForward = grid.CameraForward;
        var cameraForward = new Vector3(CameraForward.X, CameraForward.Y, CameraForward.Z);
        _ranges.Gather(lights, renderOrigin, grid.EyeRender, cameraForward, grid.ClipNear, grid.ClipFar,
            grid.SliceDepth, grid.GeometryScale);
        if (!AssignClusters(lights, grid, renderOrigin))
        {
            MarkInvalid();
            return;
        }

        // Round up to a whole uvec4 and zero the pad, so the upload is whole elements and the image is deterministic.
        int used = HeaderRegionUInts + _indexCursor;
        while ((used & 3) != 0) Image[used++] = 0u;
        UsedUIntCount = used;
    }

    // Visits clusters in index order, z then y then x, testing each against only the lights whose range holds it. The
    // candidate lists narrow per slice and then per row but never reorder, so every cluster still sees its lights in
    // ascending submitted order.
    bool AssignClusters(ReadOnlySpan<ModelRenderer.PointLightData> lights, ClusterFrameGrid grid,
        Vector3 renderOrigin)
    {
        ReadOnlySpan<int> survivors = _ranges.Lights;
        ReadOnlySpan<PointLightClusterRanges.ClusterRange> ranges = _ranges.Ranges;
        EnsureCandidateCapacity(survivors.Length);
        for (int z = _ranges.MinSlice; z <= _ranges.MaxSlice; z++)
        {
            int sliceCount = 0;
            int minY = ClusterCountY;
            int maxY = -1;
            for (int entry = 0; entry < survivors.Length; entry++)
            {
                if (!ranges[entry].ContainsSlice(z)) continue;
                _sliceCandidates[sliceCount++] = entry;
                minY = Math.Min(minY, ranges[entry].MinY);
                maxY = Math.Max(maxY, ranges[entry].MaxY);
            }
            for (int y = minY; y <= maxY; y++)
            {
                int rowCount = 0;
                int minX = ClusterCountX;
                int maxX = -1;
                for (int i = 0; i < sliceCount; i++)
                {
                    int entry = _sliceCandidates[i];
                    if (!ranges[entry].ContainsRow(y)) continue;
                    _rowCandidates[rowCount++] = entry;
                    minX = Math.Min(minX, ranges[entry].MinX);
                    maxX = Math.Max(maxX, ranges[entry].MaxX);
                }
                for (int x = minX; x <= maxX; x++)
                    if (!PackCluster(lights, grid, renderOrigin, survivors, ranges, rowCount, x, y, z)) return false;
            }
        }
        return true;
    }

    static bool TryBuildPlanes(ClusterFrameGrid grid, int x, int y, float depthNear, float depthFar,
        out ClusterPlanes planes, out float clusterScale)
    {
        planes = default;
        Vector3 n00 = grid.PointAtDepth(BoundaryIndex(x, y), depthNear);
        Vector3 n10 = grid.PointAtDepth(BoundaryIndex(x + 1, y), depthNear);
        Vector3 n01 = grid.PointAtDepth(BoundaryIndex(x, y + 1), depthNear);
        Vector3 n11 = grid.PointAtDepth(BoundaryIndex(x + 1, y + 1), depthNear);
        Vector3 f00 = grid.PointAtDepth(BoundaryIndex(x, y), depthFar);
        Vector3 f10 = grid.PointAtDepth(BoundaryIndex(x + 1, y), depthFar);
        Vector3 f01 = grid.PointAtDepth(BoundaryIndex(x, y + 1), depthFar);
        Vector3 f11 = grid.PointAtDepth(BoundaryIndex(x + 1, y + 1), depthFar);
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

    // The exact test is unchanged. Only its candidates changed: the lights whose range holds this cluster, still in
    // ascending submitted order, so the first 64 that pass and the overflow point are the same as testing all of them,
    // apart from the slice-zero rounding case PointLightClusterRanges describes, which adds no light.
    // The planes are built at the first candidate, so a cluster no light's range reaches is never built.
    bool PackCluster(ReadOnlySpan<ModelRenderer.PointLightData> lights, ClusterFrameGrid grid, Vector3 renderOrigin,
        ReadOnlySpan<int> survivors, ReadOnlySpan<PointLightClusterRanges.ClusterRange> ranges, int rowCount,
        int x, int y, int z)
    {
        int cluster = (z * ClusterCountY + y) * ClusterCountX + x;
        int first = HeaderRegionUInts + _indexCursor;
        ClusterPlanes planes = default;
        float clusterScale = 0f;
        bool planesBuilt = false;
        bool overflow = false;
        int count = 0;
        for (int i = 0; i < rowCount; i++)
        {
            int entry = _rowCandidates[i];
            if (!ranges[entry].ContainsColumn(x)) continue;
            if (!planesBuilt)
            {
                if (!TryBuildPlanes(grid, x, y, grid.SliceDepth[z], grid.SliceDepth[z + 1], out planes, out clusterScale))
                    return false;
                planesBuilt = true;
                PlaneSetsBuilt++;
            }
            int light = survivors[entry];
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            float epsilon = GeometryEpsilonScale * MathF.Max(1f,
                MathF.Max(clusterScale, MathF.Max(MaxAbs(center), radius)));
            SphereTests++;
            if (!planes.IntersectsSphere(center, radius, epsilon)) continue;
            if (count == MaxLightsPerCluster)
            {
                overflow = true;
                break;
            }
            Image[first + count] = (uint)light;
            count++;
        }

        // The diagnostic keeps its meaning: an overflowed cluster still counts the 64 references it accepted first.
        LightReferenceCount += count;
        if (overflow)
        {
            // An overflowed cluster stores no indices. Its tentative ones sit past the cursor and the next cluster writes
            // over them.
            Image[cluster] = OverflowCount;
            OverflowedClusters++;
        }
        else if (count > 0)
        {
            Image[cluster] = ((uint)_indexCursor << CountBits) | (uint)count;
            _indexCursor += count;
        }
        return true;
    }

    void EnsureCandidateCapacity(int count)
    {
        if (_sliceCandidates.Length >= count) return;
        int capacity = Math.Max(count, _sliceCandidates.Length * 2);
        _sliceCandidates = new int[capacity];
        _rowCandidates = new int[capacity];
    }

    void MarkInvalid()
    {
        Array.Fill(Image, OverflowCount, 0, HeaderRegionUInts);
        _indexCursor = 0;
        UsedUIntCount = HeaderRegionUInts;
        Depth = new Vector4(0f, 0f, 0f, -1f);
        CameraForward = Vector4.Zero;
        OverflowedClusters = ClusterCount;
        LightReferenceCount = 0;
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

    internal static int BoundaryIndex(int x, int y) => ClusterFrameGrid.BoundaryIndex(x, y);

    internal static float MaxAbs(Vector3 value) =>
        MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    static float MaxAbs(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Vector3 e, Vector3 f, Vector3 g, Vector3 h) =>
        MathF.Max(MathF.Max(MathF.Max(MaxAbs(a), MaxAbs(b)), MathF.Max(MaxAbs(c), MaxAbs(d))),
            MathF.Max(MathF.Max(MaxAbs(e), MaxAbs(f)), MathF.Max(MaxAbs(g), MaxAbs(h))));

    internal static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

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
