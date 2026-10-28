using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Bins render-relative water boxes into the shared 16 x 9 x 24 cluster grid. The image is a GLSL std430 uvec4 array.
/// All offsets below are uint offsets, so word n is buffer[n / 4][n % 4]. Cluster (x,y,z) is (z * 9 + y) * 16 + x.
/// Words [0,3456) are headers, one uint per cluster. A header is the count from 0 to 32, or 255 on overflow.
/// Overflow means scan every record from 0 to RecordCount - 1. The header has no offset bits or other flags.
/// Words [3456,114048) hold 32 fixed index slots per cluster. Cluster c starts at word 3456 + c * 32.
/// Only its header count of indices is read, in ascending submission order. Overflowed clusters store no indices.
/// Words [114048,163200) hold up to 4096 records, each twelve uints (three uvec4), starting at 114048 + record * 12.
/// Every record field is a float bit pattern read with uintBitsToFloat. Its first uvec4 is (MinX,MinZ,MaxX,MaxZ),
/// its second is (SurfaceY,FloorY,AbsorptionPerMetre.X,AbsorptionPerMetre.Y), and its third is
/// (AbsorptionPerMetre.Z,PenumbraPerMetre,0,0). FloorY equal to float.MinValue encodes an unbounded floor.
/// AbsorptionPerMetre retains its name and offsets, carrying legacy absorption or derived extinction for
/// WaterColumnLight bodies. The direct sun beam uses the packed coefficient in either case.
/// All positions, including FloorY, are already in render-relative metres. Build does not subtract an origin.
/// UsedUIntCount is 114048 + RecordCount * 12 for a nonempty valid build, otherwise zero. The frame record count
/// is supplied separately by the caller. Empty or invalid-grid builds reset every diagnostic and expose no image.
/// ReferenceCount counts stored indices, excluding overflowed clusters. DroppedRecords counts the submitted tail
/// beyond MaxRecords. Image has fixed ImageUIntCount capacity and unused words inside its used prefix are zero.
/// Ranges widen at frame scale, then local plane tolerances and box-axis bounds limit false positives. Only the
/// two end depth faces inherit the fragment query's wider depth tolerance.
/// </summary>
internal sealed class WaterVolumeClusterBuilder
{
    internal const int MaxRecords = 4096;
    internal const int MaxRecordsPerCluster = 32;
    internal const int ClusterCount = ClusterFrameGrid.ClusterCountX * ClusterFrameGrid.ClusterCountY
        * ClusterFrameGrid.ClusterCountZ;
    internal const int HeaderRegionUInts = ClusterCount;
    internal const int IndexRegionUInts = ClusterCount * MaxRecordsPerCluster;
    internal const int RecordRegionUInts = HeaderRegionUInts + IndexRegionUInts;
    internal const int RecordStrideUInts = 12;
    internal const int ImageUIntCount = RecordRegionUInts + MaxRecords * RecordStrideUInts;
    internal const uint OverflowCount = 255u;

    const int CountX = ClusterFrameGrid.ClusterCountX;
    const int CountY = ClusterFrameGrid.ClusterCountY;
    const int CountZ = ClusterFrameGrid.ClusterCountZ;
    const float RangeMarginScale = 1e-3f;
    const float GeometryEpsilonScale = ClusterFrameGrid.GeometryEpsilonScale;

    readonly Plane[] _columnPlanes = new Plane[CountX + 1];
    readonly Plane[] _rowPlanes = new Plane[CountY + 1];
    readonly Candidate[] _candidates = new Candidate[MaxRecords];
    readonly int[] _sliceCandidates = new int[MaxRecords];
    readonly int[] _rowCandidates = new int[MaxRecords];

    internal uint[] Image { get; } = new uint[ImageUIntCount];
    internal int UsedUIntCount { get; private set; }
    internal int RecordCount { get; private set; }
    internal int DroppedRecords { get; private set; }
    internal int OverflowedClusters { get; private set; }
    internal int ReferenceCount { get; private set; }

