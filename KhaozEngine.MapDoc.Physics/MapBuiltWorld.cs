using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
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
    Quaternion Orientation, MapBox3 Bounds, string Digest, IReadOnlyList<MapFaceKey> TriangleOwners)
{
    /// <summary>A precise placement pose, used only at the frame-local backend boundary. Terrain uses its whole-metre anchor.</summary>
    public MapEffectiveTransform? EffectiveTransform { get; init; }
}

/// <summary>Whether the backend's capsule feature query, <see cref="IPhysicsCapsuleFeatures"/>, can
/// capture a static, and why not. These predict that capability only. The contact controller's foot-support primitive,
/// not yet used by movement, certifies through <see cref="IPhysicsSupportNeighborhood"/>, whose limits differ: it
/// captures curved leaves and reads only the mesh triangles near its probe.</summary>
public enum MapFeatureQuerySupport
{
    /// <summary>The static meets none of the limits this builder measures: flattened leaf count, curved leaves, mesh
    /// triangle count, leaf local extent and the conservative hull point count of <see cref="HullCapacity"/>. The
    /// backend checks a hull's actual vertex and face capacity only inside a query, and the conservative point count
    /// keeps every supported hull within it.</summary>
    Supported,

    /// <summary>A compound flattens to more than 64 leaves.</summary>
    LeafCapacity,

    /// <summary>A sphere, capsule or cylinder leaf. The capsule feature query does not capture curved leaves.</summary>
    CurvedLeaf,

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
/// chunks and any bounded water. Resolver 1 worlds keep analytic terrain behind <see cref="LegacySupportHeight"/> and
/// identify it through <see cref="LegacyTerrainIdentity"/>. Water is built-world data, never a static.</summary>
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

    /// <summary>The union of storage, physical support, statics, interaction envelopes, render, LOD, light and water bounds.
    /// Storage carries no height, so a world with no bounded resource has a zero height range. A resolver-1 world's
    /// bounds do not include its analytic terrain height.</summary>
    public MapBox3 Bounds { get; }

    /// <summary>The resolved document's authored hash.</summary>
    public string AuthoredHash { get; }

    /// <summary>The document's storage tile edge in metres, <see cref="MapDocument.TileSize"/>.</summary>
    public float TileSize { get; }

    /// <summary>The lowercase hex SHA-256 of the built world's identity: the authored hash, builder version, consumer
    /// policy, interaction policy, chunk policy, legacy terrain identity and every placement and chunk digest, and for a
    /// world with a water section the water digest and every compiled body digest.</summary>
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

    /// <summary>Every bounded water body compiled against <see cref="Surfaces"/>, in id order, empty without a water
    /// section. Each is <see cref="MapWaterCompileStatus.Complete"/>.</summary>
    public IReadOnlyList<MapCompiledWaterBody> Water { get; }

    /// <summary>The water section's media in key order, empty without a section.</summary>
    public IReadOnlyList<MapWaterMediumDoc> WaterMedia { get; }

    /// <summary><see cref="MapWaterSet.Digest"/> of the water section the world was built from, empty without a
    /// section.</summary>
    public string WaterDigest { get; }

    /// <summary>The resolver-1 global water level, null for a native world.</summary>
    public float? LegacyWaterLevel { get; }

    /// <summary>Immutable world-metre lights in stable ID order, including bound leaf emissions.</summary>
    public IReadOnlyList<MapEffectiveLight> Lights { get; }

    /// <summary>Immutable XYZ markers in stable ID order, independent of MapSpawn.</summary>
    public IReadOnlyList<MapEffectiveMarker> Markers { get; }

    /// <summary>Explicit concealment links with world-metre heights and typed attachments.</summary>
    public IReadOnlyList<MapRoofLinkDoc> RoofLinks { get; }

    /// <summary>Explicit room sets. Only groups attached to a link expand its concealment set.</summary>
    public IReadOnlyList<MapRoofGroupDoc> RoofGroups { get; }

    /// <summary>The versioned effective annotation digest, empty without annotation intent.</summary>
    public string AnnotationDigest { get; }

    readonly Dictionary<string, MapPlacementGeometry> _byId;

    internal MapBuiltWorld(MapResolvedDocument document, MapScopedSurfaces surfaces,
        IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain, IReadOnlyList<MapStaticDescriptor> statics,
        IReadOnlyList<MapStaticDiagnostic> diagnostics, MapBox3 bounds, float tileSize, string buildHash, bool isNative,
        Func<float, float, float>? legacySupportHeight, IReadOnlyList<MapLegacySculptTile> legacySculptTiles,
        float legacySculptCellSize, string legacyTerrainBlockDigest, string legacyTerrainIdentity,
        IReadOnlyList<MapCompiledWaterBody> water, IReadOnlyList<MapWaterMediumDoc> waterMedia, string waterDigest,
        float? legacyWaterLevel)
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
        Water = water;
        WaterMedia = waterMedia;
        WaterDigest = waterDigest;
        LegacyWaterLevel = legacyWaterLevel;
        var effective = document.EffectiveResources;
        Lights = effective is null ? Array.Empty<MapEffectiveLight>() : MapEffectiveAnnotations.Lights(effective);
        Markers = effective is null ? Array.Empty<MapEffectiveMarker>() : MapEffectiveAnnotations.Markers(effective);
        var intent = effective?.BuildingIntent;
        RoofLinks = Array.AsReadOnly((intent?.RoofLinks ?? []).Select(link => link with
        { Rooms = Array.AsReadOnly(link.Rooms.ToArray()), Groups = Array.AsReadOnly(link.Groups.ToArray()) }).ToArray());
        RoofGroups = Array.AsReadOnly((intent?.RoofGroups ?? []).Select(group => group with { Rooms = Array.AsReadOnly(group.Rooms.ToArray()) }).ToArray());
        AnnotationDigest = effective?.AnnotationDigest ?? "";
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
