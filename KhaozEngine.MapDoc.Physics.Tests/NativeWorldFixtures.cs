using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Shared native world fixtures, built through public MapDoc and Physics APIs only. Every fixture collider
/// sits with its bottom at the placement origin unless a fixture states otherwise. The physics seam centres a box on
/// its pose, so a box child is lifted by half its height. A cylinder stands on its base at its pose, at top level and
/// as a compound child, so a cylinder child sits at local y 0.</summary>
internal static class NativeWorldFixtures
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

        MapAssetRef root = source.Add(AssetRootId, new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = assets,
            ["resources"] = resources
        }.ToJsonString());
        MapAssetRef[] roots = { root };
        return (MapAssetClosure.Load(roots, source), roots);
    }

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
    // Terrain documents
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The capsule the feature query tests install: radius, length and the gap <see cref="ProbeNear"/> leaves
    /// between its nearest point and the face.</summary>
    const float ProbeRadius = 0.2f, ProbeLength = 0.4f, ProbeGap = 0.005f;

    // Every compiled face a terrain fixture registers, keyed by face. Fixture surface and strip IDs are unique per
    // geometry, so one face never registers two normals.
    static readonly ConcurrentDictionary<MapFaceKey, Vector3> CompiledNormals = new();

    /// <summary>A stacked cave in metre cells over [0, 6) by [0, 6): a lower floor at y 0 and a lower ceiling at y 3
    /// that drops to y 1.2 over corners x and z at most 2, an upper floor at y 3.5 and an upper ceiling at y 6.5.
    /// The shaft cells [3, 5) by [3, 5) are open through the lower ceiling and the upper floor, and four wall strips
    /// join those two edges around the shaft. Spaces: the lower room, the shaft and the upper room, linked through
    /// the shaft.</summary>
    internal static StackedCaveFixture StackedCave()
    {
        var set = new MapSurfaceSet();
        Func<int, int, bool> solid = (x, z) => !(x is >= 3 and < 5 && z is >= 3 and < 5);
        MapSurfacePatch lowerFloor = AddPatch(set, "cave-lower-floor", MapSurfaceRole.SupportFloor, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 0);
        MapSurfacePatch lowerCeiling = AddPatch(set, "cave-lower-ceiling", MapSurfaceRole.Ceiling, CentimetreHeights,
            0, 0, 6, 6, (x, z) => x <= 2 && z <= 2 ? 120 : 300, solid);
        MapSurfacePatch upperFloor = AddPatch(set, "cave-upper-floor", MapSurfaceRole.SupportFloor, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 350, solid);
        MapSurfacePatch upperCeiling = AddPatch(set, "cave-upper-ceiling", MapSurfaceRole.Ceiling, CentimetreHeights,
            0, 0, 6, 6, (_, _) => 650);
        int[] shaft = { 195, 196, 259, 260 };
        int[] room = Enumerable.Range(0, 6).SelectMany(z => Enumerable.Range(0, 6).Where(x => solid(x, z))
            .Select(x => z * 64 + x)).ToArray();
        MapRecordRef Ref(string id, MapSurfacePatch anchor) => new(id, anchor.Key);
        MapBoundRef Bound(MapBoundKind kind, MapSurfacePatch surface) => new(kind, surface.Key.SurfaceId, null);
        MapBoundRef Opening(MapSurfacePatch anchor) => new(MapBoundKind.HorizontalOpening, null,
            Ref(anchor == lowerCeiling ? "lower-shaft-opening" : "upper-shaft-opening", anchor));

        lowerCeiling.Records.Add(new MapHorizontalOpening("lower-shaft-opening", lowerCeiling.Key, shaft));
        upperFloor.Records.Add(new MapHorizontalOpening("upper-shaft-opening", upperFloor.Key, shaft));
        var walls = new List<MapBoundaryRef>();
        foreach (var (name, x0, z0, x1, z1, facing) in new[]
        {
            ("west", 3, 3, 3, 5, MapStripFacing.Front), ("east", 5, 3, 5, 5, MapStripFacing.Back),
            ("south", 3, 3, 5, 3, MapStripFacing.Back), ("north", 3, 5, 5, 5, MapStripFacing.Front),
        })
        {
            string id = "shaft-" + name;
            lowerFloor.Records.Add(EdgeChain(id + "-lower", lowerCeiling.Key, x0, z0, x1, z1));
            lowerFloor.Records.Add(EdgeChain(id + "-upper", upperFloor.Key, x0, z0, x1, z1));
            lowerFloor.Records.Add(new MapWallStrip(id, Ref(id + "-lower", lowerFloor), Ref(id + "-upper", lowerFloor), facing, 1));
            walls.Add(new(Ref(id, lowerFloor), facing == MapStripFacing.Front ? MapSide.Front : MapSide.Back));
        }
        MapRecordRef[] link = { Ref("shaft-link", lowerFloor) };
        lowerFloor.Records.Add(CaveSpace("lower-room", Array.Empty<MapBoundaryRef>(), link));
        lowerFloor.Records.Add(CaveSpace("shaft", walls, Array.Empty<MapRecordRef>()));
        lowerFloor.Records.Add(new MapSpaceFootprint("lower-room-cells", Ref("lower-room", lowerFloor), lowerFloor.Key, room,
            Bound(MapBoundKind.SupportFloor, lowerFloor), Bound(MapBoundKind.Ceiling, lowerCeiling)));
        lowerFloor.Records.Add(new MapSpaceFootprint("lower-room-shaft-cells", Ref("lower-room", lowerFloor), lowerFloor.Key,
            shaft, Bound(MapBoundKind.SupportFloor, lowerFloor), Opening(lowerCeiling)));
        lowerFloor.Records.Add(new MapSpaceFootprint("shaft-cells", Ref("shaft", lowerFloor), lowerFloor.Key, shaft,
            Opening(lowerCeiling), Opening(upperFloor)));
        lowerFloor.Records.Add(new MapVerticalLink("shaft-link", Ref("upper-room", upperFloor), Ref("lower-room", lowerFloor),
            new[] { Ref("lower-shaft-opening", lowerCeiling), Ref("upper-shaft-opening", upperFloor) },
            Array.Empty<MapRecordRef>(), walls.Select(w => w.Record).ToArray()));
        upperFloor.Records.Add(CaveSpace("upper-room", Array.Empty<MapBoundaryRef>(), link));
        upperFloor.Records.Add(new MapSpaceFootprint("upper-room-cells", Ref("upper-room", upperFloor), upperFloor.Key, room,
            Bound(MapBoundKind.SupportFloor, upperFloor), Bound(MapBoundKind.Ceiling, upperCeiling)));
        upperFloor.Records.Add(new MapSpaceFootprint("upper-room-shaft-cells", Ref("upper-room", upperFloor), upperFloor.Key,
            shaft, Opening(upperFloor), Bound(MapBoundKind.Ceiling, upperCeiling)));

        TerrainFixture f = Terrain(set);
        return new StackedCaveFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount,
            new Vector3(1.5f, 0f, 4.5f), new Vector3(0.5f, 1.2f, 0.5f), new Vector3(4f, 3.25f, 4f), new Vector3(1.5f, 3.5f, 4.5f));
    }

    /// <summary>One 8 by 8 metre-cell floor rising 0.02 m per metre along x from y 1.2, with every patch-boundary
    /// cell edge subdivided into 4 segments. Under a cap of 64 each 4 by 4 quadrant is over the cap, so the chunks
    /// are the 2 by 2 cell blocks and their boundaries run along x and z 2, 4 and 6 inside the one patch. The probe
    /// points lie on those boundaries, including shared vertices.</summary>
    internal static FinePatchFixture FinePatch()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch patch = AddPatch(set, "fine", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 8, 8,
            (x, _) => 120 + 2 * x);
        for (int i = 0; i < 8; i++)
        {
            patch.EdgeSubdivisions.Add(new(i, 0, MapCellEdge.South, 4));
            patch.EdgeSubdivisions.Add(new(i, 7, MapCellEdge.North, 4));
            patch.EdgeSubdivisions.Add(new(0, i, MapCellEdge.West, 4));
            patch.EdgeSubdivisions.Add(new(7, i, MapCellEdge.East, 4));
        }
        TerrainFixture f = Terrain(set);
        var points = new (float X, float Z)[]
        {
            (2f, 1f), (2f, 2f), (2f, 3.5f), (4f, 4f), (4f, 5.25f), (6f, 0.75f),
            (1.5f, 2f), (3f, 4f), (6.5f, 6f), (5f, 2f), (6f, 6f), (4f, 7.5f),
        };
        return new FinePatchFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount,
            points.Select(p => new Vector3(p.X, FineHeight(p.X), p.Z)).ToArray());
    }

    internal static float FineHeight(float x) => (float)(1.2 + 0.02 * x);

    /// <summary>A LegacyTileWorld row of four cells under the legacy exterior recipe: cell 1 is NoDraw, cell 2 has no
    /// underlay and cell 3 is absent, so cells 1 and 2 are fallback cells.</summary>
    internal static LegacyFallbackFixture LegacyExteriorWithFallback()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch row = AddPatch(set, "legacy-row", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            0, 0, 4, 1, (_, _) => 0, policy: MapPresencePolicy.LegacyTileWorld);
        row.Heights = new[] { 0, 100, 300, 300, 300, 0, 200, 600, 600, 600 };
        row.Cells[1] = row.Cells[1] with { Flags = MapCellFlags.NoDraw };
        row.Cells[2] = row.Cells[2] with { Underlay = 0 };
        row.SetPresent(3, 0, false);
        row.Records.Add(new MapSpaceDoc("legacy-world", MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        row.Records.Add(new MapSpaceFootprint("legacy-world-cells", new("legacy-world", row.Key), row.Key, new[] { 0, 1, 2 },
            new(MapBoundKind.LegacyExteriorV1, "legacy-row", null), new(MapBoundKind.OpenTop, null, null)));
        TerrainFixture f = Terrain(set);
        return new LegacyFallbackFixture(f.Document, f.Assets, f.Resolved, f.View, f.CompiledFaceCount, row.Key, new[] { 1, 2 });
    }

    /// <summary>One flat metre cell with all four edges subdivided into 32 segments: 2 x (31 + 31 + 3) = 130 faces in
    /// one slot cell.</summary>
    internal static TerrainFixture DenseCell()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch patch = AddPatch(set, "dense", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 1, 1, (_, _) => 0);
        foreach (MapCellEdge edge in Enum.GetValues<MapCellEdge>()) patch.EdgeSubdivisions.Add(new(0, 0, edge, 32));
        return Terrain(set);
    }

    /// <summary>A straight front-facing wall 2.5 m tall at z 0.5, from x 0.5 to x 0.5 plus
    /// <paramref name="lengthMetres"/> in 4 m segments with a shorter last one, hosted on a 2 by 2 cell yard
    /// floor.</summary>
    internal static TerrainFixture LongWallStrip(float lengthMetres)
    {
        long halves = checked((long)Math.Round(lengthMetres * 2.0));
        string id = "long-wall-" + halves;
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, id + "-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 2, 2, 2, (_, _) => 0);
        var points = new List<(long X, long Z)>();
        for (long h = 0; h < halves; h += 8) points.Add((1 + h, 1));
        points.Add((1 + halves, 1));
        AddStrip(yard, id, points, MapStripFacing.Front);
        return Terrain(set);
    }

    /// <summary>A 4 by 4 cell LegacyTileWorld patch with every corner at <paramref name="heightMetres"/>.</summary>
    internal static TerrainFixture HighLegacyPatch(float heightMetres)
    {
        int centimetres = checked((int)Math.Round(heightMetres * 100.0));
        var set = new MapSurfaceSet();
        AddPatch(set, "high-legacy-" + centimetres, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre,
            0, 0, 4, 4, (_, _) => centimetres, policy: MapPresencePolicy.LegacyTileWorld);
        return Terrain(set);
    }

    /// <summary>A two-sided wall 2.5 m tall at x 1.5, from z 0.5 to z 8.5 in 2 m segments, hosted on a yard
    /// floor.</summary>
    internal static TerrainFixture TwoSidedStrip()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "two-sided-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 4, 0, 2, 2, (_, _) => 0);
        AddStrip(yard, "two-sided-wall", new (long, long)[] { (3, 1), (3, 5), (3, 9), (3, 13), (3, 17) }, MapStripFacing.TwoSided);
        return Terrain(set);
    }

    /// <summary>An acquired view that is not complete: its yard holds a wall strip whose chains do not exist.</summary>
    internal static MapScopedSurfaces IncompleteView()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "dangling-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 2, 2, (_, _) => 0);
        yard.Records.Add(new MapWallStrip("dangling-wall", new("missing-lower", yard.Key), new("missing-upper", yard.Key),
            MapStripFacing.Front, 1));
        var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(-1f, -1f), new Vector2(3f, 3f), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
        MapScopedSurfaces view = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(Resolve(set).Document), scope,
            Array.Empty<string>());
        return view.Status == MapAcquireStatus.Incomplete ? view
            : throw new InvalidOperationException("incomplete view fixture acquired as " + view.Status);
    }

    /// <summary>A complete view whose wall strip's upper chain runs along a declared ceiling that has no patch.</summary>
    internal static MapScopedSurfaces UnresolvedStripView()
    {
        var set = new MapSurfaceSet();
        MapSurfacePatch yard = AddPatch(set, "unresolved-yard", MapSurfaceRole.SupportFloor, CentimetreHeights, 0, 0, 2, 2, (_, _) => 0);
        set.Refs.Add(new MapSurfaceRef("unresolved-lid", CentimetreHeights, MapSurfaceRole.Ceiling, MapPresencePolicy.Native,
            null, null, ""));
        yard.Records.Add(EdgeChain("unresolved-lower", yard.Key, 0, 0, 2, 0));
        yard.Records.Add(EdgeChain("unresolved-upper", new("unresolved-lid", 0, 0), 0, 0, 2, 0));
        yard.Records.Add(new MapWallStrip("unresolved-wall", new("unresolved-lower", yard.Key), new("unresolved-upper", yard.Key),
            MapStripFacing.Front, 1));
        return MapScopedSurfaces.CompleteView(set);
    }

    /// <summary>Whether the backend front of chunk triangle <paramref name="t"/>, <c>cross(C - A, B - A)</c> of its
    /// emitted vertices, points along the normal R2 compiled for its face.</summary>
    internal static bool BackendFrontAlongCompiledNormal(MapTerrainChunk chunk, int t)
    {
        if (!CompiledNormals.TryGetValue(chunk.TriangleOwners[t], out Vector3 normal)) return false;
        (Vector3 a, Vector3 b, Vector3 c) = Triangle(chunk, t);
        return Vector3.Dot(Vector3.Normalize(Vector3.Cross(c - a, b - a)), normal) > 0.999f;
    }

    /// <summary>The world pose of an upright probe capsule whose nearest point lies <see cref="ProbeGap"/> in front of
    /// one of the chunk's faces. A floor or ceiling face takes the cap end nearest its centroid. A vertical wall face
    /// is parallel to an upright capsule, which the feature query refuses as ambiguous, so a wall probe sits just above
    /// the midpoint of a top edge, in front of the face.</summary>
    internal static Pose ProbeNear(MapTerrainChunk chunk)
    {
        var anchor = new Vector3(chunk.Anchor.X, chunk.Anchor.Y, chunk.Anchor.Z);
        for (int t = 0; t < chunk.TriangleOwners.Count; t++)
        {
            (Vector3 a, Vector3 b, Vector3 c) = Triangle(chunk, t);
            Vector3 front = Vector3.Normalize(Vector3.Cross(c - a, b - a));
            Vector3 reach = front * (ProbeRadius + ProbeGap);
            if (front.Y != 0f)
                return Pose.At(anchor + (a + b + c) / 3f + reach + Vector3.UnitY * MathF.CopySign(ProbeLength / 2f, front.Y));
            float top = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            Vector3[] edge = new[] { a, b, c }.Where(v => v.Y == top).ToArray();
            if (edge.Length == 2)
                return Pose.At(anchor + (edge[0] + edge[1]) / 2f + reach + Vector3.UnitY * (0.001f + ProbeLength / 2f));
        }
        throw new InvalidOperationException("chunk " + chunk.ChunkId + " has no probe face");
    }

    static (Vector3 A, Vector3 B, Vector3 C) Triangle(MapTerrainChunk chunk, int t) => (
        chunk.Shape.Vertices[chunk.Shape.Indices[3 * t]], chunk.Shape.Vertices[chunk.Shape.Indices[3 * t + 1]],
        chunk.Shape.Vertices[chunk.Shape.Indices[3 * t + 2]]);

    /// <summary>Validates the topology references, resolves the document and registers every face R2 compiles from
    /// its present patches and wall strips.</summary>
    static TerrainFixture Terrain(MapSurfaceSet set)
    {
        IReadOnlyList<string> errors = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (errors.Count != 0)
            throw new InvalidOperationException("terrain fixture reference validation failed: " + string.Join(", ", errors));
        NativeFixture f = Resolve(set);
        int faces = 0;
        foreach (MapPatchKey key in f.View.Witness.Present.Select(p => p.Key))
        {
            MapSurfaceRef surface = f.View.Surfaces.Single(s => s.Id == key.SurfaceId);
            if (surface.Role != MapSurfaceRole.PaintOverride)
                faces += Register(MapSurfaceCompiler.Compile(surface, f.View.Patch(key).Patch!).Faces);
            foreach (MapWallStrip strip in f.View.RecordsIn(key).OfType<MapWallStrip>())
                faces += Register(MapWallStripCompiler.Compile(strip, Chain(strip.LowerChain), Chain(strip.UpperChain)).Faces);
        }
        return new TerrainFixture(f.Document, f.Assets, f.Resolved, f.View, faces);

        MapChainResolution Chain(MapRecordRef reference)
        {
            f.View.TryRecord(reference, out MapTopologyRecord? record, out _);
            return MapBoundaryGeometry.ResolveChain((MapBoundaryChain)record!, f.View);
        }
    }

    static int Register(IReadOnlyList<MapCompiledFace> faces)
    {
        foreach (MapCompiledFace face in faces)
            if (CompiledNormals.GetOrAdd(face.Key, face.Normal) != face.Normal)
                throw new InvalidOperationException("terrain fixture face " + face.Key + " registered with two normals");
        return faces.Count;
    }

    /// <summary>A full rectangle of cells [minX, minX + width) by [minZ, minZ + depth) in slot (0, 0) of a new surface.
    /// <paramref name="height"/> maps a lattice corner to height units and <paramref name="present"/> a cell to its
    /// presence.</summary>
    static MapSurfacePatch AddPatch(MapSurfaceSet set, string id, MapSurfaceRole role, MapLatticeFrame frame, int minX,
        int minZ, int width, int depth, Func<int, int, int> height, Func<int, int, bool>? present = null,
        MapPresencePolicy policy = MapPresencePolicy.Native)
    {
        set.Refs.Add(new MapSurfaceRef(id, frame, role, policy, null, null, ""));
        var patch = new MapSurfacePatch
        {
            Key = new(id, 0, 0),
            CellMinX = minX,
            CellMinZ = minZ,
            Width = width,
            Depth = depth,
            Heights = new int[(width + 1) * (depth + 1)],
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None,
                MapCellTopology.Auto), width * depth).ToArray(),
            Presence = new ulong[(width * depth + 63) / 64],
        };
        for (int z = 0; z <= depth; z++)
            for (int x = 0; x <= width; x++)
                patch.Heights[z * (width + 1) + x] = height(minX + x, minZ + z);
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                patch.SetPresent(x, z, present?.Invoke(minX + x, minZ + z) ?? true);
        set.Patches.Add(patch.Key, patch);
        return patch;
    }

    /// <summary>A source-edge chain along whole lattice corners of <paramref name="source"/>.</summary>
    static MapBoundaryChain EdgeChain(string id, MapPatchKey source, int x0, int z0, int x1, int z1)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));
        return new(id, MapChainKind.SurfaceEdge, source, Enumerable.Range(0, steps + 1).Select(i => new MapChainVertex(
            new(source.SurfaceId, MapLatticeAddress.Corner(x0 + Math.Sign(x1 - x0) * i, z0 + Math.Sign(z1 - z0) * i)),
            null)).ToArray());
    }

    /// <summary>An authored strip from y 0 to y 2.5 through half-metre lattice points of the host's surface.</summary>
    static void AddStrip(MapSurfacePatch host, string id, IReadOnlyList<(long X, long Z)> halves, MapStripFacing facing)
    {
        MapChainVertex[] Chain(int height) => halves.Select(p => new MapChainVertex(
            new(host.Key.SurfaceId, MapLatticeAddress.Create(p.X, p.Z, 2)), height)).ToArray();
        host.Records.Add(new MapBoundaryChain(id + "-lower", MapChainKind.Authored, null, Chain(0)));
        host.Records.Add(new MapBoundaryChain(id + "-upper", MapChainKind.Authored, null, Chain(250)));
        host.Records.Add(new MapWallStrip(id, new(id + "-lower", host.Key), new(id + "-upper", host.Key), facing, 1));
    }

    static MapSpaceDoc CaveSpace(string id, IReadOnlyList<MapBoundaryRef> walls, IReadOnlyList<MapRecordRef> links) =>
        new(id, MapSpaceKind.Cave, null, null, Array.Empty<string>(), walls, Array.Empty<MapBoundaryRef>(), links);

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