    internal void Build(ReadOnlySpan<WaterVolumeRecord> records, ClusterFrameGrid grid)
    {
        UsedUIntCount = 0;
        RecordCount = 0;
        DroppedRecords = 0;
        OverflowedClusters = 0;
        ReferenceCount = 0;
        if (!grid.IsValid || records.IsEmpty) return;

        Array.Clear(Image);
        RecordCount = Math.Min(records.Length, MaxRecords);
        DroppedRecords = records.Length - RecordCount;
        UsedUIntCount = RecordRegionUInts + RecordCount * RecordStrideUInts;

        bool tilePlanesValid = PrepareTilePlanes(grid);
        // Range margins include both frame coordinates and the depth span used by the shader's end-slice tolerance.
        float frameScale = MathF.Max(1f, MathF.Max(grid.GeometryScale,
            MathF.Max(MathF.Abs(grid.Depth.X), MathF.Abs(grid.Depth.Y))));
        float margin = RangeMarginScale * frameScale;
        float limit = MathF.Min(float.MaxValue, frameScale + margin);
        int candidateCount = 0;
        Span<Vector3> corners = stackalloc Vector3[8];
        var forward = new Vector3(grid.CameraForward.X, grid.CameraForward.Y, grid.CameraForward.Z);
        for (int record = 0; record < RecordCount; record++)
        {
            WaterVolumeRecord water = records[record];
            WriteRecord(water, record);

            // Only the working box is clipped. The expanded frame cube contains every queried froxel, including
            // shader boundary tolerances. Clipping keeps an unbounded floor or a huge ocean from overflowing corner
            // arithmetic or making the plane epsilon enormous. The packed record retains its exact original bounds.
            Vector3 min = Vector3.Max(new Vector3(water.MinX, water.FloorY, water.MinZ), new Vector3(-limit));
            Vector3 max = Vector3.Min(new Vector3(water.MaxX, water.SurfaceY, water.MaxZ), new Vector3(limit));
            if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z) continue;
            for (int corner = 0; corner < corners.Length; corner++)
                corners[corner] = new Vector3((corner & 1) == 0 ? min.X : max.X,
                    (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
            if (!TryGetRange(corners, grid, forward, margin, tilePlanesValid, out ClusterRange range)) continue;
            _candidates[candidateCount++] = new Candidate(min, max, range, record);
        }
        AssignClusters(grid, candidateCount);
    }

    void WriteRecord(WaterVolumeRecord record, int index)
    {
        Span<uint> words = Image.AsSpan(RecordRegionUInts + index * RecordStrideUInts, RecordStrideUInts);
        words[0] = BitConverter.SingleToUInt32Bits(record.MinX);
        words[1] = BitConverter.SingleToUInt32Bits(record.MinZ);
        words[2] = BitConverter.SingleToUInt32Bits(record.MaxX);
        words[3] = BitConverter.SingleToUInt32Bits(record.MaxZ);
        words[4] = BitConverter.SingleToUInt32Bits(record.SurfaceY);
        words[5] = BitConverter.SingleToUInt32Bits(record.FloorY);
        words[6] = BitConverter.SingleToUInt32Bits(record.AbsorptionPerMetre.X);
        words[7] = BitConverter.SingleToUInt32Bits(record.AbsorptionPerMetre.Y);
        words[8] = BitConverter.SingleToUInt32Bits(record.AbsorptionPerMetre.Z);
        words[9] = BitConverter.SingleToUInt32Bits(record.PenumbraPerMetre);
    }

    bool TryGetRange(ReadOnlySpan<Vector3> corners, ClusterFrameGrid grid, Vector3 forward, float margin,
        bool tilePlanesValid, out ClusterRange range)
    {
        range = ClusterRange.Full;
        float near = float.PositiveInfinity;
        float far = float.NegativeInfinity;
        foreach (Vector3 corner in corners)
        {
            float depth = Vector3.Dot(corner - grid.EyeRender, forward);
            if (!float.IsFinite(depth)) return true;
            near = MathF.Min(near, depth);
            far = MathF.Max(far, depth);
        }
        near -= margin;
        far += margin;
        if (!float.IsFinite(near) || !float.IsFinite(far)) return true;
        ReadOnlySpan<float> slices = grid.SliceDepth;
        if (far < slices[0] || near > slices[CountZ]) return false;
        int minZ = 0;
        while (minZ < CountZ - 1 && slices[minZ + 1] < near) minZ++;
        int maxZ = CountZ - 1;
        while (maxZ > minZ && slices[maxZ] > far) maxZ--;

        // As in point-light ranges, a box reaching the near plane keeps all tiles. Perspective boundaries cross
        // behind the eye. The final froxel plane test can safely reject the excess candidates.
        if (near <= slices[0] || !tilePlanesValid)
        {
            range = new ClusterRange(0, CountX - 1, 0, CountY - 1, minZ, maxZ);
            return true;
        }
        if (!TryGetTileSpan(_columnPlanes, corners, margin, out int minX, out int maxX)
            || !TryGetTileSpan(_rowPlanes, corners, margin, out int minY, out int maxY)) return false;
        range = new ClusterRange(minX, maxX, minY, maxY, minZ, maxZ);
        return true;
    }

    static bool TryGetTileSpan(ReadOnlySpan<Plane> boundaries, ReadOnlySpan<Vector3> corners, float margin,
        out int min, out int max)
    {
        min = -1;
        max = -1;
        for (int tile = 0; tile < boundaries.Length - 1; tile++)
        {
            float lower = float.NegativeInfinity;
            float upper = float.PositiveInfinity;
            foreach (Vector3 corner in corners)
            {
                float lowerDistance = Plane.DotCoordinate(boundaries[tile], corner);
                float upperDistance = Plane.DotCoordinate(boundaries[tile + 1], corner);
                if (!float.IsFinite(lowerDistance) || !float.IsFinite(upperDistance))
                {
                    min = 0;
                    max = boundaries.Length - 2;
                    return true;
                }
                lower = MathF.Max(lower, lowerDistance);
                upper = MathF.Min(upper, upperDistance);
            }
            if (lower < -margin || upper > margin) continue;
            if (min < 0) min = tile;
            max = tile;
        }
        return min >= 0;
    }

    bool PrepareTilePlanes(ClusterFrameGrid grid)
    {
        ReadOnlySpan<Vector3> near = grid.ClipNear;
        ReadOnlySpan<Vector3> far = grid.ClipFar;
        for (int x = 0; x <= CountX; x++)
        {
            int first = ClusterFrameGrid.BoundaryIndex(x, 0);
            int last = ClusterFrameGrid.BoundaryIndex(x, CountY);
            int beside = ClusterFrameGrid.BoundaryIndex(x < CountX ? x + 1 : x - 1, 0);
            if (!TryBoundaryPlane(near[first], far[first], far[last], far[beside], x < CountX,
                    out _columnPlanes[x])) return false;
        }
        for (int y = 0; y <= CountY; y++)
        {
            int first = ClusterFrameGrid.BoundaryIndex(0, y);
            int last = ClusterFrameGrid.BoundaryIndex(CountX, y);
            int beside = ClusterFrameGrid.BoundaryIndex(0, y < CountY ? y + 1 : y - 1);
            if (!TryBoundaryPlane(near[first], far[first], far[last], far[beside], y < CountY,
                    out _rowPlanes[y])) return false;
        }
        return true;
    }

    static bool TryBoundaryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 beside, bool besideIsPositive,
        out Plane plane)
    {
        if (!TryPlane(a, b, c, beside, out plane) || Plane.DotCoordinate(plane, beside) == 0f) return false;
        if (!besideIsPositive) plane = new Plane(-plane.Normal, -plane.D);
        return true;
    }

