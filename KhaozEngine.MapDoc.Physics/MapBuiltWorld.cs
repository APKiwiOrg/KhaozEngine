using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>The inputs of one world build. <see cref="ConsumerPolicyIdentity"/> names the consumer's own rules that
/// read the built world and enters <see cref="MapBuiltWorld.BuildHash"/>. <see cref="LegacySupportHeight"/> is
/// required by resolver 1 and ignored by resolver 2.</summary>
public sealed record MapWorldBuildOptions(MapResolveOptions Resolve, string ConsumerPolicyIdentity,
    MapTerrainChunkPolicy Chunks, Func<float, float, float>? LegacySupportHeight = null, int BuilderVersion = 1);

/// <summary>What a static belongs to.</summary>
public enum MapStaticKind
{
    /// <summary>A solid placement's collider.</summary>
    Placement,

    /// <summary>One terrain physics chunk.</summary>
    TerrainChunk,
}

/// <summary>One static the world installs. <see cref="OwnerId"/> is a placement id or a terrain chunk id. The shape is
/// in metres relative to <see cref="Position"/> and <see cref="Orientation"/>. <see cref="Bounds"/> is its world
/// bounds. <see cref="TriangleOwners"/> names the compiled face of each terrain triangle and is empty for a
/// placement.</summary>
public sealed record MapStaticDescriptor(string OwnerId, MapStaticKind Kind, PhysicsShape Shape, Vector3 Position,
    Quaternion Orientation, MapBox3 Bounds, string Digest, IReadOnlyList<MapFaceKey> TriangleOwners);

/// <summary>Whether the backend's capsule feature query can capture a static, and why not.</summary>
public enum MapFeatureQuerySupport
{
    /// <summary>The static meets none of the limits this builder measures: flattened leaf count, curved leaves, mesh
    /// triangle count, leaf local extent and the conservative hull point count of <see cref="HullCapacity"/>. The
    /// backend checks a hull's actual vertex and face capacity only inside a query, and the conservative point count
    /// keeps every supported hull within it.</summary>
    Supported,

    /// <summary>A compound flattens to more than 64 leaves.</summary>
    LeafCapacity,

    /// <summary>A sphere, capsule or cylinder leaf, which the query captures from phase 2b.</summary>
    CurvedUntilPhase2b,

    /// <summary>A triangle mesh over 65,536 triangles.</summary>
    MeshTriangleCapacity,

    /// <summary>A leaf whose installed geometry reaches more than 64 m from its own origin on an axis.</summary>
    LocalExtent,

    /// <summary>A convex hull leaf with more than 130 points. The backend captures at most 256 faces and 1,524 face
    /// entries. A convex polyhedron with V vertices has at most 2V - 4 faces and 6V - 12 face entries, and a hull's
    /// vertices are a subset of its points, so 130 points or fewer keeps every limit. The check is conservative: a
    /// larger hull may still be captured. A tilted cylinder's interaction envelope hull has 128 points and is not a
    /// collider, so this check never measures it.</summary>
    HullCapacity,
}

/// <summary>One feature query limit a static meets. Limits are reported, never refused, because the static still
/// collides and blocks.</summary>
public sealed record MapStaticDiagnostic(string OwnerId, MapFeatureQuerySupport Support);

/// <summary>One resolver-1 sculpt tile: its tile coordinate, the world XZ footprint its deltas can change and its
/// digest. The footprint reaches one sculpt cell past the tile's first and last cell centres on each axis, since heights
/// interpolate between cell centres. Analytic terrain heights are unknown to the builder, so there is no height
/// range.</summary>
public sealed record MapLegacySculptTile(int TileX, int TileZ, MapResolvedBounds Footprint, string Digest);

/// <summary>A complete immutable world built from one native document. Resolver 2 worlds carry compiled terrain
/// chunks. Resolver 1 worlds keep analytic terrain behind <see cref="LegacySupportHeight"/> and identify it through
/// <see cref="LegacyTerrainIdentity"/>.</summary>
public sealed class MapBuiltWorld
{
    /// <summary>The resolved document.</summary>
    public MapResolvedDocument Document { get; }

    /// <summary>A complete view over a snapshot of the document's surfaces, empty for resolver 1.</summary>
    public MapScopedSurfaces Surfaces { get; }

    /// <summary>Every placement's geometry in ordinal placement order, solid or not.</summary>
    public IReadOnlyList<MapPlacementGeometry> Placements { get; }

