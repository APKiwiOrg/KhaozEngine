using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal static partial class FoliageFixtures
{
    /// <summary>The solid fixture's floors without owners, plus a native "coarse" support floor of 4 m cells at 10 m. The
    /// layer is bound to "coarse" with its chunk (0, 0) at the world origin, so the chunk's west edge meets both
    /// surfaces' patch edges at x = 0. Coarse cell (-1, -1), west of the sample (0.1, 0.5), has no underlay. Its centre
    /// (-2, 2) lies in the next patch and two fine cells outside the chunk.</summary>
    internal static FoliageFixture EdgeChunk(float edge)
    {
        var fixture = Clearance("solid");
        MapDocument document = fixture.Document;
        document.Placements.Clear();
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, "coarse", MapSurfaceRole.SupportFloor,
            new(new(4, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0), -16, -16, 16, 16, (_, _) => 1000);
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            for (int i = 0; i < patch.Cells.Length; i++) patch.Cells[i] = patch.Cells[i] with { Underlay = 10 };
            document.Surfaces.Patches.Add(patch.Key, patch);
        }
        var key = MapPatchKey.ForCell("coarse", -1, -1);
        MapSurfacePatch west = document.Surfaces.Patches[key];
        int index = ((int)(-1 - key.SlotZ * 64) - west.CellMinZ) * west.Width + (int)(-1 - key.SlotX * 64) - west.CellMinX;
        west.Cells[index] = west.Cells[index] with { Underlay = 0 };
        MapSurfaceRef surface = set.Refs.Single();
        document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                set.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
        });
        var layer = Layer("cover-a", Surface("coarse"), 0, 0, 1, 32, 32, Enumerable.Repeat((byte)255, 32 * 32).ToArray(), [10]);
        document.Foliage = new(1, [ClearanceLayer(layer, rules: [], ordinals: [new("coarse", 0)], edge: edge)]);
        return fixture;
    }

    /// <summary>The edge chunk with the layer origin moved to (15, 0). Coarse slot boundary x = 0 lies 15 m, 3.75 of its
    /// 4 m cells, west of the chunk: past the 3-cell + 0.01 halo widened by the 0.5-cell centre reach, inside the halo
    /// widened by the 2-cell edge fade read reach. The edge fade grid starts at coarse cell 0, whose west corner normal
    /// reads the corner in slot -1.</summary>
    internal static FoliageFixture EdgeScope(float edge)
    {
        var fixture = EdgeChunk(edge);
        var layer = Layer("cover-a", Surface("coarse"), 15, 0, 1, 32, 32, Enumerable.Repeat((byte)255, 32 * 32).ToArray(), [10]);
        fixture.Document.Foliage = new(1, [ClearanceLayer(layer, rules: [], ordinals: [new("coarse", 0)], edge: edge)]);
        return fixture;
    }

    /// <summary>The edge chunk without edge fade, with the layer origin at (0.1, 0) so chunk (0, 0) is x 0.1 to 32.1, two
    /// chunks wide. A placement seated on "coarse" at (34, 2) owns a Solid mask targeting "coarse". A sample at x 32 to
    /// 32.1 lies in coarse cell (8, -1), whose centre (34, 2) is cell 34 of the acquired 1 m floors, past their one-cell
    /// ring around the chunk.</summary>
    internal static FoliageFixture CentreMask()
    {
        var fixture = EdgeChunk(0);
        fixture.Document.Placements.Add(ClearingPlacement("owner", "coarse", 34, 2));
        var layer = Layer("cover-a", Surface("coarse"), 0.1f, 0, 1, 64, 32, Enumerable.Repeat((byte)255, 64 * 32).ToArray(), [10]);
        fixture.Document.Foliage = new(1, [ClearanceLayer(layer, rules: [], ordinals: [new("coarse", 0)], edge: 0,
            masks: [new("centre", Owner("owner"), Surface("coarse"), MapFoliageClearanceKind.Solid, new(-0.5f, -0.5f, 0.5f, 0.5f))])]);
        return fixture;
    }

    /// <summary>A room bound by Space over a native 4 m "coarse" floor at 20 m and one 4 m cell of a prefab deck at 21 m,
    /// yawed 45 degrees and centred at (100.75, -13.98). The layer origin is (64, -32), so the chunk's east edge is x 96.
    /// The sample (95.9, -14) is 0.1 m inside its coarse cell's east edge. Its east neighbour centre (98, -14) selects the
    /// deck, whose cell centre lies 4.75 m past the chunk, beyond the old one cell plus edge fade owner pad. A Solid owner
    /// seated on the deck through OwnerSupports covers only that centre.</summary>
    internal static FoliageFixture YawedNeighbour()
    {
        var fixture = Clearance("solid");
        MapDocument document = fixture.Document;
        document.Placements.Clear();
        var set = new MapSurfaceSet();
        WaterFixtures.Surface(set, "coarse", MapSurfaceRole.SupportFloor,
            new(new(4, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0), 0, 0, 32, 16, (_, _) => 2000);
        MapSurfacePatch floor = set.Patches.Values.Single();
        for (int i = 0; i < floor.Cells.Length; i++) floor.Cells[i] = floor.Cells[i] with { Underlay = 10 };
        MapPatchKey anchor = floor.Key;
        floor.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        floor.Records.Add(new MapSpaceFootprint("outside-domain", new("outside", anchor), anchor,
            Enumerable.Range(0, 16).SelectMany(z => Enumerable.Range(0, 32).Select(x => MapLowerCellClassifier.SlotCell(x, z))).ToArray(),
            new(MapBoundKind.SupportFloor, "coarse", null), new(MapBoundKind.OpenTop, null, null)));
        floor.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, new("outside", anchor), null, [], [], [], []));
        floor.Records.Add(new MapPrismDomain("room-domain", 1, new("room", anchor), new("outside", anchor), "", "storey",
            new(MapLatticeFrame.ImportedMetreCentimetre, [new(56, 0, 112, 40)], []), 20, 25));
        document.Surfaces.Patches.Add(floor.Key, floor);
        MapSurfaceRef coarse = set.Refs.Single();
        document.Surfaces.Refs.Add(coarse with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(coarse,
                [new KeyValuePair<MapPatchKey, string>(floor.Key, MapSurfaceSemantics.PatchDigest(floor))])
        });
        document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "storey", "Storey", null)] };
        MapSurfacePatch deck = PrefabFixtures.Patch("deck", 1, 1, 0);
        deck.Cells[0] = new(10, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto);
        var asset = new MapPrefabAsset(1, "deck-prefab", new MapPrefabChild[] { PrefabFixtures.SurfaceChild("deck", MapSurfaceRole.SupportFloor, deck) });
        var (roots, source) = ClearanceAssets();
        MapAssetRef prefabRef = source.Add("deck-prefab", System.Text.Encoding.UTF8.GetString(MapPrefabFile.Write(asset)));
        MapAssetRef index = source.Add("deck-prefab-index", new JsonObject
        {
            ["payloadVersion"] = 1,
            ["assets"] = new JsonArray(),
            ["resources"] = new JsonArray(NativeAssetFixtures.Resource(prefabRef, "Prefab", "mesh", "collider", "selection", "material")),
        }.ToJsonString());
        roots = roots.Append(index).ToArray();
        document.NativeAssets = roots.ToList();
        // Local cell (0, 0) spans x 0 to 4 and z -4 to 0 at scale 4. Yawed 45 degrees it is a diamond whose top vertex is
        // the pose origin and whose centre lies 2 sqrt(2) below it, so the west vertex sits just west of (98, -14).
        document.Prefabs = new(1, 1, [new("p", "deck-prefab", prefabRef.Sha256, new MapTransform(new(100.75f, 21, -11.15f), MathF.PI / 4, 4), null,
            new(asset.Children.Select(c => new MapPrefabBinding(c.Key, "p/" + c.Key.Key, null, false)).ToArray()), [])], []);
        // A world placement cannot bind to a prefab surface, so the explicit-height owner is seated through OwnerSupports.
        document.Placements.Add(ClearingPlacement("owner", "p/deck", 100.75f, -13.98f, explicitY: true));
        var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "room", 21.2f, 1.5f, 0.5f);
        var layer = Layer("cover-a", binding, 64, -32, 1, 64, 32, Enumerable.Repeat((byte)255, 64 * 32).ToArray(), [10]);
        document.Foliage = new(1, [ClearanceLayer(layer, rules: [new("clear", "tree", "clear", MapFoliageClearanceKind.Solid, new(-0.2f, -0.2f, 0.2f, 0.2f))],
            owners: [new(Owner("owner"), Surface("p/deck"))], ordinals: [new("p/deck", 0)], edge: 0.25f, indoors: false)]);
        return new(document, MapAssetClosure.Load(roots, source));
    }

    /// <summary>The edge chunk bound to an exterior space "level" by Space at ReferenceY 20.2 with 11 m below, so the
    /// 4 m "coarse" floor at 10 m and the 1 m "floor-1" at 20 m are both in its band. The layer origin is (1.5, 0).
    /// Coarse supports the space over its patch (0, -1), world x 0 to 64. Floor-1 supports it only over cells x -8 to -1
    /// and z -8 to -1, painted ineligible. The sample (1.75, 6) lies in coarse cell (0, -2), whose west neighbour centre
    /// (-2, 6) selects floor-1 cell (-2, -6), two cells past floor-1's one-cell margin around the chunk.</summary>
    internal static FoliageFixture EdgeChunkSpace(float edge)
    {
        var fixture = EdgeChunk(edge);
        MapDocument document = fixture.Document;
        MapSurfacePatch coarse = document.Surfaces.Patches[MapPatchKey.ForCell("coarse", 0, -1)];
        var space = new MapRecordRef("level", coarse.Key);
        coarse.Records.Add(new MapSpaceDoc("level", MapSpaceKind.Exterior, null, null, [], [], [], []));
        coarse.Records.Add(new MapSpaceFootprint("level-coarse", space, coarse.Key,
            Enumerable.Range(0, coarse.Depth).SelectMany(z => Enumerable.Range(0, coarse.Width)
                .Select(x => MapLowerCellClassifier.SlotCell(coarse.Key.SlotX * 64 + coarse.CellMinX + x, coarse.Key.SlotZ * 64 + coarse.CellMinZ + z))).ToArray(),
            new(MapBoundKind.SupportFloor, "coarse", null), new(MapBoundKind.OpenTop, null, null)));
        MapSurfacePatch fine = document.Surfaces.Patches[MapPatchKey.ForCell("floor-1", -1, -1)];
        var cells = new List<int>();
        for (long z = -8; z < 0; z++)
            for (long x = -8; x < 0; x++)
            {
                int index = (int)(z - fine.Key.SlotZ * 64 - fine.CellMinZ) * fine.Width + (int)(x - fine.Key.SlotX * 64 - fine.CellMinX);
                fine.Cells[index] = fine.Cells[index] with { Overlay = 6, Cut = MapOverlayCut.Full };
                cells.Add(MapLowerCellClassifier.SlotCell(x, z));
            }
        fine.Records.Add(new MapSpaceFootprint("level-fine", space, fine.Key, cells.ToArray(),
            new(MapBoundKind.SupportFloor, "floor-1", null), new(MapBoundKind.OpenTop, null, null)));
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            MapSurfaceRef surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        var binding = new MapSupportBinding(MapSupportBindingKind.Space, null, "level", 20.2f, 11, 0.5f);
        var layer = Layer("cover-a", binding, 1.5f, 0, 1, 32, 32, Enumerable.Repeat((byte)255, 32 * 32).ToArray(), [10]);
        document.Foliage = new(1, [ClearanceLayer(layer, rules: [], ordinals: [new("coarse", 0)], edge: edge)]);
        return fixture;
    }
}