    void AssignClusters(ClusterFrameGrid grid, int candidateCount)
    {
        for (int z = 0; z < CountZ; z++)
        {
            int sliceCount = 0;
            int minY = CountY;
            int maxY = -1;
            for (int entry = 0; entry < candidateCount; entry++)
            {
                ClusterRange range = _candidates[entry].Range;
                if (z < range.MinZ || z > range.MaxZ) continue;
                _sliceCandidates[sliceCount++] = entry;
                minY = Math.Min(minY, range.MinY);
                maxY = Math.Max(maxY, range.MaxY);
            }
            for (int y = minY; y <= maxY; y++)
            {
                int rowCount = 0;
                int minX = CountX;
                int maxX = -1;
                for (int i = 0; i < sliceCount; i++)
                {
                    int entry = _sliceCandidates[i];
                    ClusterRange range = _candidates[entry].Range;
                    if (y < range.MinY || y > range.MaxY) continue;
                    _rowCandidates[rowCount++] = entry;
                    minX = Math.Min(minX, range.MinX);
                    maxX = Math.Max(maxX, range.MaxX);
                }
                for (int x = minX; x <= maxX; x++) PackCluster(grid, rowCount, x, y, z);
            }
        }
    }

    void PackCluster(ClusterFrameGrid grid, int rowCount, int x, int y, int z)
    {
        int cluster = (z * CountY + y) * CountX + x;
        int first = HeaderRegionUInts + cluster * MaxRecordsPerCluster;
        Span<Plane> planes = stackalloc Plane[6];
        bool planesBuilt = false;
        bool planesValid = false;
        Vector3 minimum = default;
        Vector3 maximum = default;
        float geometryScale = 0f;
        float boundsEpsilon = 0f;
        float depthEpsilon = GeometryEpsilonScale * MathF.Max(1f,
            MathF.Max(MathF.Abs(grid.Depth.X), MathF.Abs(grid.Depth.Y)));
        int count = 0;
        for (int i = 0; i < rowCount; i++)
        {
            Candidate candidate = _candidates[_rowCandidates[i]];
            if (x < candidate.Range.MinX || x > candidate.Range.MaxX) continue;
            if (!planesBuilt)
            {
                planesValid = TryBuildPlanes(grid, x, y, z, depthEpsilon, planes,
                    out minimum, out maximum, out geometryScale);
                boundsEpsilon = GeometryEpsilonScale * MathF.Max(geometryScale,
                    MathF.Max(MaxAbs(minimum), MaxAbs(maximum)));
                planesBuilt = true;
            }
            // A degenerate plane set retains the entire conservative candidate list rather than losing a receiver.
            // Froxel faces alone can accept a tall box using a different corner for each face. Its own three axes
            // rule out distant froxels before that test, keeping unbounded river columns from flooding the lists.
            if (planesValid && (!OverlapsBounds(candidate.Min, candidate.Max, minimum, maximum, boundsEpsilon)
                || !IntersectsBox(planes, candidate.Min, candidate.Max, geometryScale, depthEpsilon, z))) continue;
            if (count == MaxRecordsPerCluster)
            {
                Image[cluster] = OverflowCount;
                Array.Clear(Image, first, count);
                OverflowedClusters++;
                return;
            }
            Image[first + count++] = (uint)candidate.Record;
        }
        Image[cluster] = (uint)count;
        ReferenceCount += count;
    }

