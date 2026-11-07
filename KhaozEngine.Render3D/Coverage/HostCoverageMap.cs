using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>A contiguous run of source triangles, <c>[FirstTriangle, FirstTriangle + Count)</c>, in one imported glTF
/// mesh primitive. Mesh, primitive and triangle are source ordinals, not names.</summary>
public readonly record struct CoverageTriangleRange(int Mesh, int Primitive, int FirstTriangle, int Count);

/// <summary>Maps one source render vertex to its welded logical surface vertex. Several render vertices split by a
/// material, UV or normal seam share one <see cref="SurfaceId"/>.</summary>
public readonly record struct CoverageTopologyEntry(int Mesh, int Primitive, int Vertex, int SurfaceId);

/// <summary>Whether a region triangle must stay fully behind equipment (core) or may deform with bounded weight
/// (collar).</summary>
public enum CoverageTriangleRole
{
    Core = 1,
    Collar = 2,
}

public readonly record struct HostCoverageRegionTriangles(CoverageTriangleRange Triangles, CoverageTriangleRole Role);

/// <summary>Authored region strength of one welded surface vertex, finite in <c>[0,1]</c>.</summary>
public readonly record struct CoverageSurfaceStrength(int SurfaceId, float Strength);

/// <summary>A fixed render subdivision level, 1 to 4, for a run of source triangles.</summary>
public readonly record struct HostCoverageRefinement(CoverageTriangleRange Triangles, int Level);

/// <summary>Exporter identity and source revision label. Diagnostics only, never identity proof.</summary>
public readonly record struct HostCoverageAuthoring(string Exporter, string Revision);

