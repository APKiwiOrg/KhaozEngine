using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Builds the complete immutable world of one native document: resolution, placement geometry, terrain
/// chunks, statics, feature query diagnostics, bounds and identity.</summary>
public static class MapWorldBuilder
{
    /// <summary>The largest static position, in metres from the world origin on any axis.</summary>
    public const double MaxCoordinateMetres = 1_000_000;

    // The backend capsule feature query's capture limits.
    const int MaxFeatureLeaves = 64, MaxFeatureMeshTriangles = 65_536;
    const float MaxFeatureLocalExtentMetres = 64f;

    /// <summary>Builds <paramref name="document"/> against the verified <paramref name="assets"/>. Throws
    /// <see cref="MapDocumentException"/> for a partial window, a resolver identity the options or the legacy
    /// support height do not match, every resolution, placement and terrain refusal, a static position beyond
    /// 1,000,000 m on an axis and two statics with one owner id. Throws <see cref="ArgumentException"/> for
    /// invalid options.</summary>
    public static MapBuiltWorld Build(MapDocument document, MapAssetClosure assets, MapWorldBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException(
                "A world builds from a complete document, not a partial window. Load every tile before building.");
        ValidateOptions(options);

        MapResolvedDocument resolved =
            MapNativeResolution.Resolve(document, assets, options.Resolve, options.LegacySupportHeight);
        bool native = options.Resolve.ResolverVersion == 2;
        MapScopedSurfaces surfaces = MapScopedSurfaces.CompleteView(native ? document.Surfaces.Clone() : new MapSurfaceSet());
        MapTerrainChunkSet terrain = MapTerrainPhysics.Compile(surfaces, options.Chunks);
        IReadOnlyList<MapPlacementGeometry> placements = MapPlacementShapes.Resolve(resolved);

        IReadOnlyList<MapLegacySculptTile> sculpt = Array.Empty<MapLegacySculptTile>();
        string terrainBlock = "", legacyIdentity = "";
        if (!native)
        {
            sculpt = LegacySculpt(document);
            terrainBlock = MapLegacyTerrainDigest.TerrainBlock(document);
            legacyIdentity = LegacyIdentity(terrainBlock, sculpt);
        }

        IReadOnlyList<MapStaticDescriptor> statics = Statics(placements, terrain);
        var diagnostics = new List<MapStaticDiagnostic>();
        foreach (MapStaticDescriptor descriptor in statics)
            foreach (MapFeatureQuerySupport support in Measure(descriptor.Shape))
                diagnostics.Add(new(descriptor.OwnerId, support));

        string buildHash = BuildHash(resolved.AuthoredHash, options, legacyIdentity, placements, terrain);
        return new MapBuiltWorld(resolved, surfaces, placements, terrain, statics, diagnostics.AsReadOnly(),
            WorldBounds(document.Bounds, statics, placements), document.TileSize, buildHash, native,
            native ? null : options.LegacySupportHeight, sculpt, terrainBlock, legacyIdentity);
    }

