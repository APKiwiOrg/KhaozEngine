using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Invented acceptance decorations around the released stacked-support and prefab-cave fixtures. Each keeps
/// its released topology and ownership. Only underlay paint, added floors, owners and density layers are new.</summary>
internal static partial class FoliageFixtures
{
    internal const string SurfaceLayer = "surface-cover", BridgeLayer = "bridge-cover", RoomLayer = "room-cover", CaveLayer = "cave-cover";
    internal const string SurfaceFloor = "surface", BridgeFloor = "bridge", DeepFloor = "deep-floor";
    internal const string ShoreLayer = "shore-cover", DryLayer = "dry-cover", FloodLayer = "flood-cover", OtherCaveLayer = "other-cave-cover";

    /// <summary>The released eight-level stack (floors at 0 to 70 m over two by two cells) wrapped as a resolver-2
    /// document with 64 m storage tiles over bounds of +-64 m and three layers: level-0 by Surface, level-3 and level-7 by
    /// Space.</summary>
    internal static FoliageFixture Stacked()
    {
        MapSurfaceSet set = SupportFixtures.EightStacked().Surfaces;
        var bytes = Enumerable.Repeat((byte)230, 9).ToArray();
        MapFoliageLayer Level(string id, MapSupportBinding support) => Layer(id, support, -1, -1, 2, 3, 3, bytes, []);
        FoliageFixture f = Assemble(set, Level("stack-low", Surface("floor-0")));
        // Tight 64 m storage tiles and bounds keep a built world's navigation partition to four tiles.
        f.Document.TileSize = 64;
        f.Document.Bounds = new() { MinX = -64, MinZ = -64, MaxX = 64, MaxZ = 64 };
        f.Document.PlayableBounds = new() { MinX = -64, MinZ = -64, MaxX = 64, MaxZ = 64 };
        f.Document.Foliage = new(1, [Level("stack-low", Surface("floor-0")),
            Level("stack-mid", new(MapSupportBindingKind.Space, null, "level-3", 30.2f, 1, 0.5f)),
            Level("stack-top", new(MapSupportBindingKind.Space, null, "level-7", 70.2f, 1, 0.5f))]);
        return f;
    }