    static bool TryBuildPlanes(ClusterFrameGrid grid, int x, int y, int z, float depthEpsilon, Span<Plane> planes,
        out Vector3 minimum, out Vector3 maximum, out float geometryScale)
    {
        float near = grid.SliceDepth[z];
        float far = grid.SliceDepth[z + 1];
        Vector3 n00 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x, y), near);
        Vector3 n10 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x + 1, y), near);
        Vector3 n01 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x, y + 1), near);
        Vector3 n11 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x + 1, y + 1), near);
        Vector3 f00 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x, y), far);
        Vector3 f10 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x + 1, y), far);
        Vector3 f01 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x, y + 1), far);
        Vector3 f11 = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x + 1, y + 1), far);
        Vector3 center = (n00 + n10 + n01 + n11 + f00 + f10 + f01 + f11) * 0.125f;
        minimum = Vector3.Min(Vector3.Min(n00, n10), Vector3.Min(n01, n11));
        minimum = Vector3.Min(minimum, Vector3.Min(Vector3.Min(f00, f10), Vector3.Min(f01, f11)));
        maximum = Vector3.Max(Vector3.Max(n00, n10), Vector3.Max(n01, n11));
        maximum = Vector3.Max(maximum, Vector3.Max(Vector3.Max(f00, f10), Vector3.Max(f01, f11)));
        geometryScale = MathF.Max(1f, MathF.Max(MathF.Max(MaxAbs(minimum), MaxAbs(maximum)),
            MathF.Max(MathF.Abs(near), MathF.Abs(far))));

        // End-slice queries clamp depth. Extend bounds along the actual tile rays, whose lateral components matter
        // in perspective, while keeping plane-rounding tolerances tied to the original froxel's scale.
        if (z == 0 && !TryExpandBounds(grid, x, y, near - depthEpsilon, ref minimum, ref maximum)) return false;
        if (z == CountZ - 1 && !TryExpandBounds(grid, x, y, far + depthEpsilon, ref minimum, ref maximum)) return false;
        return Finite(minimum) && Finite(maximum) && float.IsFinite(geometryScale)
            && TryPlane(n00, f00, f01, center, out planes[0])
            && TryPlane(n10, n11, f11, center, out planes[1])
            && TryPlane(n00, n10, f10, center, out planes[2])
            && TryPlane(n01, f01, f11, center, out planes[3])
            && TryPlane(n00, n01, n11, center, out planes[4])
            && TryPlane(f00, f10, f11, center, out planes[5]);
    }

    static bool TryExpandBounds(ClusterFrameGrid grid, int x, int y, float depth,
        ref Vector3 minimum, ref Vector3 maximum)
    {
        for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx <= 1; dx++)
            {
                Vector3 point = grid.PointAtDepth(ClusterFrameGrid.BoundaryIndex(x + dx, y + dy), depth);
                if (!Finite(point)) return false;
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
        return true;
    }

    static bool TryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, out Plane plane)
    {
        plane = default;
        Vector3 normal = Vector3.Cross(b - a, c - a);
        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-16f) return false;
        normal /= MathF.Sqrt(lengthSquared);
        float distance = -Vector3.Dot(normal, a);
        float side = Vector3.Dot(normal, inside) + distance;
        if (!float.IsFinite(distance) || !float.IsFinite(side)) return false;
        if (side < 0f) { normal = -normal; distance = -distance; }
        plane = new Plane(normal, distance);
        return true;
    }

    static bool OverlapsBounds(Vector3 min, Vector3 max, Vector3 clusterMin, Vector3 clusterMax, float epsilon) =>
        min.X <= clusterMax.X + epsilon && max.X >= clusterMin.X - epsilon
        && min.Y <= clusterMax.Y + epsilon && max.Y >= clusterMin.Y - epsilon
        && min.Z <= clusterMax.Z + epsilon && max.Z >= clusterMin.Z - epsilon;

    static bool IntersectsBox(ReadOnlySpan<Plane> planes, Vector3 min, Vector3 max, float geometryScale,
        float depthEpsilon, int z)
    {
        for (int face = 0; face < planes.Length; face++)
        {
            Plane plane = planes[face];
            // The corner furthest into the inward half-space is the box's support point for this plane.
            Vector3 support = new(plane.Normal.X >= 0f ? max.X : min.X,
                plane.Normal.Y >= 0f ? max.Y : min.Y, plane.Normal.Z >= 0f ? max.Z : min.Z);
            float distance = Plane.DotCoordinate(plane, support);
            // Rounding follows the projected terms, not the box's largest coordinate. An unbounded floor must not
            // widen a plane whose normal has no Y component.
            float magnitude = MathF.Abs(plane.Normal.X * support.X) + MathF.Abs(plane.Normal.Y * support.Y)
                + MathF.Abs(plane.Normal.Z * support.Z) + MathF.Abs(plane.D);
            float epsilon = GeometryEpsilonScale * MathF.Max(geometryScale, magnitude);
            if ((face == 4 && z == 0) || (face == 5 && z == CountZ - 1)) epsilon = MathF.Max(epsilon, depthEpsilon);
            if (float.IsFinite(distance) && distance < -epsilon) return false;
        }
        return true;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static float MaxAbs(Vector3 value) => MathF.Max(MathF.Abs(value.X), MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));

    readonly record struct Candidate(Vector3 Min, Vector3 Max, ClusterRange Range, int Record);

    readonly record struct ClusterRange(int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ)
    {
        internal static ClusterRange Full => new(0, CountX - 1, 0, CountY - 1, 0, CountZ - 1);
    }
}