    static void ValidateOptions(MapWorldBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Resolve, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Chunks, nameof(options));
        if (string.IsNullOrWhiteSpace(options.ConsumerPolicyIdentity))
            throw new ArgumentException("ConsumerPolicyIdentity must name the consumer policy.", nameof(options));
        if (options.BuilderVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "BuilderVersion must be positive.");
        // MapTerrainPhysics.Compile repeats this check, but only after resolution has run. Refusing here keeps an
        // invalid option from paying for a whole resolution first.
        if (options.Chunks.MaxTrianglesPerChunk is < MapTerrainPhysics.MinTrianglesPerChunk or > MapTerrainPhysics.MaxTrianglesPerChunk)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxTrianglesPerChunk must be {MapTerrainPhysics.MinTrianglesPerChunk} to {MapTerrainPhysics.MaxTrianglesPerChunk}.");
    }

    static IReadOnlyList<MapStaticDescriptor> Statics(IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain)
    {
        var statics = new List<MapStaticDescriptor>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacementGeometry placement in placements)
        {
            if (placement.Collider is null) continue;
            Vector3 p = placement.WorldPose.Position;
            RequireWithinWorld(placement.PlacementId, p.X, p.Y, p.Z);
            Add(new(placement.PlacementId, MapStaticKind.Placement, placement.Collider, p, placement.WorldPose.Orientation,
                placement.ColliderBounds!.Value, placement.Digest, Array.Empty<MapFaceKey>()));
        }
        foreach (MapTerrainChunk chunk in terrain.Chunks)
        {
            MapSubmissionAnchor a = chunk.Anchor;
            RequireWithinWorld(chunk.ChunkId, a.X, a.Y, a.Z);
            var position = new Vector3(a.X, a.Y, a.Z);
            Add(new(chunk.ChunkId, MapStaticKind.TerrainChunk, chunk.Shape, position, Quaternion.Identity,
                MapShapeBounds.Of(chunk.Shape, Pose.At(position)), chunk.Digest, chunk.TriangleOwners));
        }
        return statics.AsReadOnly();

        void Add(MapStaticDescriptor descriptor)
        {
            if (!owners.Add(descriptor.OwnerId))
                throw new MapDocumentException($"Two statics share the owner id '{descriptor.OwnerId}'.");
            statics.Add(descriptor);
        }
    }

    static void RequireWithinWorld(string owner, double x, double y, double z)
    {
        if (Math.Abs(x) > MaxCoordinateMetres || Math.Abs(y) > MaxCoordinateMetres || Math.Abs(z) > MaxCoordinateMetres)
            throw new MapDocumentException(FormattableString.Invariant(
                $"Static '{owner}' at ({x}, {y}, {z}) lies beyond 1,000,000 m from the world origin on an axis."));
    }

    /// <summary>The feature query limits a static meets, measured per installed leaf as the backend measures it.</summary>
    internal static IReadOnlyList<MapFeatureQuerySupport> Measure(PhysicsShape shape)
    {
        var found = new SortedSet<MapFeatureQuerySupport>();
        if (shape is CompoundShape compound)
        {
            int leaves = 0;
            MeasureCompound(compound, found, ref leaves);
            if (leaves > MaxFeatureLeaves) found.Add(MapFeatureQuerySupport.LeafCapacity);
        }
        else MeasureLeaf(shape, found);
        return found.ToArray();
    }

    // The backend flattens nested compounds into one level of leaves and measures each leaf in its own frame.
    static void MeasureCompound(CompoundShape compound, SortedSet<MapFeatureQuerySupport> found, ref int leaves)
    {
        foreach (CompoundChild child in compound.Children)
        {
            if (child.Shape is CompoundShape nested) MeasureCompound(nested, found, ref leaves);
            else
            {
                leaves++;
                MeasureLeaf(child.Shape, found);
            }
        }
    }

    static void MeasureLeaf(PhysicsShape shape, SortedSet<MapFeatureQuerySupport> found)
    {
        switch (shape)
        {
            case SphereShape or CapsuleShape or CylinderShape:
                found.Add(MapFeatureQuerySupport.CurvedUntilPhase2b);
                break;
            case BoxShape box:
                if (Beyond(box.HalfExtents)) found.Add(MapFeatureQuerySupport.LocalExtent);
                break;
            case ConvexHullShape hull:
                // The backend recentres a hull on its centre of mass, which lies inside its bounding box, so a box
                // span within the limit keeps every recentred point within it.
                if (Span(hull.Points) is var span && (span.X > MaxFeatureLocalExtentMetres ||
                    span.Y > MaxFeatureLocalExtentMetres || span.Z > MaxFeatureLocalExtentMetres))
                    found.Add(MapFeatureQuerySupport.LocalExtent);
                break;
            case TriangleMeshShape mesh:
                if (mesh.Indices.Length / 3 > MaxFeatureMeshTriangles) found.Add(MapFeatureQuerySupport.MeshTriangleCapacity);
                if (mesh.Vertices.Any(Beyond)) found.Add(MapFeatureQuerySupport.LocalExtent);
                break;
        }
    }

    static bool Beyond(Vector3 v) => Math.Abs(v.X) > MaxFeatureLocalExtentMetres ||
        Math.Abs(v.Y) > MaxFeatureLocalExtentMetres || Math.Abs(v.Z) > MaxFeatureLocalExtentMetres;

    static Vector3 Span(Vector3[] points)
    {
        Vector3 min = points[0], max = points[0];
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return max - min;
    }

    static MapBox3 WorldBounds(MapBounds storage, IReadOnlyList<MapStaticDescriptor> statics,
        IReadOnlyList<MapPlacementGeometry> placements)
    {
        double minX = storage.MinX, minZ = storage.MinZ, maxX = storage.MaxX, maxZ = storage.MaxZ;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (MapBox3 box in statics.Select(s => s.Bounds).Concat(placements.Select(p => p.Envelope.Bounds)))
        {
            minX = Math.Min(minX, box.MinX);
            minY = Math.Min(minY, box.MinY);
            minZ = Math.Min(minZ, box.MinZ);
            maxX = Math.Max(maxX, box.MaxX);
            maxY = Math.Max(maxY, box.MaxY);
            maxZ = Math.Max(maxZ, box.MaxZ);
        }
        if (minY > maxY) minY = maxY = 0;
        return new MapBox3(minX, minY, minZ, maxX, maxY, maxZ);
    }

    static IReadOnlyList<MapLegacySculptTile> LegacySculpt(MapDocument document)
    {
        if (document.TerrainOverrides is not { } overrides) return Array.Empty<MapLegacySculptTile>();
        double cell = overrides.CellSize;
        const long size = TerrainSculpt.TileSize;
        return overrides.Tiles.Select(tile => new MapLegacySculptTile(tile.TileX, tile.TileZ,
            new MapResolvedBounds((float)((tile.TileX * size - 1) * cell), (float)((tile.TileZ * size - 1) * cell),
                (float)((tile.TileX * size + size) * cell), (float)((tile.TileZ * size + size) * cell)),
            MapLegacyTerrainDigest.SculptTile(tile))).ToArray();
    }

    static string LegacyIdentity(string terrainBlock, IReadOnlyList<MapLegacySculptTile> sculpt) => CanonicalHash(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/legacy-terrain/1");
        w.WriteString("terrainBlock", terrainBlock);
        w.WriteStartArray("sculpt");
        foreach (MapLegacySculptTile tile in sculpt)
        {
            w.WriteStartArray();
            w.WriteNumberValue(tile.TileX);
            w.WriteNumberValue(tile.TileZ);
            w.WriteStringValue(tile.Digest);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    static string BuildHash(string authoredHash, MapWorldBuildOptions options, string legacyIdentity,
        IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain) => CanonicalHash(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/built-world/1");
        w.WriteString("authoredHash", authoredHash);
        w.WriteNumber("builderVersion", options.BuilderVersion);
        w.WriteString("consumerPolicyIdentity", options.ConsumerPolicyIdentity);
        w.WriteString("interactionPolicyHash", MapInteractionPolicy.Hash);
        w.WriteNumber("maxTrianglesPerChunk", options.Chunks.MaxTrianglesPerChunk);
        w.WriteString("legacyTerrainIdentity", legacyIdentity);
        w.WriteStartArray("placements");
        foreach (MapPlacementGeometry placement in placements) Pair(w, placement.PlacementId, placement.Digest);
        w.WriteEndArray();
        w.WriteStartArray("terrain");
        foreach (MapTerrainChunk chunk in terrain.Chunks) Pair(w, chunk.ChunkId, chunk.Digest);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    static void Pair(Utf8JsonWriter w, string id, string digest)
    {
        w.WriteStartArray();
        w.WriteStringValue(id);
        w.WriteStringValue(digest);
        w.WriteEndArray();
    }

    // Compact canonical JSON, hashed as written.
    static string CanonicalHash(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            body(writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
