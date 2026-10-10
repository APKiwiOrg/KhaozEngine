using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Shared native world fixtures, built through public MapDoc and Physics APIs only. Every fixture collider
/// sits with its bottom at the placement origin unless a fixture states otherwise. The physics seam centres a box on
/// its pose, so a box child is lifted by half its height. A cylinder stands on its base at its pose, at top level and
/// as a compound child, so a cylinder child sits at local y 0.</summary>
internal static partial class NativeWorldFixtures
{
    // ---------------------------------------------------------------------------------------------------------------
    // Asset closure
    // ---------------------------------------------------------------------------------------------------------------

    internal const string AssetRootId = "native-world.assets";
    const string SharedMeshId = "native-world.mesh";

    /// <summary>One verified closure holding every fixture asset, loaded from an in-memory source.</summary>
    internal static (MapAssetClosure Closure, IReadOnlyList<MapAssetRef> Roots) Assets()
    {
        var source = new NativeWorldAssetSource();
        var assets = new JsonArray();
        var resources = new JsonArray { Resource(source.Add(SharedMeshId, "mesh:native-world"), MapResourceKind.Mesh) };

        void Solid(string id, PhysicsShape collider, Vector3 min, Vector3 max, params string[] supportIds)
        {
            string colliderId = id + ".collider";
            resources.Add(Resource(source.Add(colliderId, ColliderBytes(collider)), MapResourceKind.Collider));
            assets.Add(Asset(id, min, max, colliderId, null, supportIds));
        }

        // A doorway: two jambs and a lintel, 1.8 m wide and 2.7 m tall, open between x = -0.6 and 0.6.
        Solid("doorway", Compound(
                (Box(0.3f, 2.4f, 0.3f), new Vector3(-0.75f, 1.2f, 0f)),
                (Box(0.3f, 2.4f, 0.3f), new Vector3(0.75f, 1.2f, 0f)),
                (Box(1.8f, 0.3f, 0.3f), new Vector3(0f, 2.55f, 0f))),
            new Vector3(-0.9f, 0f, -0.15f), new Vector3(0.9f, 2.7f, 0.15f));

        // Two 2 m by 2 m by 0.2 m walls meeting at a right angle in the corner at the origin.
        Solid("corner-wall", Compound(
                (Box(2f, 2f, 0.2f), new Vector3(1f, 1f, 0.1f)),
                (Box(0.2f, 2f, 2f), new Vector3(0.1f, 1f, 1f))),
            Vector3.Zero, new Vector3(2f, 2f, 2f));

        Solid("crate", Compound((Box(0.6f, 0.2f, 0.6f), new Vector3(0f, 0.1f, 0f))),
            new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 0.2f, 0.3f));