    /// <summary>The terrain chunks compiled from <see cref="Surfaces"/>, empty for resolver 1.</summary>
    public MapTerrainChunkSet Terrain { get; }

    /// <summary>Placement colliders, then terrain chunks, each in ordinal owner order.</summary>
    public IReadOnlyList<MapStaticDescriptor> Statics { get; }

    /// <summary>Every feature query limit a static meets, in static order.</summary>
    public IReadOnlyList<MapStaticDiagnostic> Diagnostics { get; }

    /// <summary>The union of the storage bounds, every static and every interaction envelope. The storage bounds
    /// carry no height, so a world with no static and no envelope has a zero height range. A resolver-1 world's
    /// bounds do not include its analytic terrain height.</summary>
    public MapBox3 Bounds { get; }

    /// <summary>The resolved document's authored hash.</summary>
    public string AuthoredHash { get; }

    /// <summary>The document's storage tile edge in metres, <see cref="MapDocument.TileSize"/>.</summary>
    public float TileSize { get; }

    /// <summary>The lowercase hex SHA-256 of the built world's identity: the authored hash, builder version, consumer
    /// policy, interaction policy, chunk policy, legacy terrain identity and every placement and chunk digest.</summary>
    public string BuildHash { get; }

    /// <summary>True for resolver 2, whose terrain is compiled from authored surfaces.</summary>
    public bool IsNative { get; }

    /// <summary>The analytic support height of a resolver-1 world, null for a native world.</summary>
    public Func<float, float, float>? LegacySupportHeight { get; }

    /// <summary>The resolver-1 sculpt tiles in (TileZ, TileX) order, empty for a native world.</summary>
    public IReadOnlyList<MapLegacySculptTile> LegacySculptTiles { get; }

    /// <summary>The resolver-1 sculpt cell edge in metres: the document's sculpt block's, or
    /// <see cref="MapTerrainOverrides.DefaultCellSize"/> when it has none. Zero for a native world.</summary>
    public float LegacySculptCellSize { get; }

    /// <summary>The digest of the resolver-1 analytic terrain block without sculpt, empty for a native world.</summary>
    public string LegacyTerrainBlockDigest { get; }

    /// <summary>The digest over <see cref="LegacyTerrainBlockDigest"/> and every sculpt tile digest, empty for a native
    /// world.</summary>
    public string LegacyTerrainIdentity { get; }

    readonly Dictionary<string, MapPlacementGeometry> _byId;

    internal MapBuiltWorld(MapResolvedDocument document, MapScopedSurfaces surfaces,
        IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain, IReadOnlyList<MapStaticDescriptor> statics,
        IReadOnlyList<MapStaticDiagnostic> diagnostics, MapBox3 bounds, float tileSize, string buildHash, bool isNative,
        Func<float, float, float>? legacySupportHeight, IReadOnlyList<MapLegacySculptTile> legacySculptTiles,
        float legacySculptCellSize, string legacyTerrainBlockDigest, string legacyTerrainIdentity)
    {
        Document = document;
        Surfaces = surfaces;
        Placements = placements;
        Terrain = terrain;
        Statics = statics;
        Diagnostics = diagnostics;
        Bounds = bounds;
        AuthoredHash = document.AuthoredHash;
        TileSize = tileSize;
        BuildHash = buildHash;
        IsNative = isNative;
        LegacySupportHeight = legacySupportHeight;
        LegacySculptTiles = legacySculptTiles;
        LegacySculptCellSize = legacySculptCellSize;
        LegacyTerrainBlockDigest = legacyTerrainBlockDigest;
        LegacyTerrainIdentity = legacyTerrainIdentity;
        _byId = new Dictionary<string, MapPlacementGeometry>(placements.Count, StringComparer.Ordinal);
        foreach (MapPlacementGeometry placement in placements) _byId.Add(placement.PlacementId, placement);
    }

    /// <summary>The geometry of <paramref name="placementId"/>. Throws <see cref="ArgumentException"/> naming
    /// <c>placementId</c> when the world has no such placement.</summary>
    internal MapPlacementGeometry Placement(string placementId)
    {
        ArgumentNullException.ThrowIfNull(placementId);
        if (!_byId.TryGetValue(placementId, out MapPlacementGeometry? placement))
            throw new ArgumentException($"The world has no placement '{placementId}'.", nameof(placementId));
        return placement;
    }
}
