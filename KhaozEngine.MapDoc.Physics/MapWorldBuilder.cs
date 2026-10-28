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
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Builds the complete immutable world of one native document: resolution, placement geometry, terrain
/// chunks, bounded water, statics, feature query diagnostics, bounds and identity.</summary>
public static class MapWorldBuilder
{
    /// <summary>The largest static position, in metres from the world origin on any axis.</summary>
    public const double MaxCoordinateMetres = MapResourceTransform.MaxCoordinateMetres;

    // The backend capsule feature query's capture limits.
    const int MaxFeatureLeaves = 64, MaxFeatureMeshTriangles = 65_536;
    const float MaxFeatureLocalExtentMetres = 64f;

    // A hull with at most this many points stays within the backend's 256 faces and 1,524 face entries, since a convex
    // polyhedron with V vertices has at most 2V - 4 faces and 6V - 12 face entries.
    const int MaxFeatureHullPoints = 130;

    /// <summary>Builds <paramref name="document"/> against the verified <paramref name="assets"/>. Throws
    /// <see cref="MapDocumentException"/> for a partial window, a resolver identity the options or the legacy
    /// support height do not match, every resolution, placement and terrain refusal, a static position beyond
    /// 1,000,000 m on an axis, two statics with one owner id, and naming the water body, with "water: " and its
    /// detail, when a body of the water section does not compile <see cref="MapWaterCompileStatus.Complete"/>.
    /// Throws <see cref="ArgumentException"/> for invalid options.</summary>
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
        var effective = native ? resolved.EffectiveResources : null;
        bool completeEffective = effective is not null && (document.Prefabs is not null || document.NativeResourceTransforms is not null ||
            resolved.Placements.Any(p => assets.GetAsset(p.AssetId).SupportResourceIds.Count != 0));
        MapScopedSurfaces surfaces = effective?.CompleteView() ?? MapScopedSurfaces.CompleteView(new MapSurfaceSet());
        if (completeEffective)
        {
            // Coverage refines under the same declared profile the resolution used.
            var findings = MapSpaceCoverageValidator.Validate(surfaces, options.Resolve.QueryLimits.Refinement ?? new());
            if (findings.Count != 0) throw new MapDocumentException(string.Join("\n", findings));
        }
        MapTerrainChunkSet terrain = MapTerrainPhysics.Compile(surfaces, options.Chunks);
        IReadOnlyList<MapPlacementGeometry> placements = MapPlacementShapes.Resolve(resolved);
        if (completeEffective)
            MapPrefabSupportBounds.RefuseCoincidentFloors(surfaces, placements);
        // The resolver's authored identity verified the document-level water rules and the material closure. The compile
        // verifies containment over the whole-document view, so a built world never carries unverified water.
        MapWaterSet? water = effective?.Water;
        IReadOnlyList<MapCompiledWaterBody> bodies = water is null ? Array.Empty<MapCompiledWaterBody>() : CompileWater(surfaces, water);

        IReadOnlyList<MapLegacySculptTile> sculpt = Array.Empty<MapLegacySculptTile>();
        string terrainBlock = "", legacyIdentity = "";
        float sculptCellSize = 0f;
        if (!native)
        {
            sculptCellSize = document.TerrainOverrides?.CellSize ?? MapTerrainOverrides.DefaultCellSize;
            sculpt = LegacySculpt(document);
            terrainBlock = MapLegacyTerrainDigest.TerrainBlock(document);
            legacyIdentity = LegacyIdentity(terrainBlock, sculpt);
        }