        // Base-aligned, so the trunk spans y 0 to 6 once installed.
        Solid("tree", Compound((new CylinderShape(0.3f, 6f), Vector3.Zero)),
            new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 6f, 0.3f));

        Solid("large-building", Compound((Box(100f, 10f, 100f), new Vector3(0f, 5f, 0f))),
            new Vector3(-50f, 0f, -50f), new Vector3(50f, 10f, 50f));

        // Its corners lie 75 m from the origin along x.
        Solid("long-wall", Compound((Box(150f, 3f, 0.5f), new Vector3(0f, 1.5f, 0f))),
            new Vector3(-75f, 0f, -0.25f), new Vector3(75f, 3f, 0.25f));

        // The one fixture whose top, not its bottom, is at the placement origin.
        Solid("bridge-deck", Compound((Box(18f, 0.25f, 5f), new Vector3(0f, -0.125f, 0f))),
            new Vector3(-9f, -0.25f, -2.5f), new Vector3(9f, 0f, 2.5f));

        Solid("parapet-1m", Compound((Box(1f, 1f, 0.2f), new Vector3(0f, 0.5f, 0f))),
            new Vector3(-0.5f, 0f, -0.1f), new Vector3(0.5f, 1f, 0.1f));

        // A 2 m by 2 m by 0.2 m wall standing on its base, seated on the native slope.
        Solid("slope-wall", Compound((Box(2f, 2f, 0.2f), new Vector3(0f, 1f, 0f))),
            new Vector3(-1f, 0f, -0.1f), new Vector3(1f, 2f, 0.1f));

        // Neither a collider nor a selection volume, so it has no interaction source.
        assets.Add(Asset("shapeless-prop", new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f), null, null));

        // A 0.5 m post standing on its base.
        Solid("short-post", Compound((new CylinderShape(0.2f, 0.5f), Vector3.Zero)),
            new Vector3(-0.2f, 0f, -0.2f), new Vector3(0.2f, 0.5f, 0.2f));

        // The same post turned upside down: its base is at local y 0.5 and its axis points down to y 0.
        Solid("hanging-post", new CompoundShape(new[]
            {
                new CompoundChild(new CylinderShape(0.2f, 0.5f), new Pose(new Vector3(0f, 0.5f, 0f), new Quaternion(1f, 0f, 0f, 0f))),
            }),
            new Vector3(-0.2f, 0f, -0.2f), new Vector3(0.2f, 0.5f, 0.2f));

        // A 0.6 m tall fallen log lying along x from -1 to 1, its axis turned from local Y onto world X.
        Solid("fallen-log", new CompoundShape(new[]
            {
                new CompoundChild(new CylinderShape(0.3f, 2f),
                    new Pose(new Vector3(-1f, 0.3f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f))),
            }),
            new Vector3(-1f, 0f, -0.3f), new Vector3(1f, 0.6f, 0.3f));

        // A top-level mesh ramp rising from 0 to 0.3 m.
        Solid("mesh-ramp", MeshRamp, new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0.3f, 0.5f));

        // A 100 unit crate authored in centimetres, so it is 1 m across before placement scale.
        string centimetreId = "centimetre-crate.collider";
        resources.Add(Resource(source.Add(centimetreId,
            ColliderBytes(Compound((Box(100f, 100f, 100f), new Vector3(0f, 50f, 0f))))), MapResourceKind.Collider));
        JsonObject centimetreCrate = Asset("centimetre-crate", new Vector3(-50f, 0f, -50f), new Vector3(50f, 100f, 50f),
            centimetreId, null);
        centimetreCrate["sourceUnitsToMetres"] = 0.01f;
        assets.Add(centimetreCrate);

        // A cylinder child whose orientation is not a unit quaternion and would rotate its axis to zero.
        Solid("degenerate-orientation-collider", new CompoundShape(new[]
            {
                new CompoundChild(new CylinderShape(0.2f, 0.5f), new Pose(Vector3.Zero, new Quaternion(0.5f, 0f, 0.5f, 0f))),
            }),
            -Vector3.One, Vector3.One);

        // Point sets with nothing in them, refused before any bounds are taken.
        Solid("empty-hull-collider", new ConvexHullShape(Array.Empty<Vector3>()), -Vector3.One, Vector3.One);
        Solid("empty-mesh-collider", new TriangleMeshShape(Array.Empty<Vector3>(), Array.Empty<int>()), -Vector3.One, Vector3.One);

        // A selection volume and no collider, so the sign can be examined but never blocks.
        string selectionId = "examine-sign.selection";
        resources.Add(Resource(source.Add(selectionId, ColliderBytes(Box(0.8f, 1.2f, 0.1f))), MapResourceKind.Selection));
        assets.Add(Asset("examine-sign", new Vector3(-0.4f, -0.6f, -0.05f), new Vector3(0.4f, 0.6f, 0.05f), null, selectionId));

        // Refused shape data.
        resources.Add(Resource(source.Add("garbage-collider.collider", "not a collision payload"), MapResourceKind.Collider));
        assets.Add(Asset("garbage-collider", -Vector3.One, Vector3.One, "garbage-collider.collider", null));

        var triangle = new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 });
        Solid("mesh-in-compound", Compound((triangle, Vector3.Zero)), Vector3.Zero, new Vector3(1f, 0f, 1f));
        Solid("mesh-in-nested-compound", Compound((Compound((triangle, Vector3.Zero)), Vector3.Zero)),
            Vector3.Zero, new Vector3(1f, 0f, 1f));

        // A valid box payload followed by one stray byte.
        byte[] trailing = ColliderBytes(Box(1f, 1f, 1f)).Append((byte)0).ToArray();
        resources.Add(Resource(source.Add("trailing-byte-collider.collider", trailing), MapResourceKind.Collider));
        assets.Add(Asset("trailing-byte-collider", -Vector3.One, Vector3.One, "trailing-byte-collider.collider", null));

        // Well-formed bytes carrying a non-finite box, which the collision reader refuses.
        Solid("nan-box-collider", Compound((new BoxShape(new Vector3(0.5f, float.NaN, 0.5f)), Vector3.Zero)),
            -Vector3.One, Vector3.One);

        string supportId = "deck-with-support.surface";
        resources.Add(Resource(source.Add(supportId, "surface:deck-with-support"), MapResourceKind.Surface));
        // Its top, not its bottom, is at the placement origin, like bridge-deck.
        Solid("deck-with-support", Compound((Box(4f, 0.25f, 4f), new Vector3(0f, -0.125f, 0f))),
            new Vector3(-2f, -0.25f, -2f), new Vector3(2f, 0f, 2f), supportId);

        WorldAssets((id, collider, min, max) => Solid(id, collider, min, max));

        MapAssetRef root = source.Add(AssetRootId, new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = assets,
            ["resources"] = resources
        }.ToJsonString());
        MapAssetRef[] roots = { root };
        return (MapAssetClosure.Load(roots, source), roots);
    }

    /// <summary>Adds the world build fixture assets through the closure's solid asset helper.</summary>
    static partial void WorldAssets(Action<string, PhysicsShape, Vector3, Vector3> solid);

    // ---------------------------------------------------------------------------------------------------------------
    // Native documents
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The resolver-2 options every fixture document resolves with.</summary>
    internal static readonly MapResolveOptions ResolveOptions =
        new("native-world-fixtures", 1, "native-world-fixture-options", ResolverVersion: 2);

    // The slope rises along +x from its west edge at 0.1763 m per metre, so atan(0.1763) is 10 degrees within 0.002.
    const int SlopeMinCellX = 8;
    const int SlopeRiseTenThousandthsPerCell = 1763;

    /// <summary>The doorway at (0.23, 0, 0.17) on the flat native floor, with the given yaw, scale and numeric ID.</summary>
    internal static NativeFixture Doorway(float yaw, float scale, long? numericId = 1) =>
        Resolve(FloorSurfaces(), OnFloor("doorway", "doorway", 0.23f, 0.17f, yaw, scale, numericId));

    /// <summary>One placement of <paramref name="assetId"/>, with the same ID, at the origin on the flat native
    /// floor.</summary>
    internal static NativeFixture Placed(string assetId, float yaw = 0f, float scale = 1f) =>
        Resolve(FloorSurfaces(), OnFloor(assetId, assetId, 0f, 0f, yaw, scale));

    /// <summary>The mesh-ramp collider: two triangles rising from y 0 at z -0.5 to y 0.3 at z 0.5.</summary>
    internal static TriangleMeshShape MeshRamp { get; } = new(new[]
        {
            new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
            new Vector3(-0.5f, 0.3f, 0.5f), new Vector3(0.5f, 0.3f, 0.5f),
        }, new[] { 0, 2, 1, 1, 2, 3 });

    /// <summary>The 0.2 m tall crate at the origin on the flat native floor.</summary>
    internal static NativeFixture Crate() => Resolve(FloorSurfaces(), OnFloor("crate", "crate", 0f, 0f));

    /// <summary>A placement of an asset with neither a collider nor a selection volume.</summary>
    internal static NativeFixture ShapelessProp() =>
        Resolve(FloorSurfaces(), OnFloor("shapeless-prop", "shapeless-prop", 0f, 0f));

    /// <summary>The slope wall bound to the 10 degree native slope with no authored Y, and the corner wall on the
    /// flat floor.</summary>
    internal static SlopeAndCornerWallsFixture SlopeAndCornerWalls()
    {
        MapSurfaceSet surfaces = FloorSurfaces();
        AddSurface(surfaces, "slope", TenThousandthHeights, SlopeMinCellX, 0, 16, 8,
            (x, _) => (x - SlopeMinCellX) * SlopeRiseTenThousandthsPerCell);
        const float wallX = 10.5f;
        var slopeWall = new MapPlacement
        {
            Id = "slope-wall",
            Kind = "wall",
            AssetId = "slope-wall",
            X = wallX,
            Z = 4f,
            SupportBinding = new(MapSupportBindingKind.Surface, "slope", null, null, null, null),
        };
        NativeFixture f = Resolve(surfaces, slopeWall, OnFloor("corner-wall", "corner-wall", -3f, -3f));
        return new SlopeAndCornerWallsFixture(f.Document, f.Assets, f.Resolved, f.View,
            (wallX - SlopeMinCellX) * SlopeRiseTenThousandthsPerCell / 10000f);
    }

    static readonly MapLatticeFrame CentimetreHeights =
        new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    static readonly MapLatticeFrame TenThousandthHeights =
        new(new(1, 1), new(1, 10000), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    /// <summary>The flat native floor at y 0, spanning x and z from -8 to 8 in four patch slots.</summary>
    static MapSurfaceSet FloorSurfaces()
    {
        var set = new MapSurfaceSet();
        AddSurface(set, "floor", CentimetreHeights, -8, -8, 8, 8, (_, _) => 0);
        return set;
    }

    static MapPlacement OnFloor(string id, string assetId, float x, float z, float yaw = 0f, float scale = 1f,
        long? numericId = null) => new()
        {
            Id = id,
            Kind = "prop",
            AssetId = assetId,
            NumericId = numericId,
            X = x,
            Y = 0f,
            Z = z,
            Yaw = yaw,
            Scale = scale,
        };

    /// <summary>A present support floor over whole cells [minX, maxX) by [minZ, maxZ), split into one patch per
    /// 64-cell slot. <paramref name="height"/> maps a world corner to height units.</summary>
    static void AddSurface(MapSurfaceSet set, string id, MapLatticeFrame frame, int minX, int minZ, int maxX, int maxZ,
        Func<int, int, int> height)
    {
        const int slot = MapPatchKey.SlotCells;
        set.Refs.Add(new MapSurfaceRef(id, frame, MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        for (int slotZ = (int)Math.Floor(minZ / (double)slot); slotZ * slot < maxZ; slotZ++)
            for (int slotX = (int)Math.Floor(minX / (double)slot); slotX * slot < maxX; slotX++)
            {
                int x0 = Math.Max(minX, slotX * slot), x1 = Math.Min(maxX, (slotX + 1) * slot);
                int z0 = Math.Max(minZ, slotZ * slot), z1 = Math.Min(maxZ, (slotZ + 1) * slot);
                int width = x1 - x0, depth = z1 - z0;
                var patch = new MapSurfacePatch
                {
                    Key = new(id, slotX, slotZ),
                    CellMinX = x0 - slotX * slot,
                    CellMinZ = z0 - slotZ * slot,
                    Width = width,
                    Depth = depth,
                    Heights = new int[(width + 1) * (depth + 1)],
                    Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None,
                        MapCellTopology.Auto), width * depth).ToArray(),
                    Presence = new ulong[(width * depth + 63) / 64],
                };
                for (int z = 0; z <= depth; z++)
                    for (int x = 0; x <= width; x++)
                        patch.Heights[z * (width + 1) + x] = height(x0 + x, z0 + z);
                for (int z = 0; z < depth; z++)
                    for (int x = 0; x < width; x++)
                        patch.SetPresent(x, z, true);
                set.Patches.Add(patch.Key, patch);
            }
    }

    /// <summary>Seals the surface digests, builds the resolver-2 document over the shared closure and resolves it
    /// through <see cref="MapResolverV2"/>.</summary>
    static NativeFixture Resolve(MapSurfaceSet surfaces, params MapPlacement[] placements)
    {
        (MapAssetClosure closure, IReadOnlyList<MapAssetRef> roots) = Assets();
        var digests = surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(
            p.Key, MapSurfaceSemantics.PatchDigest(p.Value))).ToArray();
        for (int i = 0; i < surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = surfaces.Refs[i];
            surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, digests.Where(p => p.Key.SurfaceId == surface.Id)),
            };
        }
        var document = new MapDocument
        {
            Id = "native-world",
            // The 64 m storage tile the residency tests align their grids to.
            TileSize = 64f,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = roots.ToList(),
            NumericIdHighWaterMark = 100,
            Bounds = new() { MinX = -64, MinZ = -64, MaxX = 64, MaxZ = 64 },
            PlayableBounds = new() { MinX = -32, MinZ = -32, MaxX = 32, MaxZ = 32 },
            Surfaces = surfaces,
        };
        document.Placements.AddRange(placements);
        MapResolvedDocument resolved = MapResolverV2.Resolve(document, closure,
            MapDocumentSurfaceSource.Capture(document), ResolveOptions).Document;
        return new NativeFixture(document, closure, resolved, MapScopedSurfaces.CompleteView(document.Surfaces));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shape and manifest helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A box from its full size in metres.</summary>
    internal static BoxShape Box(float x, float y, float z) => new(new Vector3(x, y, z) * 0.5f);

    /// <summary>A compound of identity-oriented children at the given local centres.</summary>
    internal static CompoundShape Compound(params (PhysicsShape Shape, Vector3 Centre)[] children) =>
        new(children.Select(child => new CompoundChild(child.Shape, Pose.At(child.Centre))).ToArray());

    /// <summary>The <see cref="PropCollisionFormat"/> version 1 bytes a Collider or Selection resource carries.</summary>
    internal static byte[] ColliderBytes(PhysicsShape shape)
    {
        using var stream = new MemoryStream();
        PropCollisionFormat.Write(shape, stream);
        return stream.ToArray();
    }

    static JsonObject Asset(string id, Vector3 min, Vector3 max, string? colliderId, string? selectionId,
        params string[] supportIds)
    {
        var asset = new JsonObject
        {
            ["id"] = id,
            ["meshResourceId"] = SharedMeshId,
            ["supportResourceIds"] = Ids(supportIds),
            ["materialResourceIds"] = new JsonArray(),
            ["lodResourceIds"] = new JsonArray(),
            ["lightResourceIds"] = new JsonArray(),
            ["sourceUnitsToMetres"] = 1,
            ["renderBounds"] = new JsonObject { ["min"] = Vector(min), ["max"] = Vector(max) },
            ["source"] = "fixtures/" + id + ".blend",
            ["license"] = "CC0",
            ["textured"] = false
        };
        if (colliderId is not null) asset["collisionResourceId"] = colliderId;
        if (selectionId is not null) asset["selectionResourceId"] = selectionId;
        return asset;
    }

    static JsonObject Resource(MapAssetRef reference, MapResourceKind kind) => new()
    {
        ["reference"] = new JsonObject
        {
            ["id"] = reference.Id,
            ["path"] = reference.Path,
            ["sha256"] = reference.Sha256,
            ["payloadVersion"] = reference.PayloadVersion
        },
        ["kind"] = kind.ToString(),
        ["dependencies"] = new JsonArray()
    };

    static JsonObject Vector(Vector3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    static JsonArray Ids(string[] ids) => new(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
}

/// <summary>A resolved fixture document with the closure it resolved against and a complete view of its
/// surfaces.</summary>
internal record NativeFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View);

/// <summary><see cref="NativeWorldFixtures.SlopeAndCornerWalls"/>, with the analytic slope height under the slope
/// wall's origin.</summary>
internal sealed record SlopeAndCornerWallsFixture(MapDocument Document, MapAssetClosure Assets,
    MapResolvedDocument Resolved, MapScopedSurfaces View, float SlopeHeightAtWall)
    : NativeFixture(Document, Assets, Resolved, View);

/// <summary>An in-memory resource source keyed by resource ID. Each added buffer gets its own digest-bearing
/// reference.</summary>
internal sealed class NativeWorldAssetSource : IMapAssetSource
{
    readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

    internal MapAssetRef Add(string id, string text) => Add(id, Encoding.UTF8.GetBytes(text));

    internal MapAssetRef Add(string id, byte[] bytes)
    {
        _bytes[id] = bytes.ToArray();
        return new MapAssetRef(id, "assets/" + id, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 1);
    }

    public ReadOnlyMemory<byte> Read(MapAssetRef reference) => _bytes.TryGetValue(reference.Id, out byte[]? bytes)
        ? bytes : throw new MapDocumentException("Missing fixture resource " + reference.Id);
}
