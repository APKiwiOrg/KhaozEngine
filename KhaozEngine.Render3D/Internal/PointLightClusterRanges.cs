using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The conservative cluster range of every point light in one frame (issue #1112), gathered before any cluster is
/// visited. A range holds every cluster whose exact plane test in <see cref="PointLightClusterBuilder"/> could accept the
/// light, so testing only those clusters assigns exactly what testing all of them did. The exception is that test's
/// own float rounding in slice-zero planes. Those clusters are the frame's smallest, millimetres to centimetres across,
/// and are built from corners whose rounding grows with the eye's distance from render-space zero, so their planes can
/// tilt enough to admit a light that does not reach the cluster in exact geometry. Such a light is dropped here. It
/// adds no light to that cluster, so lighting is identical. A light whose range is empty could pass no cluster's exact
/// test, apart from that exception, and is culled here.
/// </summary>
internal sealed class PointLightClusterRanges
{
    /// <summary>
    /// How far every range test is widened, as a fraction of the largest of one, the frame's geometry scale, the light's
    /// largest centre coordinate and its radius. That is ten times the builder's exact-test epsilon over the same scale,
    /// so the widening covers the epsilon plus ordinary rounding between these boundary planes and the planes each
    /// cluster builds from its own corners. It does not cover a slice-zero plane that rounding tilts far enough to
    /// admit a light that is not there in exact geometry, and it is deliberately not raised to chase one, because such
    /// a light adds no light.
    /// </summary>
    internal const float MarginScale = 1e-3f;

    const int CountX = PointLightClusterBuilder.ClusterCountX;
    const int CountY = PointLightClusterBuilder.ClusterCountY;
    const int CountZ = PointLightClusterBuilder.ClusterCountZ;

    readonly Plane[] _columnPlanes = new Plane[CountX + 1];
    readonly Plane[] _rowPlanes = new Plane[CountY + 1];
    int[] _lights = new int[16];
    ClusterRange[] _ranges = new ClusterRange[16];
    bool _tilePlanesValid;
    float _geometryScale;

    /// <summary>Submitted indices of the lights that survived the cull, in ascending order.</summary>
    internal ReadOnlySpan<int> Lights => new(_lights, 0, Count);

    /// <summary>The range of each surviving light, parallel to <see cref="Lights"/>.</summary>
    internal ReadOnlySpan<ClusterRange> Ranges => new(_ranges, 0, Count);

    internal int Count { get; private set; }

    /// <summary>The lowest slice any surviving range reaches, or the slice count when none survived.</summary>
    internal int MinSlice { get; private set; }

    /// <summary>The highest slice any surviving range reaches, or -1 when none survived.</summary>
    internal int MaxSlice { get; private set; }

    internal void Gather(ReadOnlySpan<ModelRenderer.PointLightData> lights, Vector3 renderOrigin, Vector3 eye,
        Vector3 cameraForward, ReadOnlySpan<Vector3> clipNear, ReadOnlySpan<Vector3> clipFar,
        ReadOnlySpan<float> sliceDepth, float geometryScale)
    {
        Count = 0;
        MinSlice = CountZ;
        MaxSlice = -1;
        _geometryScale = geometryScale;
        _tilePlanesValid = PrepareTilePlanes(clipNear, clipFar);
        EnsureCapacity(lights.Length);
        for (int light = 0; light < lights.Length; light++)
        {
            Vector4 posRadius = lights[light].PosRadius;
            var center = new Vector3(posRadius.X, posRadius.Y, posRadius.Z) - renderOrigin;
            float radius = posRadius.W;
            // The builder's own rejection. A light it would skip in every cluster never gets a range.
            if (!PointLightClusterBuilder.Finite(center) || !float.IsFinite(radius) || radius < 0f) continue;
            if (!TryGetRange(center, radius, eye, cameraForward, sliceDepth, out ClusterRange range)) continue;
            _lights[Count] = light;
            _ranges[Count] = range;
            Count++;
            MinSlice = Math.Min(MinSlice, range.MinZ);
            MaxSlice = Math.Max(MaxSlice, range.MaxZ);
        }
    }

    bool TryGetRange(Vector3 center, float radius, Vector3 eye, Vector3 cameraForward, ReadOnlySpan<float> sliceDepth,
        out ClusterRange range)
    {
        range = ClusterRange.Full;
        float margin = MarginScale * MathF.Max(1f,
            MathF.Max(_geometryScale, MathF.Max(PointLightClusterBuilder.MaxAbs(center), radius)));
        float reach = radius + margin;
        float depth = Vector3.Dot(center - eye, cameraForward);
        float nearReach = depth - reach;
        float farReach = depth + reach;
        // Arithmetic that overflowed bounds nothing. The light keeps every cluster and the exact test decides, as it did
        // for every light before ranges existed.
        if (!float.IsFinite(nearReach) || !float.IsFinite(farReach)) return true;

        // In exact geometry every cluster's near and far faces lie at constant view depth, so a light outside this
        // widened depth span reaches no cluster. The brute force can still admit one into slice zero when rounding
        // tilts that slice's planes, and dropping it adds no light.
        if (farReach < sliceDepth[0] || nearReach > sliceDepth[CountZ]) return false;
        int minZ = 0;
        while (minZ < CountZ - 1 && sliceDepth[minZ + 1] < nearReach) minZ++;
        int maxZ = CountZ - 1;
        while (maxZ > minZ && sliceDepth[maxZ] > farReach) maxZ--;

        // A sphere that reaches the near plane takes every tile. Behind the eye a column's side planes cross, and the
        // exact test accepts some spheres there that lie wholly outside the frustum. The span below applies the same
        // side-plane pairs as that test, but keeping every tile for these lights is the conservative choice and needs
        // no argument about how the crossed planes behave.
        if (nearReach <= sliceDepth[0] || !_tilePlanesValid)
        {
            range = new ClusterRange(0, CountX - 1, 0, CountY - 1, minZ, maxZ);
            return true;
        }
        if (!TryGetTileSpan(_columnPlanes, center, reach, out int minX, out int maxX)
            || !TryGetTileSpan(_rowPlanes, center, reach, out int minY, out int maxY))
            return false;
        range = new ClusterRange(minX, maxX, minY, maxY, minZ, maxZ);
        return true;
    }

    // A tile column (or row) can hold the sphere only if it reaches the inner side of both boundary planes of that tile,
    // which are the two side planes every cluster in it tests. The span runs from the first such tile to the last.
    static bool TryGetTileSpan(Plane[] boundaries, Vector3 center, float reach, out int min, out int max)
    {
        int last = boundaries.Length - 2;
        min = -1;
        max = -1;
        float lower = Plane.DotCoordinate(boundaries[0], center);
        for (int tile = 0; tile <= last; tile++)
        {
            float upper = Plane.DotCoordinate(boundaries[tile + 1], center);
            if (!float.IsFinite(lower) || !float.IsFinite(upper))
            {
                min = 0;
                max = last;
                return true;
            }
            if (lower >= -reach && upper <= reach)
            {
                if (min < 0) min = tile;
                max = tile;
            }
            lower = upper;
        }
        return min >= 0;
    }

    // One plane per tile boundary, through the corner rays the builder unprojected for it, oriented so the next tile
    // along lies on its positive side. Every cluster's side plane on that boundary is built from points on the same
    // rays, so the two agree up to rounding. The margin covers that rounding on the seeded equivalence scenes, with the
    // same slice-zero exception the class summary describes, rather than by proof. A failure here gives every light the
    // full tile range, which is always safe.
    bool PrepareTilePlanes(ReadOnlySpan<Vector3> clipNear, ReadOnlySpan<Vector3> clipFar)
    {
        for (int x = 0; x <= CountX; x++)
        {
            int first = PointLightClusterBuilder.BoundaryIndex(x, 0);
            int last = PointLightClusterBuilder.BoundaryIndex(x, CountY);
            int beside = PointLightClusterBuilder.BoundaryIndex(x < CountX ? x + 1 : x - 1, 0);
            if (!TryBoundaryPlane(clipNear[first], clipFar[first], clipFar[last], clipFar[beside], x < CountX,
                    out _columnPlanes[x]))
                return false;
        }
        for (int y = 0; y <= CountY; y++)
        {
            int first = PointLightClusterBuilder.BoundaryIndex(0, y);
            int last = PointLightClusterBuilder.BoundaryIndex(CountX, y);
            int beside = PointLightClusterBuilder.BoundaryIndex(0, y < CountY ? y + 1 : y - 1);
            if (!TryBoundaryPlane(clipNear[first], clipFar[first], clipFar[last], clipFar[beside], y < CountY,
                    out _rowPlanes[y]))
                return false;
        }
        return true;
    }

    static bool TryBoundaryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 beside, bool besideIsPositive,
        out Plane plane)
    {
        plane = default;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-16f) return false;
        normal /= MathF.Sqrt(lengthSquared);
        float distance = -Vector3.Dot(normal, a);
        float side = Vector3.Dot(normal, beside) + distance;
        if (!float.IsFinite(distance) || !float.IsFinite(side) || side == 0f) return false;
        if (side < 0f == besideIsPositive)
        {
            normal = -normal;
            distance = -distance;
        }
        plane = new Plane(normal, distance);
        return true;
    }

    void EnsureCapacity(int count)
    {
        if (_lights.Length >= count) return;
        int capacity = Math.Max(count, _lights.Length * 2);
        _lights = new int[capacity];
        _ranges = new ClusterRange[capacity];
    }

    /// <summary>An inclusive range of tile columns, tile rows and depth slices.</summary>
    internal readonly struct ClusterRange(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        internal static ClusterRange Full => new(0, CountX - 1, 0, CountY - 1, 0, CountZ - 1);

        internal int MinX { get; } = minX;
        internal int MaxX { get; } = maxX;
        internal int MinY { get; } = minY;
        internal int MaxY { get; } = maxY;
        internal int MinZ { get; } = minZ;
        internal int MaxZ { get; } = maxZ;

        internal bool ContainsColumn(int x) => x >= MinX && x <= MaxX;
        internal bool ContainsRow(int y) => y >= MinY && y <= MaxY;
        internal bool ContainsSlice(int z) => z >= MinZ && z <= MaxZ;
    }
}