        IReadOnlyList<MapStaticDescriptor> statics = Statics(placements, terrain);
        var staticOwners = new HashSet<string>(statics.Select(s => s.OwnerId), StringComparer.Ordinal);
        foreach (MapCompiledWaterBody body in bodies)
        {
            string owner = MapResidencyOwnership.WaterOwnerId(body.BodyId);
            if (staticOwners.Contains(owner))
                throw new MapDocumentException($"water: static owner '{owner}' conflicts with body '{body.BodyId}'");
        }
        var diagnostics = new List<MapStaticDiagnostic>();
        foreach (MapStaticDescriptor descriptor in statics)
            foreach (MapFeatureQuerySupport support in Measure(descriptor.Shape))
                diagnostics.Add(new(descriptor.OwnerId, support));

        string waterDigest = water?.Digest() ?? "";
        string buildHash = BuildHash(resolved.AuthoredHash, options, legacyIdentity, placements, terrain,
            water is null ? null : (waterDigest, bodies), effective?.AnnotationDigest ?? "");
        MapWaterMediumDoc[] media = water?.Media.OrderBy(m => m.Key, StringComparer.Ordinal).ToArray() ?? Array.Empty<MapWaterMediumDoc>();
        MapBox3[] supportBounds = effective is not null && terrain.Chunks.Count != 0
            ? new[] { MapPrefabSupportBounds.Bounds(effective) } : Array.Empty<MapBox3>();
        return new MapBuiltWorld(resolved, surfaces, placements, terrain, statics, diagnostics.AsReadOnly(),
            WorldBounds(document.Bounds, statics, placements,
                effective is null ? Array.Empty<MapBox3>() : MapPrefabSupportBounds.OtherBounds(effective, assets)
                    .Concat(supportBounds)
                    .Concat(bodies.Where(b => b.Bounds is not null).Select(b => b.Bounds!.Value))), document.TileSize, buildHash, native,
            native ? null : options.LegacySupportHeight, sculpt, sculptCellSize, terrainBlock, legacyIdentity,
            bodies, Array.AsReadOnly(media), waterDigest,
            legacyWaterLevel: !native && document.Water is null ? document.Terrain.WaterLevel : null);
    }

    /// <summary>The compiled bodies of <paramref name="water"/>, after refusing any body that is not complete.</summary>
    static IReadOnlyList<MapCompiledWaterBody> CompileWater(MapScopedSurfaces surfaces, MapWaterSet water)
    {
        IReadOnlyList<MapCompiledWaterBody> bodies = MapWaterCompiler.Compile(surfaces, water);
        foreach (MapCompiledWaterBody body in bodies)
            RequireWaterBody(body);
        return bodies;
    }

    internal static void RequireWaterBody(MapCompiledWaterBody body)
    {
        if (body.Status != MapWaterCompileStatus.Complete)
            throw new MapDocumentException($"water: {body.Detail ?? $"water body '{body.BodyId}': {body.Status}"}");
        if (body.Regions.Count > 0 && body.Bounds is null)
            throw new MapDocumentException($"water: body '{body.BodyId}' has regions but no bounds");
        if (body.Regions.Any(r => r.Fragment is not null) && body.FragmentBounds.Count == 0)
            throw new MapDocumentException($"water: body '{body.BodyId}' has occupied regions but no fragment bounds");
    }

    internal static void ValidateOptions(MapWorldBuildOptions options)
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

    internal static IReadOnlyList<MapStaticDescriptor> Statics(IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain)
    {
        var statics = new List<MapStaticDescriptor>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapPlacementGeometry placement in placements)
        {
            if (placement.Collider is null) continue;
            Vector3 p = placement.WorldPose.Position;
            MapResourceTransform.RequireWithinWorld(placement.PlacementId, p.X, p.Y, p.Z);
            Add(new(placement.PlacementId, MapStaticKind.Placement, placement.Collider, p, placement.WorldPose.Orientation,
                placement.ColliderBounds!.Value, placement.Digest, Array.Empty<MapFaceKey>())
            { EffectiveTransform = placement.EffectiveTransform });
        }
        foreach (MapTerrainChunk chunk in terrain.Chunks)
        {
            MapSubmissionAnchor a = chunk.Anchor;
            MapResourceTransform.RequireWithinWorld(chunk.ChunkId, a.X, a.Y, a.Z);
            var position = new Vector3(a.X, a.Y, a.Z);
            Add(new(chunk.ChunkId, MapStaticKind.TerrainChunk, chunk.Shape, position, Quaternion.Identity,
                chunk.Bounds, chunk.Digest, chunk.TriangleOwners));
        }
        return statics.AsReadOnly();

        void Add(MapStaticDescriptor descriptor)
        {
            if (!owners.Add(descriptor.OwnerId))
                throw new MapDocumentException($"Two statics share the owner id '{descriptor.OwnerId}'.");
            statics.Add(descriptor);
        }
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
                found.Add(MapFeatureQuerySupport.CurvedLeaf);
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
                if (hull.Points.Length > MaxFeatureHullPoints) found.Add(MapFeatureQuerySupport.HullCapacity);
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
        IReadOnlyList<MapPlacementGeometry> placements, IEnumerable<MapBox3> other)
    {
        double minX = storage.MinX, minZ = storage.MinZ, maxX = storage.MaxX, maxZ = storage.MaxZ;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (MapBox3 box in statics.Select(s => s.Bounds).Concat(placements.Select(p => p.Envelope.Bounds)).Concat(other))
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
        return overrides.Tiles.Select(tile =>
        {
            (float minX, float maxX) = SculptFootprintAxis(tile.TileX, cell);
            (float minZ, float maxZ) = SculptFootprintAxis(tile.TileZ, cell);
            return new MapLegacySculptTile(tile.TileX, tile.TileZ, new MapResolvedBounds(minX, minZ, maxX, maxZ),
                MapLegacyTerrainDigest.SculptTile(tile));
        }).ToArray();
    }

    /// <summary>The extent along one axis that resolver-1 sculpt tile index <paramref name="tile"/> can change at sculpt
    /// cell edge <paramref name="cell"/>: one cell past its first and last cell centres, since heights interpolate
    /// between cell centres.</summary>
    internal static (float Min, float Max) SculptFootprintAxis(long tile, double cell)
    {
        const long size = TerrainSculpt.TileSize;
        return ((float)((tile * size - 1) * cell), (float)((tile * size + size) * cell));
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

    // Water adds hash members only when a section is present. The authored hash already includes the native format.
    static string BuildHash(string authoredHash, MapWorldBuildOptions options, string legacyIdentity,
        IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain,
        (string Digest, IReadOnlyList<MapCompiledWaterBody> Bodies)? water, string annotationDigest) => CanonicalHash(w =>
    {
        w.WriteStartObject();
        w.WriteString("domain", "kemap/built-world/1");
        w.WriteString("authoredHash", authoredHash);
        w.WriteNumber("builderVersion", options.BuilderVersion);
        w.WriteString("consumerPolicyIdentity", options.ConsumerPolicyIdentity);
        w.WriteString("interactionPolicyHash", MapInteractionPolicy.Hash);
        w.WriteNumber("maxTrianglesPerChunk", options.Chunks.MaxTrianglesPerChunk);
        w.WriteString("legacyTerrainIdentity", legacyIdentity);
        if (annotationDigest.Length != 0) w.WriteString("annotationDigest", annotationDigest);
        w.WriteStartArray("placements");
        foreach (MapPlacementGeometry placement in placements) Pair(w, placement.PlacementId, placement.Digest);
        w.WriteEndArray();
        w.WriteStartArray("terrain");
        foreach (MapTerrainChunk chunk in terrain.Chunks) Pair(w, chunk.ChunkId, chunk.Digest);
        w.WriteEndArray();
        if (water is { } section)
        {
            w.WriteString("waterDigest", section.Digest);
            w.WriteStartArray("water");
            foreach (MapCompiledWaterBody body in section.Bodies) Pair(w, body.BodyId, body.Digest);
            w.WriteEndArray();
        }
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