/// <summary>A terrain fixture with the number of faces R2 compiles from its present patches and wall strips.</summary>
internal record TerrainFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount) : NativeFixture(Document, Assets, Resolved, View)
{
    /// <summary>See <see cref="NativeWorldFixtures.ProbeNear"/>.</summary>
    internal Pose ProbeNear(MapTerrainChunk chunk) => NativeWorldFixtures.ProbeNear(chunk);
}

/// <summary><see cref="NativeWorldFixtures.FinePatch"/>, with world points on its internal chunk boundaries.</summary>
internal sealed record FinePatchFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, IReadOnlyList<Vector3> SeamProbePoints)
    : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount)
{
    /// <summary>The analytic floor height, which depends on x only.</summary>
    internal float HeightAt(float x, float z) => NativeWorldFixtures.FineHeight(x);
}

/// <summary><see cref="NativeWorldFixtures.LegacyExteriorWithFallback"/>, with its fallback slot cells.</summary>
internal sealed record LegacyFallbackFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, MapPatchKey Row, IReadOnlyList<int> FallbackCells)
    : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount)
{
    internal int FallbackCellCount => FallbackCells.Count;

    internal bool IsFallbackFace(MapFaceKey key) => key.Patch == Row && FallbackCells.Contains(key.Primitive);
}

/// <summary><see cref="NativeWorldFixtures.StackedCave"/>, with a point on the lower floor, under the low ceiling,
/// inside the shaft between the slabs and on the upper floor.</summary>
internal sealed record StackedCaveFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View, int CompiledFaceCount, Vector3 LowerFloorPoint, Vector3 LowCeilingPoint, Vector3 ShaftPoint,
    Vector3 UpperFloorPoint) : TerrainFixture(Document, Assets, Resolved, View, CompiledFaceCount);

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