/// <summary>One authored coverage region of a host map. Its key is compared ordinally. Triangle ranges are sorted by
/// source ordinal and disjoint, strengths are sorted by surface ID and unique. Collections are owned snapshots.</summary>
public sealed class HostCoverageRegion
{
    public HostCoverageRegion(string key, IEnumerable<HostCoverageRegionTriangles> triangles, IEnumerable<CoverageSurfaceStrength> strengths)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(strengths);
        if (!HostCoverageMap.IsWellFormedLabel(key))
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidLabel, "A region key must be non-blank, well-formed UTF-16.")
            { Path = $"$.key", RegionKey = key };
        string context = $"Region '{key}'";

        HostCoverageRegionTriangles[] ownedTriangles = CoverageCollections.Snapshot(triangles, HostCoverageMap.MaxTriangles, count =>
            new HostCoverageDataException(HostCoverageRefusal.RegionTriangleCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.triangles", RegionKey = key, Count = count, Limit = HostCoverageMap.MaxTriangles });
        if (ownedTriangles.Length == 0)
            throw new HostCoverageDataException(HostCoverageRefusal.RegionTriangleCapacity, $"{context}: at least one triangle range is required.")
            { Path = $"$.triangles", RegionKey = key, Count = ownedTriangles.Length, Limit = HostCoverageMap.MaxTriangles };
        for (int index = 0; index < ownedTriangles.Length; index++)
        {
            HostCoverageRegionTriangles entry = ownedTriangles[index];
            HostCoverageMap.ValidateRange(entry.Triangles, context, $"$.triangles[{index}]", key);
            if (entry.Role is not (CoverageTriangleRole.Core or CoverageTriangleRole.Collar))
                throw new HostCoverageDataException(HostCoverageRefusal.InvalidRole, $"{context}: unsupported triangle role {(int)entry.Role}.")
                { Path = $"$.triangles[{index}].role", RegionKey = key };
        }
        Array.Sort(ownedTriangles, (left, right) => HostCoverageMap.Compare(left.Triangles, right.Triangles));
        HostCoverageMap.RefuseOverlap(ownedTriangles.Select(entry => entry.Triangles), context, "$.triangles", key);
        TriangleCount = ownedTriangles.Sum(entry => (long)entry.Triangles.Count);
        if (TriangleCount > HostCoverageMap.MaxTriangles)
            throw new HostCoverageDataException(HostCoverageRefusal.RegionTriangleTotalCapacity, $"{context}: more than {HostCoverageMap.MaxTriangles} triangles.")
            { Path = $"$.triangles", RegionKey = key, Count = TriangleCount, Limit = HostCoverageMap.MaxTriangles };

        CoverageSurfaceStrength[] ownedStrengths = CoverageCollections.Snapshot(strengths, HostCoverageMap.MaxSurfaceVertices, count =>
            new HostCoverageDataException(HostCoverageRefusal.StrengthCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.strengths", RegionKey = key, Count = count, Limit = HostCoverageMap.MaxSurfaceVertices });
        if (ownedStrengths.Length == 0)
            throw new HostCoverageDataException(HostCoverageRefusal.StrengthCapacity, $"{context}: at least one surface strength is required.")
            { Path = $"$.strengths", RegionKey = key, Count = ownedStrengths.Length, Limit = HostCoverageMap.MaxSurfaceVertices };
        var strengthIds = new HashSet<int>();
        for (int index = 0; index < ownedStrengths.Length; index++)
        {
            CoverageSurfaceStrength entry = ownedStrengths[index];
            if (entry.SurfaceId < 0)
                throw new HostCoverageDataException(HostCoverageRefusal.InvalidSurfaceId, $"{context}: surface IDs must be non-negative.")
                { Path = $"$.strengths[{index}].surfaceId", RegionKey = key, SurfaceId = entry.SurfaceId };
            if (!float.IsFinite(entry.Strength) || entry.Strength < 0f || entry.Strength > 1f)
                throw new HostCoverageDataException(HostCoverageRefusal.InvalidStrength, $"{context}: surface {entry.SurfaceId} strength must be finite in [0,1].")
                { Path = $"$.strengths[{index}].strength", RegionKey = key, SurfaceId = entry.SurfaceId };
            if (!strengthIds.Add(entry.SurfaceId))
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateStrength, $"{context}: duplicate surface strength.")
                { Path = $"$.strengths[{index}].surfaceId", RegionKey = key, SurfaceId = entry.SurfaceId };
            ownedStrengths[index] = entry with { Strength = entry.Strength == 0f ? 0f : entry.Strength };
        }
        Array.Sort(ownedStrengths, (left, right) => left.SurfaceId.CompareTo(right.SurfaceId));

        Key = key;
        Triangles = Array.AsReadOnly(ownedTriangles);
        Strengths = Array.AsReadOnly(ownedStrengths);
    }

    public string Key { get; }
    public IReadOnlyList<HostCoverageRegionTriangles> Triangles { get; }
    public IReadOnlyList<CoverageSurfaceStrength> Strengths { get; }
    internal long TriangleCount { get; }
}

/// <summary>
/// Immutable optional coverage metadata beside one exact host GLB, the in-memory form of strict host coverage JSON
/// format 1 (<see cref="HostCoverageMapJson"/>). It names welded surface topology, authored regions, fixed render
/// refinement and authoring labels. The constructor validates every rule that does not need the host source, snapshots
/// the caller's collections and stores them in canonical order. Source-dependent checks (ranges inside the actual
/// primitives, manifold affected topology, core strengths) belong to the importer that also holds the GLB.
/// </summary>
public sealed class HostCoverageMap
{
    public const int MaxRegions = 32;
    public const int MaxSurfaceVertices = 65_536;
    public const int MaxTriangles = 65_536;
    public const int MinRefinementLevel = 1;
    public const int MaxRefinementLevel = 4;

    readonly string semanticSha256;

    public HostCoverageMap(string hostSha256, IEnumerable<CoverageTopologyEntry> topology, IEnumerable<HostCoverageRegion> regions,
        IEnumerable<HostCoverageRefinement> refinement, HostCoverageAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(refinement);
        if (!IsLowerSha256(hostSha256))
            throw new HostCoverageDataException(HostCoverageRefusal.MalformedDigest, "hostSha256 must be a lowercase SHA-256.")
            { Path = $"$.hostSha256" };
        if (!IsWellFormedLabel(authoring.Exporter))
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidLabel, "Authoring exporter must be non-blank, well-formed UTF-16.")
            { Path = "$.authoring.exporter" };
        if (!IsWellFormedLabel(authoring.Revision))
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidLabel, "Authoring revision must be non-blank, well-formed UTF-16.")
            { Path = "$.authoring.revision" };

        CoverageTopologyEntry[] ownedTopology = CoverageCollections.Snapshot(topology, MaxSurfaceVertices, count =>
            new HostCoverageDataException(HostCoverageRefusal.TopologyCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.topology", Count = count, Limit = MaxSurfaceVertices });
        if (ownedTopology.Length is 0 or > MaxSurfaceVertices)
            throw new HostCoverageDataException(HostCoverageRefusal.TopologyCapacity, $"Topology must contain 1 to {MaxSurfaceVertices} vertices.")
            { Path = $"$.topology", Count = ownedTopology.Length, Limit = MaxSurfaceVertices };
        var vertices = new HashSet<(int Mesh, int Primitive, int Vertex)>();
        var surfaces = new HashSet<int>();
        for (int index = 0; index < ownedTopology.Length; index++)
        {
            CoverageTopologyEntry entry = ownedTopology[index];
            if (entry.Mesh < 0 || entry.Primitive < 0 || entry.Vertex < 0 || entry.SurfaceId < 0)
                throw new HostCoverageDataException(HostCoverageRefusal.InvalidTopology, "Topology ordinals and surface IDs must be non-negative.")
                { Path = $"$.topology[{index}]", Mesh = entry.Mesh, Primitive = entry.Primitive, Vertex = entry.Vertex, SurfaceId = entry.SurfaceId };
            if (!vertices.Add((entry.Mesh, entry.Primitive, entry.Vertex)))
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateTopologyVertex, "Duplicate source topology vertex.")
                { Path = $"$.topology[{index}]", Mesh = entry.Mesh, Primitive = entry.Primitive, Vertex = entry.Vertex, SurfaceId = entry.SurfaceId };
            surfaces.Add(entry.SurfaceId);
        }
        Array.Sort(ownedTopology, CompareVertex);

        HostCoverageRegion[] ownedRegions = CoverageCollections.Snapshot(regions, MaxRegions, count =>
            new HostCoverageDataException(HostCoverageRefusal.RegionCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.regions", Count = count, Limit = MaxRegions });
        if (ownedRegions.Length is 0 or > MaxRegions)
            throw new HostCoverageDataException(HostCoverageRefusal.RegionCapacity, $"A host map must contain 1 to {MaxRegions} regions.")
            { Path = $"$.regions", Count = ownedRegions.Length, Limit = MaxRegions };
        if (Array.IndexOf(ownedRegions, null) >= 0)
            throw new HostCoverageDataException(HostCoverageRefusal.NullRegion, "A region cannot be null.")
            { Path = $"$.regions" };
        var regionKeys = new HashSet<string>(StringComparer.Ordinal);
        var positiveOwners = new Dictionary<int, string>();
        long triangleTotal = 0;
        for (int index = 0; index < ownedRegions.Length; index++)
        {
            HostCoverageRegion region = ownedRegions[index];
            if (!regionKeys.Add(region.Key))
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateRegion, $"Region key '{region.Key}' is not unique.")
                { Path = $"$.regions[{index}].key", RegionKey = region.Key };
            triangleTotal += region.TriangleCount;
            for (int strengthIndex = 0; strengthIndex < region.Strengths.Count; strengthIndex++)
            {
                CoverageSurfaceStrength strength = region.Strengths[strengthIndex];
                if (!surfaces.Contains(strength.SurfaceId))
                    throw new HostCoverageDataException(HostCoverageRefusal.UnknownSurface, $"Region '{region.Key}': surface {strength.SurfaceId} is not in the topology.")
                    { Path = $"$.regions[{index}].strengths[{strengthIndex}].surfaceId", RegionKey = region.Key, SurfaceId = strength.SurfaceId };
                if (strength.Strength > 0f && !positiveOwners.TryAdd(strength.SurfaceId, region.Key))
                    throw new HostCoverageDataException(HostCoverageRefusal.PositiveStrengthOverlap,
                        $"Surface {strength.SurfaceId} has positive strength in regions '{positiveOwners[strength.SurfaceId]}' and '{region.Key}'.")
                    { Path = $"$.regions[{index}].strengths[{strengthIndex}].surfaceId", RegionKey = region.Key, SurfaceId = strength.SurfaceId };
            }
        }
        if (triangleTotal > MaxTriangles)
            throw new HostCoverageDataException(HostCoverageRefusal.TotalTriangleCapacity, $"Regions reference more than {MaxTriangles} triangles.")
            { Path = $"$.regions", Count = triangleTotal, Limit = MaxTriangles };
        RefuseOverlap(ownedRegions.SelectMany(region => region.Triangles.Select(entry => entry.Triangles)).Order(RangeComparer.Instance),
            "Regions", "$.regions");

        HostCoverageRefinement[] ownedRefinement = CoverageCollections.Snapshot(refinement, MaxTriangles, count =>
            new HostCoverageDataException(HostCoverageRefusal.RefinementRangeCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.refinement", Count = count, Limit = MaxTriangles });
        for (int index = 0; index < ownedRefinement.Length; index++)
        {
            HostCoverageRefinement entry = ownedRefinement[index];
            ValidateRange(entry.Triangles, "Refinement", $"$.refinement[{index}]");
            if (entry.Level is < MinRefinementLevel or > MaxRefinementLevel)
                throw new HostCoverageDataException(HostCoverageRefusal.RefinementLevel, $"Refinement level must be {MinRefinementLevel} to {MaxRefinementLevel}.")
                { Path = $"$.refinement[{index}].level", Mesh = entry.Triangles.Mesh, Primitive = entry.Triangles.Primitive, FirstTriangle = entry.Triangles.FirstTriangle };
        }
        Array.Sort(ownedRefinement, (left, right) => Compare(left.Triangles, right.Triangles));
        RefuseOverlap(ownedRefinement.Select(entry => entry.Triangles), "Refinement", "$.refinement");
        if (ownedRefinement.Sum(entry => (long)entry.Triangles.Count) > MaxTriangles)
            throw new HostCoverageDataException(HostCoverageRefusal.RefinementTriangleCapacity, $"Refinement references more than {MaxTriangles} triangles.")
            { Path = $"$.refinement", Count = ownedRefinement.Sum(entry => (long)entry.Triangles.Count), Limit = MaxTriangles };

        Array.Sort(ownedRegions, (left, right) => string.CompareOrdinal(left.Key, right.Key));
        HostSha256 = hostSha256;
        Topology = Array.AsReadOnly(ownedTopology);
        Regions = Array.AsReadOnly(ownedRegions);
        Refinement = Array.AsReadOnly(ownedRefinement);
        Authoring = authoring;
        semanticSha256 = HostCoverageMapJson.ComputeSemanticSha256(this);
    }

    /// <summary>Lowercase SHA-256 of the exact self-contained host GLB bytes this map describes.</summary>
    public string HostSha256 { get; }
    public IReadOnlyList<CoverageTopologyEntry> Topology { get; }
    public IReadOnlyList<HostCoverageRegion> Regions { get; }
    public IReadOnlyList<HostCoverageRefinement> Refinement { get; }
    public HostCoverageAuthoring Authoring { get; }
    internal string SemanticSha256 => semanticSha256;

    internal static void ValidateRange(CoverageTriangleRange range, string context, string path, string? regionKey = null)
    {
        if (range.Mesh < 0 || range.Primitive < 0 || range.FirstTriangle < 0 || range.Count < 1
            || (long)range.FirstTriangle + range.Count > int.MaxValue)
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidRange, $"{context}: invalid triangle range {Describe(range)}.")
            { Path = path, RegionKey = regionKey, Mesh = range.Mesh, Primitive = range.Primitive, FirstTriangle = range.FirstTriangle };
    }

    /// <summary>Refuses overlap between ranges already sorted by <see cref="Compare"/>.</summary>
    internal static void RefuseOverlap(IEnumerable<CoverageTriangleRange> sorted, string context, string path, string? regionKey = null)
    {
        CoverageTriangleRange? previous = null;
        foreach (CoverageTriangleRange range in sorted)
        {
            if (previous is { } last && last.Mesh == range.Mesh && last.Primitive == range.Primitive
                && (long)last.FirstTriangle + last.Count > range.FirstTriangle)
                throw new HostCoverageDataException(HostCoverageRefusal.RangeOverlap, $"{context}: triangle ranges {Describe(last)} and {Describe(range)} overlap.")
                { Path = path, RegionKey = regionKey, Mesh = range.Mesh, Primitive = range.Primitive, FirstTriangle = range.FirstTriangle };
            previous = range;
        }
    }

    internal static int Compare(CoverageTriangleRange left, CoverageTriangleRange right) =>
        (left.Mesh, left.Primitive, left.FirstTriangle, left.Count).CompareTo((right.Mesh, right.Primitive, right.FirstTriangle, right.Count));

    internal static bool IsWellFormedLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index == value.Length || !char.IsLowSurrogate(value[index]))
                    return false;
            }
            else if (char.IsLowSurrogate(value[index]))
                return false;
        }
        return true;
    }

    static bool IsLowerSha256(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    static int CompareVertex(CoverageTopologyEntry left, CoverageTopologyEntry right) =>
        (left.Mesh, left.Primitive, left.Vertex).CompareTo((right.Mesh, right.Primitive, right.Vertex));

    static string Describe(CoverageTriangleRange range) => $"mesh {range.Mesh} primitive {range.Primitive} triangles {range.FirstTriangle}+{range.Count}";

    static string Describe(CoverageTopologyEntry entry) => $"mesh {entry.Mesh} primitive {entry.Primitive} vertex {entry.Vertex}";

    sealed class RangeComparer : IComparer<CoverageTriangleRange>
    {
        internal static readonly RangeComparer Instance = new();
        public int Compare(CoverageTriangleRange left, CoverageTriangleRange right) => HostCoverageMap.Compare(left, right);
    }
}