    /// <summary>The released stacked prefab-cave topology: the deep cave (floor -60 to -59 m) and the high cave (floor -40
    /// to -39 m) under world X [0, 4] and Z [-4, 0], each holding its released room prism. Added on the same XZ: floor
    /// "surface" at 0 m over X [-4, 8] and Z [-8, 4], floor "bridge" at 3 m over X [0, 4] and Z [-4, 0] inside exterior
    /// space "deck", and room "upper-room" standing on the bridge over X [1, 3] and Z [-3, -1] from 3 to 6 m. Every floor
    /// cell has underlay 10. Owners: a boulder seated on the surface at (2.5, -2.5) clears by a Solid rule, a canopy
    /// seated on the bridge at (1.5, -1.5) clears by RoofAbove, a gate marker seated on the bridge at (3.5, -3.5) owns a
    /// Door mask and a seep marker seated in the deep cave at (0.5, -0.5) owns a Solid mask on the deep floor. Ordinals:
    /// surface 0, bridge 2, the cave floors unmapped. Layers: surface and bridge by Surface with indoor exclusion, the
    /// upper room by Space without it, the deep cave by Space. <paramref name="without"/> drops one exclusion for a
    /// control: "solid" the boulder, "roof" the canopy, "door" the gate mask, "mask" the seep mask, "room" the bridge
    /// layer's indoor exclusion.</summary>
    internal static FoliageFixture Cave(string without = "")
    {
        PrefabFixture f = PrefabFixtures.Cave("stacked");
        MapDocument document = f.Document;
        WaterFixtures.Surface(document.Surfaces, SurfaceFloor, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, -4, -4, 8, 8, (_, _) => 0);
        WaterFixtures.Surface(document.Surfaces, BridgeFloor, MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, 0, 0, 4, 4, (_, _) => 300);
        foreach (MapSurfacePatch patch in document.Surfaces.Patches.Values.Where(p => !p.Key.SurfaceId.EndsWith("-ceiling", StringComparison.Ordinal)))
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        MapSurfacePatch bridge = document.Surfaces.Patches[new(BridgeFloor, 0, 0)];
        var deck = new MapRecordRef("deck", bridge.Key);
        bridge.Records.Add(new MapSpaceDoc("deck", MapSpaceKind.Exterior, null, null, [], [], [], []));
        bridge.Records.Add(new MapSpaceFootprint("deck-domain", deck, bridge.Key,
            Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * MapPatchKey.SlotCells + x)).ToArray(),
            new(MapBoundKind.SupportFloor, BridgeFloor, null), new(MapBoundKind.OpenTop, null, null)));
        bridge.Records.Add(new MapSpaceDoc("upper-room", MapSpaceKind.Room, deck, null, [], [], [], []));
        bridge.Records.Add(new MapPrismDomain("upper-room-domain", 1, new("upper-room", bridge.Key), deck, "", "upper-storey",
            PrefabFixtures.Domain(1, 1, 3, 3), 3, 6));
        Rehash(document);
        if (without != "solid") document.Placements.Add(Owner("boulder", "boulder", 2.5f, -2.5f, SurfaceFloor));
        if (without != "roof") document.Placements.Add(Owner("canopy", "canopy", 1.5f, -1.5f, BridgeFloor));
        MapBuildingIntent intent = document.BuildingIntent!;
        document.BuildingIntent = new(1, intent.RoofLinks, intent.RoofGroups, intent.Lights,
            [new(1, "gate", "Gate", new(3.5f, 3, -3.5f), 0, "gate", true, []), new(1, "seep", "Seep", new(0.5f, -60, -0.5f), 0, "seep", true, [])])
        { Roofs = intent.Roofs, Storeys = intent.Storeys.Append(new MapStoreyDoc(1, "upper-storey", "Upper", null)).ToArray() };
        MapFoliageCompatibilityOrdinal[] ordinals = [new(SurfaceFloor, 0), new(BridgeFloor, 2)];
        MapFoliageClearanceRule[] rules =
        [
            new("boulder-solid", "item", "boulder", MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f)),
            new("canopy-roof", "item", "canopy", MapFoliageClearanceKind.RoofAbove, new(-0.5f, -0.5f, 0.5f, 0.5f)),
        ];
        MapPrefabReference gate = Owner("gate", MapPrefabChildKind.Marker), seep = Owner("seep", MapPrefabChildKind.Marker);
        var masks = new List<MapFoliageMask>();
        if (without != "door") masks.Add(new("gate-door", gate, Surface(BridgeFloor), MapFoliageClearanceKind.Door, new(-0.25f, -0.25f, 0.25f, 0.25f)));
        if (without != "mask") masks.Add(new("seep-solid", seep, Surface(DeepFloor), MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f)));
        MapFoliageOwnerSupport[] owners = [new(gate, Surface(BridgeFloor)), new(seep, Surface(DeepFloor))];
        MapFoliageLayer Decorated(string id, MapSupportBinding support, bool indoors) => AcceptanceLayer(id, support, -2, -6, 1, 8, 8,
            indoors, 0.75f, ordinals, rules, masks, owners);
        document.Foliage = new(1,
        [
            Decorated(SurfaceLayer, Surface(SurfaceFloor), true),
            Decorated(BridgeLayer, Surface(BridgeFloor), without != "room"),
            Decorated(RoomLayer, new(MapSupportBindingKind.Space, null, "upper-room", 3.2f, 1, 0.5f), false),
            Decorated(CaveLayer, new(MapSupportBindingKind.Space, null, "deep-cave", -59.2f, 1, 0.5f), true),
        ]);
        return new(document, f.Assets);
    }

    /// <summary>The released prefab water caves: instance "a" holds a dry cave (floor -60 m) under the world ocean and a
    /// flooded cave (floor -40 m) holding its own lake, under the exterior floor at -2 m. A second instance "b" of the
    /// same asset sits at X 40 inside an added exterior "far" at -2 m over X [40, 56] and Z [-16, 0]. Prefab floors take
    /// underlay 10 except flooded cell (3, 3), which shows water material 12. The exterior floor takes underlay 10 except
    /// row Z [-1, 0], which shows water material 12. Layers bind by Surface, so a moved cave keeps its support: shore the
    /// exterior, dry and flood the floors of a, other cave b's dry floor. These released caves have no walls, so their
    /// coverage does not build a physical world.</summary>
    internal static FoliageFixture PrefabCaves()
    {
        PrefabFixture water = PrefabFixtures.WaterCaves();
        MapPrefabAsset asset = water.Assets.GetPrefab("hut");
        MapPrefabChild[] children = asset.Children.Select(c => c is MapPrefabSurfaceChild { Surface.Role: MapSurfaceRole.SupportFloor } surface
            ? surface with { Patches = surface.Patches.Select(p => Painted(p, surface.Key.Key == "flooded-floor" ? 3 * p.Width + 3 : -1)).ToArray() }
            : c).ToArray();
        MapPrefabAsset painted = asset with { Children = children };
        MapAssetClosure prefabAssets = PrefabFixtures.LoadAssets(MapPrefabFile.Write(painted), true);
        MapDocument document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(water.Document));
        MapPrefabInstance a = document.Prefabs!.Instances.Single() with { ResourceSha256 = prefabAssets.GetResource("hut").Reference.Sha256 };
        WaterFixtures.Surface(document.Surfaces, "far", MapSurfaceRole.SupportFloor, MapLatticeFrame.ImportedMetreCentimetre, 40, 0, 56, 16, (_, _) => -200);
        MapSurfacePatch far = document.Surfaces.Patches[new("far", 0, 0)];
        far.Records.Add(new MapSpaceDoc("far", MapSpaceKind.Exterior, null, null, [], [], [], []));
        far.Records.Add(new MapSpaceFootprint("far-domain", new("far", far.Key), far.Key,
            Enumerable.Range(0, 16).SelectMany(z => Enumerable.Range(40, 16).Select(x => z * MapPatchKey.SlotCells + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "far", null), new(MapBoundKind.OpenTop, null, null)));
        for (int i = 0; i < far.Cells.Length; i++) far.Cells[i] = far.Cells[i] with { Underlay = 10 };
        MapPrefabInstance b = a with
        {
            Id = "b",
            Transform = new(new(40, 0, 0), 0, 1),
            ParentSpace = new("far", far.Key),
            Bindings = new(a.Bindings.Entries.Select(binding => binding with { WorldId = "b/" + binding.Child.Key, NumericId = null }).ToArray()),
        };
        document.Prefabs = new(1, 1, [a, b], []);
        MapSurfacePatch exterior = document.Surfaces.Patches[new("exterior", 0, 0)];
        for (int i = 0; i < exterior.Cells.Length; i++) exterior.Cells[i] = exterior.Cells[i] with { Underlay = (ushort)(i / exterior.Width == 0 ? 12 : 10) };
        Rehash(document);
        document.NativeAssets = prefabAssets.Roots.ToList();
        MapFoliageLayer Cover(string id, MapSupportBinding support, float originX) =>
            AcceptanceLayer(id, support, originX, -16, 1, 32, 32, true, 0, [], [], [], []);
        document.Foliage = new(1,
        [
            Cover(ShoreLayer, Surface("exterior"), -12),
            Cover(DryLayer, Surface("a/dry-floor"), -12),
            Cover(FloodLayer, Surface("a/flooded-floor"), -12),
            Cover(OtherCaveLayer, Surface("b/dry-floor"), 36),
        ]);
        return new(document, prefabAssets);
    }

    internal const string PinGround = "pin-ground", PinLayer = "pin-cover", PinLegacy = "pin-legacy", PinLegacyLayer = "pin-legacy-cover";
    internal const string DeckLayer = "pin-deck", PlaneFloor = "plane", PlaneLayer = "plane-cover";
    internal static readonly MapLatticeFrame PositiveZMetre = new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);

    /// <summary>The read-reach third term (ruling T6-R9). Space "yard" stands on floor "coarse" (4 m cells) over world X
    /// [0, 36] and on floor "mid" (2 m cells) over X [36, 44], both at 10 m over Z [-12, 0]. Floor "fine" (0.5 m cells)
    /// spans X [38.5, 44] at 13 m, outside the yard. The layer binds the yard by Space with origin (0.5, -12), so chunk
    /// (0, 0) ends at x 32.5, and edge fade 4. A post seated on "mid" at (39, -6) owns a Solid mask on "mid" over x [38.75,
    /// 39.25] and z [-7.25, -4.75], which holds both mid cell centres beside z -6. A sample at (32.25, -6) lies in coarse
    /// cell [32, 36], whose east neighbour centre 38 selects "mid", whose own cell centre is 39. That centre lies 6.5 m
    /// past the chunk: past the 6 m reach of the first two terms over the 4 m cell, inside the 8 m reach with the
    /// third.</summary>
    internal static FoliageFixture ReachLattices()
    {
        var set = new MapSurfaceSet();
        MapLatticeFrame Frame(MapRational cell) => new(cell, new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
        WaterFixtures.Surface(set, "coarse", MapSurfaceRole.SupportFloor, Frame(new(4, 1)), 0, 0, 9, 3, (_, _) => 1000);
        WaterFixtures.Surface(set, "mid", MapSurfaceRole.SupportFloor, Frame(new(2, 1)), 18, 0, 22, 6, (_, _) => 1000);
        WaterFixtures.Surface(set, "fine", MapSurfaceRole.SupportFloor, Frame(new(1, 2)), 77, 0, 88, 24, (_, _) => 1300);
        foreach (MapSurfacePatch patch in set.Patches.Values)
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
        MapSurfacePatch coarse = set.Patches[new("coarse", 0, 0)], mid = set.Patches[new("mid", 0, 0)];
        var yard = new MapRecordRef("yard", coarse.Key);
        coarse.Records.Add(new MapSpaceDoc("yard", MapSpaceKind.Exterior, null, null, [], [], [], []));
        coarse.Records.Add(new MapSpaceFootprint("yard-coarse", yard, coarse.Key,
            Enumerable.Range(0, 3).SelectMany(z => Enumerable.Range(0, 9).Select(x => z * MapPatchKey.SlotCells + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "coarse", null), new(MapBoundKind.OpenTop, null, null)));
        mid.Records.Add(new MapSpaceFootprint("yard-mid", yard, mid.Key,
            Enumerable.Range(0, 6).SelectMany(z => Enumerable.Range(18, 4).Select(x => z * MapPatchKey.SlotCells + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "mid", null), new(MapBoundKind.OpenTop, null, null)));
        var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "yard", 10.2f, 1, 0.5f);
        FoliageFixture f = Assemble(set, Layer(LayerId, binding, 0.5f, -12, 1, 33, 13, Enumerable.Repeat((byte)255, 33 * 13).ToArray(), []));
        f.Document.Placements.Add(ClearingPlacement("post", "mid", 39, -6));
        f.Document.Foliage = new(1, [ClearanceLayer(f.Document.Foliage!.Layers[0], indoors: false, edge: 4,
            masks: [new("post-solid", Owner("post"), Surface("mid"), MapFoliageClearanceKind.Solid, new(-0.25f, -1.25f, 0.25f, 1.25f))])]);
        return f;
    }

    /// <summary>Two surfaces with independently known planes. Floor "plane" uses a PositiveZ metre lattice over world X
    /// [-20, -12] and Z [-6, 2] with corner heights 7x - 3z cm, so world y = 0.07x - 0.03z, and every boundary edge is
    /// subdivided (south and north into three, west and east into two). Prefab "p" holds deck "deck", a 3 by 2 cell
    /// NegativeZ metre lattice with corner heights 10cx + 4cz cm, so local y = 0.1 lx - 0.04 lz, posed at (-31, 2.25, -25)
    /// with yaw 0.371 and scale 1.25. Layer plane-cover binds "plane", layer pin-deck binds "p/deck".</summary>
    internal static FoliageFixture Planes()
    {
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, PlaneFloor, MapSurfaceRole.SupportFloor, PositiveZMetre, -20, -6, -12, 2, (x, z) => (int)(7 * x - 3 * z));
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
            if (patch.Key.SlotZ == 0 || patch.Key.SlotZ == -1) Subdivide(patch, patch.Key.SlotZ == -1, patch.Key.SlotZ == 0);
        }
        FoliageFixture f = Deck(set, (cx, cz) => 10 * cx + 4 * cz, new(new(-31, 2.25f, -25), 0.371f, 1.25f));
        f.Document.Foliage = new(1,
        [
            PinLayerOf(PlaneLayer, Surface(PlaneFloor), -19.5f, -5.75f, 1.25f, 6, 7, [], [], 0, (x, z) => 230),
            PinLayerOf(DeckLayer, Surface("p/deck"), -33, -30, 1, 7, 7, [], [], 0, (x, z) => 230),
        ]);
        return f;
    }

    /// <summary>The platform pin fixture. Floor "pin-ground" uses a native PositiveZ metre lattice over world X [-35, -12]
    /// and Z [-28, -20], meeting the raster's west and south borders, with underlay 10 and twisted corner heights of
    /// 5 ((37x + 11z) mod 23) less 40 cm. Cell (-30, -26) shows road 6 by a diagonal half turned once, (-29, -26) road by
    /// a corner quarter turned three times and (-28, -26) water 12 by a three-quarter cut turned twice. The south row and
    /// the west and east columns are subdivided. Rock, a tree seated on the ground at (-30.6, -26.4) with yaw 0.6 and scale
    /// 1.3, clears by a Solid rule. Floor "pin-legacy" is one legacy-tile slot over X [-64, 0] and Z [0, 64] with the same
    /// twist and underlay, where world cells (-33, 22) are NoDraw, (-32, 22) Blocked and (-31, 22) both. The posed deck of
    /// <see cref="Planes"/> takes twisted heights 30 ((cx + 2cz) mod 3) cm. Layer pin-cover has origin (-34.25, -27.5),
    /// cell 1.5 and 6 by 4 samples of bytes 40 + (53x + 29z) mod 216 with edge fade 0.25. Layer pin-legacy repeats that
    /// raster at origin (-34.25, 20.5), and layer pin-deck covers the deck. Seed 41 and spacing 0.75 throughout.</summary>
    internal static FoliageFixture Pins(Func<int, int, byte>? density = null)
    {
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, PinGround, MapSurfaceRole.SupportFloor, PositiveZMetre, -35, -28, -12, -20, (x, z) => Twist(x, z));
        MapSurfacePatch ground = set.Patches.Values.Single();
        for (int i = 0; i < ground.Cells.Length; i++) ground.Cells[i] = ground.Cells[i] with { Underlay = 10 };
        void Change(long x, long z, Func<MapSurfaceCell, MapSurfaceCell> change)
        {
            int index = (int)(z - ground.Key.SlotZ * MapPatchKey.SlotCells - ground.CellMinZ) * ground.Width
                + (int)(x - ground.Key.SlotX * MapPatchKey.SlotCells - ground.CellMinX);
            ground.Cells[index] = change(ground.Cells[index]);
        }
        Change(-30, -26, c => c with { Overlay = 6, Cut = MapOverlayCut.DiagonalHalf, Rotation = 1 });
        Change(-29, -26, c => c with { Overlay = 6, Cut = MapOverlayCut.CornerQuarter, Rotation = 3 });
        Change(-28, -26, c => c with { Overlay = 12, Cut = MapOverlayCut.CornerThreeQuarter, Rotation = 2 });
        Subdivide(ground, south: true, north: false);
        var legacy = new MapSurfacePatch
        {
            Key = new(PinLegacy, -1, -1),
            Width = 64,
            Depth = 64,
            Heights = new int[65 * 65],
            Cells = Enumerable.Repeat(new MapSurfaceCell(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), 64 * 64).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray(),
        };
        // The released edge extension repeats the last authored corner along the slot's far edges.
        for (int z = 0; z <= 64; z++)
            for (int x = 0; x <= 64; x++) legacy.Heights[z * 65 + x] = Twist(Math.Min(x, 63) - 64, Math.Min(z, 63) - 64);
        // World cell (x, 22) of the NegativeZ slot is local column x + 64 and local row 64 - 23.
        foreach (var (x, flags) in new[] { (-33, MapCellFlags.NoDraw), (-32, MapCellFlags.Blocked), (-31, MapCellFlags.NoDraw | MapCellFlags.Blocked) })
            legacy.Cells[41 * 64 + x + 64] = legacy.Cells[41 * 64 + x + 64] with { Flags = flags };
        set.Refs.Add(new MapSurfaceRef(PinLegacy, MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor, MapPresencePolicy.LegacyTileWorld, null, null, ""));
        set.Patches.Add(legacy.Key, legacy);
        FoliageFixture f = Deck(set, (cx, cz) => 30 * ((cx + 2 * cz) % 3), new(new(-31, 2.25f, -25), 0.371f, 1.25f));
        MapPlacement rock = ClearingPlacement("rock", PinGround, -30.6f, -26.4f);
        rock.Yaw = 0.6f;
        rock.Scale = 1.3f;
        rock.Tags = ["rock"];
        f.Document.Placements.Add(rock);
        Func<int, int, byte> released = (x, z) => (byte)(40 + (53 * x + 29 * z) % 216);
        f.Document.Foliage = new(1,
        [
            PinLayerOf(PinLayer, Surface(PinGround), -34.25f, -27.5f, 1.5f, 6, 4,
                [new("rock-solid", "tree", "rock", MapFoliageClearanceKind.Solid, new(-0.6f, -0.3f, 0.6f, 0.3f))], [], 0.25f, density ?? released),
            PinLayerOf(PinLegacyLayer, Surface(PinLegacy), -34.25f, 20.5f, 1.5f, 6, 4, [], [], 0, released),
            PinLayerOf(DeckLayer, Surface("p/deck"), -33, -30, 1, 7, 7, [], [], 0, (x, z) => (byte)(255 - 9 * x - 5 * z)),
        ]);
        return f;
    }

    static int Twist(long x, long z) => (int)((((37 * x + 11 * z) % 23) + 23) % 23 * 5 - 40);

    // Subdivides the patch's outer south or north row into three segments and its outer west and east columns into two.
    static void Subdivide(MapSurfacePatch patch, bool south, bool north)
    {
        for (int x = 0; x < patch.Width; x++)
        {
            if (south) patch.EdgeSubdivisions.Add(new(x, 0, MapCellEdge.South, 3));
            if (north) patch.EdgeSubdivisions.Add(new(x, patch.Depth - 1, MapCellEdge.North, 3));
        }
        for (int z = 0; z < patch.Depth; z++)
        {
            patch.EdgeSubdivisions.Add(new(0, z, MapCellEdge.West, 2));
            patch.EdgeSubdivisions.Add(new(patch.Width - 1, z, MapCellEdge.East, 2));
        }
    }

    static MapFoliageLayer PinLayerOf(string id, MapSupportBinding support, float originX, float originZ, float cell, int width, int depth,
        IReadOnlyList<MapFoliageClearanceRule> rules, IReadOnlyList<MapFoliageMask> masks, float edge, Func<int, int, byte> density)
    {
        var bytes = new byte[width * depth];
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) bytes[z * width + x] = density(x, z);
        MapFoliageLayer raster = Layer(id, support, originX, originZ, cell, width, depth, bytes, []);
        return new(1, id, support, originX, originZ, cell, width, depth, MapRowDirection.PositiveZ, 41, 0.75f, 0.85f, 1.25f, -0.015f,
            [new("reed-a", 2), new("reed-b", 1.5f)], [],
            [new(6, "soil-material", MapFoliageMaterialKind.Ordinary), new(10, "moss-material", MapFoliageMaterialKind.Ordinary),
             new(12, "pond-material", MapFoliageMaterialKind.Water)],
            new("legacy-tile", 1, 0), true, true, 0, edge, [], rules, masks, [], raster.Chunks);
    }

    // The set wrapped as a resolver-2 document over the shared foliage closure plus prefab "deck-prefab", one 3 by 2 cell
    // deck floor with underlay 10 and the given corner heights in centimetres, placed as instance "p".
    static FoliageFixture Deck(MapSurfaceSet set, Func<int, int, int> heights, MapTransform pose)
    {
        MapSurfacePatch deck = PrefabFixtures.Patch("deck", 3, 2, 0);
        for (int cz = 0; cz <= 2; cz++)
            for (int cx = 0; cx <= 3; cx++) deck.Heights[cz * 4 + cx] = heights(cx, cz);
        for (int i = 0; i < deck.Cells.Length; i++) deck.Cells[i] = new(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);
        var asset = new MapPrefabAsset(1, "deck-prefab", new MapPrefabChild[] { PrefabFixtures.SurfaceChild("deck", MapSurfaceRole.SupportFloor, deck) });
        var (roots, source) = AssetInputs();
        MapAssetRef prefabRef = source.Add("deck-prefab", System.Text.Encoding.UTF8.GetString(MapPrefabFile.Write(asset)));
        MapAssetRef index = source.Add("deck-prefab-index", new System.Text.Json.Nodes.JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new System.Text.Json.Nodes.JsonArray(),
            ["resources"] = new System.Text.Json.Nodes.JsonArray(NativeAssetFixtures.Resource(prefabRef, "Prefab", "mesh", "collider", "selection", "material")),
        }.ToJsonString());
        roots = roots.Append(index).ToArray();
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.Id = "foliage-pin-fixture";
        document.NativeAssets = roots.ToList();
        document.Prefabs = new(1, 1, [new("p", "deck-prefab", prefabRef.Sha256, pose, null,
            new(asset.Children.Select(c => new MapPrefabBinding(c.Key, "p/" + c.Key.Key, null, false)).ToArray()), [])], []);
        return new(document, MapAssetClosure.Load(roots, source));
    }

    /// <summary>A layer of <paramref name="width"/> by <paramref name="depth"/> samples of 230 at <paramref name="cell"/>
    /// metres, seed 29, spacing 0.5, scale 0.9 to 1.3, root offset -0.02, models item (weight 1), visible materials 10
    /// (ordinary) and 12 (water), with edge fade 0.</summary>
    internal static MapFoliageLayer AcceptanceLayer(string id, MapSupportBinding support, float originX, float originZ, float cell,
        int width, int depth, bool indoors, float door, IReadOnlyList<MapFoliageCompatibilityOrdinal> ordinals,
        IReadOnlyList<MapFoliageClearanceRule> rules, IReadOnlyList<MapFoliageMask> masks, IReadOnlyList<MapFoliageOwnerSupport> owners,
        byte density = 230)
    {
        MapFoliageLayer raster = Layer(id, support, originX, originZ, cell, width, depth, Enumerable.Repeat(density, width * depth).ToArray(), []);
        return new(1, id, support, originX, originZ, cell, width, depth, MapRowDirection.PositiveZ, 29, 0.5f, 0.9f, 1.3f, -0.02f,
            [new("item", 1)], [], [new(10, "material", MapFoliageMaterialKind.Ordinary), new(12, "pool-material", MapFoliageMaterialKind.Water)],
            new("legacy-tile", 1, 0), indoors, true, door, 0, ordinals, rules, masks, owners, raster.Chunks);
    }

    /// <summary>Four independent heads, a client and a server of each storage form. Each form is persisted once. Each head
    /// then loads from its own copy of those bytes, its own text or its own copy of the tiled directory, with its own
    /// closure loaded from its own copy of the captured resource bytes. Heads are listed monolithic client, monolithic
    /// server, tiled client, tiled server.</summary>
    internal static IReadOnlyList<FoliageFixture> Heads(FoliageFixture fixture)
    {
        var heads = new List<FoliageFixture>();
        byte[] text = Encoding.UTF8.GetBytes(MapDocumentFile.SaveText(fixture.Document));
        for (int head = 0; head < 2; head++)
            heads.Add(new(MapDocumentFile.LoadText(Encoding.UTF8.GetString(text.ToArray())), PrefabFixtures.ReloadVillageAssets(fixture.Assets)));
        string root = SaveTiled(fixture);
        try
        {
            for (int head = 0; head < 2; head++)
            {
                string copy = root + "-head-" + head.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(copy, Path.GetRelativePath(root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, File.ReadAllBytes(file));
                }
                try { heads.Add(new(MapDocumentFile.LoadTiled(copy), PrefabFixtures.ReloadVillageAssets(fixture.Assets))); }
                finally { Directory.Delete(copy, true); }
            }
        }
        finally { Directory.Delete(root, true); }
        return heads;
    }

    /// <summary>Changes one head's loaded state in place: its first layer's seed and the underlay of the first cell of its
    /// first surface patch, rehashed so the head stays a valid document.</summary>
    internal static void MutateHead(FoliageFixture head)
    {
        MapDocument document = head.Document;
        document.Foliage = new(1, document.Foliage!.Layers
            .Select((layer, i) => i == 0 ? FoliageStorageFixtures.Change(layer, l => l["seed"] = layer.Seed + 1) : layer).ToArray());
        MapSurfacePatch patch = document.Surfaces.Patches.Values.First();
        patch.Cells[0] = patch.Cells[0] with { Underlay = (ushort)(patch.Cells[0].Underlay == 10 ? 12 : 10) };
        Rehash(document);
    }

    /// <summary>Every complete batch of every stored chunk of every layer, generated from a fresh context.</summary>
    internal static IReadOnlyList<MapFoliageSampleBatch> Batches(FoliageFixture fixture)
    {
        MapFoliageGenerationContext context = Context(fixture);
        return context.Foliage!.Layers.SelectMany(layer => layer.ChunkDigests.Select(d => MapFoliageDistribution.Generate(context, layer.Id, d.Key)))
            .ToArray();
    }

    /// <summary>One canonical line per batch and sample: keys, digests, bytes of every float and the model.</summary>
    internal static string Canonical(IEnumerable<MapFoliageSampleBatch> batches) => string.Join("\n", batches.Select(batch =>
        string.Join("|", batch.LayerId, batch.Chunk.X, batch.Chunk.Z, batch.Status, batch.DependencyDigest) + "\n" +
        string.Join("\n", batch.Samples.Select(Canonical))));

    internal static string Canonical(MapFoliageSample s) => string.Join("|", s.Key.Address.LayerId, s.Key.Address.SupportOwnerId,
        s.Key.Address.X, s.Key.Address.Z, s.OccupiedSpaceId, s.AssetId, s.VisibleMaterial, Bits(s.Position.X), Bits(s.Position.Y),
        Bits(s.Position.Z), Bits(s.Orientation.X), Bits(s.Orientation.Y), Bits(s.Orientation.Z), Bits(s.Orientation.W),
        Bits(s.Scale), Bits(s.ThinningRank), s.Face?.ToString());

    static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Saves the document's world in tiled form under a fresh directory and returns the directory.</summary>
    internal static string SaveTiled(FoliageFixture fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "foliage-acceptance-" + Guid.NewGuid().ToString("N"));
        MapDocumentFile.SaveTiled(fixture.Document, root);
        return root;
    }

    static MapPlacement Owner(string id, string tag, float x, float z, string surface) => new()
    {
        Id = id,
        Kind = "item",
        AssetId = "item",
        X = x,
        Z = z,
        Scale = 1,
        Tags = [tag],
        SupportBinding = Surface(surface),
    };

    static MapSurfacePatch Painted(MapSurfacePatch patch, int waterCell)
    {
        MapSurfacePatch copy = patch.Clone();
        for (int i = 0; i < copy.Cells.Length; i++) copy.Cells[i] = copy.Cells[i] with { Underlay = (ushort)(i == waterCell ? 12 : 10) };
        return copy;
    }

    internal static void Rehash(MapDocument document)
    {
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
            };
        }
    }
}
