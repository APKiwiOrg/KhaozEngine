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
            throw new ArgumentException("A region key must be non-blank, well-formed UTF-16.", nameof(key));
        string context = $"Region '{key}'";

        HostCoverageRegionTriangles[] ownedTriangles = triangles.ToArray();
        if (ownedTriangles.Length == 0)
            throw new ArgumentException($"{context}: at least one triangle range is required.", nameof(triangles));
        foreach (HostCoverageRegionTriangles entry in ownedTriangles)
        {
            HostCoverageMap.ValidateRange(entry.Triangles, context, nameof(triangles));
            if (entry.Role is not (CoverageTriangleRole.Core or CoverageTriangleRole.Collar))
                throw new ArgumentException($"{context}: unsupported triangle role {(int)entry.Role}.", nameof(triangles));
        }
        Array.Sort(ownedTriangles, (left, right) => HostCoverageMap.Compare(left.Triangles, right.Triangles));
        HostCoverageMap.RefuseOverlap(ownedTriangles.Select(entry => entry.Triangles), context, nameof(triangles));
        TriangleCount = ownedTriangles.Sum(entry => (long)entry.Triangles.Count);
        if (TriangleCount > HostCoverageMap.MaxTriangles)
            throw new ArgumentException($"{context}: more than {HostCoverageMap.MaxTriangles} triangles.", nameof(triangles));

        CoverageSurfaceStrength[] ownedStrengths = strengths.ToArray();
        if (ownedStrengths.Length == 0)
            throw new ArgumentException($"{context}: at least one surface strength is required.", nameof(strengths));
        for (int index = 0; index < ownedStrengths.Length; index++)
        {
            CoverageSurfaceStrength entry = ownedStrengths[index];
            if (entry.SurfaceId < 0)
                throw new ArgumentException($"{context}: surface IDs must be non-negative.", nameof(strengths));
            if (!float.IsFinite(entry.Strength) || entry.Strength < 0f || entry.Strength > 1f)
                throw new ArgumentException($"{context}: surface {entry.SurfaceId} strength must be finite in [0,1].", nameof(strengths));
            ownedStrengths[index] = entry with { Strength = entry.Strength == 0f ? 0f : entry.Strength };
        }
        Array.Sort(ownedStrengths, (left, right) => left.SurfaceId.CompareTo(right.SurfaceId));
        for (int index = 1; index < ownedStrengths.Length; index++)
            if (ownedStrengths[index].SurfaceId == ownedStrengths[index - 1].SurfaceId)
                throw new ArgumentException($"{context}: surface {ownedStrengths[index].SurfaceId} has more than one strength.", nameof(strengths));

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
            throw new ArgumentException("hostSha256 must be a lowercase SHA-256.", nameof(hostSha256));
        if (!IsWellFormedLabel(authoring.Exporter) || !IsWellFormedLabel(authoring.Revision))
            throw new ArgumentException("Authoring exporter and revision must be non-blank, well-formed UTF-16.", nameof(authoring));

        CoverageTopologyEntry[] ownedTopology = topology.ToArray();
        if (ownedTopology.Length is 0 or > MaxSurfaceVertices)
            throw new ArgumentException($"Topology must contain 1 to {MaxSurfaceVertices} vertices.", nameof(topology));
        foreach (CoverageTopologyEntry entry in ownedTopology)
            if (entry.Mesh < 0 || entry.Primitive < 0 || entry.Vertex < 0 || entry.SurfaceId < 0)
                throw new ArgumentException("Topology ordinals and surface IDs must be non-negative.", nameof(topology));
        Array.Sort(ownedTopology, CompareVertex);
        var surfaces = new HashSet<int>();
        for (int index = 0; index < ownedTopology.Length; index++)
        {
            if (index > 0 && CompareVertex(ownedTopology[index - 1], ownedTopology[index]) == 0)
                throw new ArgumentException($"Topology vertex {Describe(ownedTopology[index])} is listed more than once.", nameof(topology));
            surfaces.Add(ownedTopology[index].SurfaceId);
        }

        HostCoverageRegion[] ownedRegions = regions.ToArray();
        if (ownedRegions.Length is 0 or > MaxRegions)
            throw new ArgumentException($"A host map must contain 1 to {MaxRegions} regions.", nameof(regions));
        if (Array.IndexOf(ownedRegions, null) >= 0)
            throw new ArgumentException("A region cannot be null.", nameof(regions));
        Array.Sort(ownedRegions, (left, right) => string.CompareOrdinal(left.Key, right.Key));
        var positiveOwners = new Dictionary<int, string>();
        long triangleTotal = 0;
        for (int index = 0; index < ownedRegions.Length; index++)
        {
            HostCoverageRegion region = ownedRegions[index];
            if (index > 0 && region.Key == ownedRegions[index - 1].Key)
                throw new ArgumentException($"Region key '{region.Key}' is not unique.", nameof(regions));
            triangleTotal += region.TriangleCount;
            foreach (CoverageSurfaceStrength strength in region.Strengths)
            {
                if (!surfaces.Contains(strength.SurfaceId))
                    throw new ArgumentException($"Region '{region.Key}': surface {strength.SurfaceId} is not in the topology.", nameof(regions));
                if (strength.Strength > 0f && !positiveOwners.TryAdd(strength.SurfaceId, region.Key))
                    throw new ArgumentException(
                        $"Surface {strength.SurfaceId} has positive strength in regions '{positiveOwners[strength.SurfaceId]}' and '{region.Key}'.", nameof(regions));
            }
        }
        if (triangleTotal > MaxTriangles)
            throw new ArgumentException($"Regions reference more than {MaxTriangles} triangles.", nameof(regions));
        RefuseOverlap(ownedRegions.SelectMany(region => region.Triangles.Select(entry => entry.Triangles)).Order(RangeComparer.Instance),
            "Regions", nameof(regions));

        HostCoverageRefinement[] ownedRefinement = refinement.ToArray();
        foreach (HostCoverageRefinement entry in ownedRefinement)
        {
            ValidateRange(entry.Triangles, "Refinement", nameof(refinement));
            if (entry.Level is < MinRefinementLevel or > MaxRefinementLevel)
                throw new ArgumentException($"Refinement level must be {MinRefinementLevel} to {MaxRefinementLevel}.", nameof(refinement));
        }
        Array.Sort(ownedRefinement, (left, right) => Compare(left.Triangles, right.Triangles));
        RefuseOverlap(ownedRefinement.Select(entry => entry.Triangles), "Refinement", nameof(refinement));
        if (ownedRefinement.Sum(entry => (long)entry.Triangles.Count) > MaxTriangles)
            throw new ArgumentException($"Refinement references more than {MaxTriangles} triangles.", nameof(refinement));

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

    internal static void ValidateRange(CoverageTriangleRange range, string context, string parameter)
    {
        if (range.Mesh < 0 || range.Primitive < 0 || range.FirstTriangle < 0 || range.Count < 1
            || (long)range.FirstTriangle + range.Count > int.MaxValue)
            throw new ArgumentException($"{context}: invalid triangle range {Describe(range)}.", parameter);
    }

    /// <summary>Refuses overlap between ranges already sorted by <see cref="Compare"/>.</summary>
    internal static void RefuseOverlap(IEnumerable<CoverageTriangleRange> sorted, string context, string parameter)
    {
        CoverageTriangleRange? previous = null;
        foreach (CoverageTriangleRange range in sorted)
        {
            if (previous is { } last && last.Mesh == range.Mesh && last.Primitive == range.Primitive
                && (long)last.FirstTriangle + last.Count > range.FirstTriangle)
                throw new ArgumentException($"{context}: triangle ranges {Describe(last)} and {Describe(range)} overlap.", parameter);
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
