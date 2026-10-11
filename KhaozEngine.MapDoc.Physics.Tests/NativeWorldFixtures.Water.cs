using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Bounded water world fixtures. Every water document resolves against <see cref="Assets"/> plus one root
/// holding the river material, so the shared closure and every dry fixture keep their identities.</summary>
internal static partial class NativeWorldFixtures
{
    const string RiverMedium = "river-water";
    const string RiverMaterial = "river-material";
    const string WaterRootId = "native-world.water";

    static readonly MapLatticeFrame HalfCentimetreHeights =
        new(new(1, 1), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    /// <summary><see cref="BuildBridge"/>'s deck and parapets at their explicit heights over a floor from x -8 to 8
    /// and z -8 to 8 whose channel x -4 to 4 sits at -1.3 m, with banks at 0 m beyond x 5 and linear slopes between.
    /// Exterior space <c>bank-exterior</c> covers every floor cell under an open top, and body <c>river</c> at
    /// <c>SurfaceY</c> -0.37 fills the channel rectangle.</summary>
    internal static WaterWorldFixture BridgeOverRiver()
    {
        var set = new MapSurfaceSet();
        AddSurface(set, "floor", CentimetreHeights, -8, -8, 8, 8, (x, _) => Math.Abs(x) <= 4 ? -130 : 0);
        var anchor = new MapPatchKey("floor", 0, 0);
        AddExterior(set, "bank-exterior", anchor, "floor");
        MapWaterSet water = Water(1f);
        water.Bodies.Add(Body("river", -0.37f, new[] { new MapWaterRect(-4, -8, 4, 8) }, "bank-exterior", anchor));
        return ResolveWater(set, water, -64, -64, 64, 64, BridgePlacements());
    }

    internal static MapBuiltWorld BuildBridgeOverRiver() => Build(BridgeOverRiver());

    /// <summary>A 256 by 128 m world of 64 m tiles, x 0 to 256 and z -64 to 64, over a flat floor at -2 m with one
    /// exterior space over every cell. Body <c>river</c> at <c>SurfaceY</c> -1 is the L of world rectangles x 8 to 24
    /// by z -48 to 48 and x 24 to 104 by z 8 to 24, reaching storage tiles (0, -1), (0, 0) and (1, 0) and navigation
    /// tiles (0, -1) and (0, 0) of 2 by 2 tile navigation tiles.
    /// <para><paramref name="withPond"/> gives the slots x 128 to 256 by z -64 to 0 their own floor
    /// <c>pond-floor</c> at -2 m, the lower bound of the exterior footprints there, and adds body <c>pond</c> at
    /// <c>SurfaceY</c> -0.5 over x 160 to 200 by z -40 to -20, in navigation tile (1, -1).
    /// <paramref name="pondFloorLowered"/> lowers the pond floor 1 m under the pond through
    /// <see cref="PondLowering"/>. <paramref name="pondFloorHalfHeightUnit"/> changes only the pond floor's height unit
    /// to 1/200 m, so its integer heights and patch digests stay while its bed rises to -1 m. The river never reads the
    /// pond floor.</para></summary>
    internal static WaterWorldFixture LRiver(float wadeSpeedScale = 1f, bool withPond = false, bool pondFloorLowered = false,
        bool pondFloorHalfHeightUnit = false) =>
        BuildLRiverFixture(wadeSpeedScale, withPond, pondFloorLowered, pondFloorHalfHeightUnit, dry: false);

    internal static MapBuiltWorld BuildLRiver() => Build(LRiver());

    /// <summary><see cref="LRiver"/> with an empty bounded water set.</summary>
    internal static MapBuiltWorld BuildDryTwinOfLRiver() =>
        Build(BuildLRiverFixture(1f, withPond: false, pondFloorLowered: false, pondFloorHalfHeightUnit: false, dry: true));

    /// <summary><paramref name="fixture"/>'s document resolved again, after a test changed it.</summary>
    internal static WaterWorldFixture Reresolve(WaterWorldFixture fixture)
    {
        Seal(fixture.Document.Surfaces);
        MapResolvedDocument resolved = MapResolverV2.Resolve(fixture.Document, fixture.Assets,
            MapDocumentSurfaceSource.Capture(fixture.Document), ResolveOptions).Document;
        return new WaterWorldFixture(fixture.Document, fixture.Assets, resolved, MapScopedSurfaces.CompleteView(fixture.Document.Surfaces));
    }

    /// <summary>The effects of raising <c>river</c> in <see cref="LRiver"/> by 0.1 m: both bounds are the river's XZ
    /// at its surface.</summary>
    internal static MapNativeEditEffects RiverLevelEditEffects() => new(
        new MapBox3(8, -1, -48, 104, -1, 48), new MapBox3(8, -0.9, -48, 104, -0.9, 48),
        Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MapDigestChange>(),
        MapNativeInvalidation.Medium | MapNativeInvalidation.Residency | MapNativeInvalidation.Nav)
    {
        WaterBodyIds = new[] { "river" },
    };

    /// <summary>The prepared effects of <see cref="PondLowering"/> on <see cref="LRiver"/> with the pond, naming both
    /// bodies that cover the edited space and adding <see cref="MapNativeInvalidation.Medium"/>, as the editor names
    /// affected bodies.</summary>
    internal static MapNativeEditEffects PondFloorEditEffects()
    {
        MapNativeEditEffects effects = MapTerrainEdits.Prepare(LRiver(withPond: true).Document.Surfaces, PondLowering()).Effects;
        return effects with
        {
            WaterBodyIds = new[] { "pond", "river" },
            Invalidates = effects.Invalidates | MapNativeInvalidation.Medium,
        };
    }

    /// <summary>The static a ray cast straight down from <paramref name="from"/> under <paramref name="lease"/> hits
    /// first.</summary>
    internal static MapStaticOwner DownwardHit(MapPhysicsRegistration registration, IPhysicsQueryLease lease, Vector3 from)
    {
        lease.AssertCurrent();
        if (!lease.SourceWorld.Raycast(from - lease.Origin, -Vector3.UnitY, 1000f, out RayHit hit) || hit.Body is not { } handle)
            throw new InvalidOperationException("the downward ray hit no static");
        return registration.TryOwner(handle, out MapStaticOwner? owner)
            ? owner : throw new InvalidOperationException("the downward ray hit a static the registration does not own");
    }

    static WaterWorldFixture BuildLRiverFixture(float wadeSpeedScale, bool withPond, bool pondFloorLowered,
        bool pondFloorHalfHeightUnit, bool dry)
    {
        var set = new MapSurfaceSet();
        AddSurface(set, "floor", CentimetreHeights, 0, -64, 256, 64, (_, _) => -200);
        var anchor = new MapPatchKey("floor", 0, 0);
        if (withPond)
        {
            set.Patches.Remove(new MapPatchKey("floor", 2, -1));
            set.Patches.Remove(new MapPatchKey("floor", 3, -1));
            AddSurface(set, "pond-floor", pondFloorHalfHeightUnit ? HalfCentimetreHeights : CentimetreHeights, 128, -64, 256, 0,
                (_, _) => -200);
            AddExterior(set, "exterior", anchor, "floor", "pond-floor");
        }
        else AddExterior(set, "exterior", anchor, "floor");
        if (pondFloorLowered) set = MapTerrainEdits.Prepare(set, PondLowering()).Candidate;

        MapWaterSet water = Water(wadeSpeedScale);
        if (dry) water = new MapWaterSet();
        else
        {
            water.Bodies.Add(Body("river", -1f, new[] { new MapWaterRect(8, -48, 24, 48), new MapWaterRect(24, 8, 104, 24) },
                "exterior", anchor));
            if (withPond)
                water.Bodies.Add(Body("pond", -0.5f, new[] { new MapWaterRect(160, -40, 200, -20) }, "exterior", anchor));
        }
        return ResolveWater(set, water, 0, -64, 256, 64);
    }

    /// <summary>Lowers every <c>pond-floor</c> corner under the pond, x 160 to 200 by z -40 to -20, from -2 m to
    /// -3 m. The corners at x 192 lie in both slot patches and are set in each.</summary>
    static MapCompositeEdit PondLowering()
    {
        const int cornerZ = 24, depth = 21; // z -40 to -20 in slot -1, which starts at z -64.
        return new MapCompositeEdit(new MapTerrainEdit[]
        {
            new MapSetCornerHeights(new MapPatchKey("pond-floor", 2, -1), 32, cornerZ, 33, depth, Enumerable.Repeat(-300, 33 * depth).ToArray()),
            new MapSetCornerHeights(new MapPatchKey("pond-floor", 3, -1), 0, cornerZ, 9, depth, Enumerable.Repeat(-300, 9 * depth).ToArray()),
        });
    }

    /// <summary>Adds exterior space <paramref name="spaceId"/>, anchored at <paramref name="anchor"/>, and one
    /// footprint over every cell of each patch of <paramref name="floors"/>, each on its own floor under an open
    /// top.</summary>
    static void AddExterior(MapSurfaceSet set, string spaceId, MapPatchKey anchor, params string[] floors)
    {
        set.Patches[anchor].Records.Add(new MapSpaceDoc(spaceId, MapSpaceKind.Exterior, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        foreach (MapSurfacePatch patch in set.Patches.Values.Where(p => floors.Contains(p.Key.SurfaceId)).OrderBy(p => p.Key).ToArray())
        {
            int[] cells = Enumerable.Range(patch.CellMinZ, patch.Depth)
                .SelectMany(z => Enumerable.Range(patch.CellMinX, patch.Width).Select(x => z * MapPatchKey.SlotCells + x)).ToArray();
            patch.Records.Add(new MapSpaceFootprint(
                FormattableString.Invariant($"{spaceId}-{patch.Key.SurfaceId}-{patch.Key.SlotX}-{patch.Key.SlotZ}"),
                new MapRecordRef(spaceId, anchor), patch.Key, cells,
                new MapBoundRef(MapBoundKind.SupportFloor, patch.Key.SurfaceId, null), new MapBoundRef(MapBoundKind.OpenTop, null, null)));
        }
    }

    static MapWaterSet Water(float wadeSpeedScale)
    {
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(RiverMedium, MapWaterMediumKind.Water, wadeSpeedScale));
        return water;
    }

    /// <summary>A body over whole-metre world rectangles in the fixture lattice, covering one space.</summary>
    static MapWaterBodyDoc Body(string id, float surfaceY, MapWaterRect[] rects, string spaceId, MapPatchKey anchor) =>
        new(id, surfaceY, RiverMedium, RiverMaterial, new MapWaterDomainDoc(CentimetreHeights, rects, Array.Empty<MapWaterRing>()),
            new[] { new MapRecordRef(spaceId, anchor) });

    /// <summary>Seals the surface digests and resolves a 64 m tile document with the given storage and playable
    /// bounds and water section, over <see cref="Assets"/> plus a root holding the river material.</summary>
    static WaterWorldFixture ResolveWater(MapSurfaceSet surfaces, MapWaterSet water, int minX, int minZ, int maxX, int maxZ,
        params MapPlacement[] placements)
    {
        (MapAssetRef[] shared, NativeWorldAssetSource source) = AssetInputs();
        MapAssetRef material = source.Add(RiverMaterial, "material:" + RiverMaterial);
        MapAssetRef root = source.Add(WaterRootId, new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(Resource(material, MapResourceKind.Material)),
        }.ToJsonString());
        MapAssetRef[] roots = shared.Append(root).ToArray();
        MapAssetClosure closure = MapAssetClosure.Load(roots, source);
        Seal(surfaces);
        var document = new MapDocument
        {
            Id = "native-world-water",
            TileSize = 64f,
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            NativeAssets = roots.ToList(),
            NumericIdHighWaterMark = 100,
            Bounds = new() { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ },
            PlayableBounds = new() { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ },
            Surfaces = surfaces,
            Water = water,
        };
        document.Placements.AddRange(placements);
        MapResolvedDocument resolved = MapResolverV2.Resolve(document, closure,
            MapDocumentSurfaceSource.Capture(document), ResolveOptions).Document;
        return new WaterWorldFixture(document, closure, resolved, MapScopedSurfaces.CompleteView(document.Surfaces));
    }
}

/// <summary>A resolved water fixture, with world points as frame points at the world origin.</summary>
internal sealed record WaterWorldFixture(MapDocument Document, MapAssetClosure Assets, MapResolvedDocument Resolved,
    MapScopedSurfaces View) : NativeFixture(Document, Assets, Resolved, View)
{
    /// <summary>The frame point for world metres (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
    internal MapFramePoint Point(float x, float y, float z) => new(WorldFrame.Origin, new Vector3(x, y, z));
}
