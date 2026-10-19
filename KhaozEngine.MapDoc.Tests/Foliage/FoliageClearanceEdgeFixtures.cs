using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
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

    /// <summary>The edge chunk with the layer origin moved to (13, 0). Coarse slot boundary x = 0 lies 13 m, 3.25 of its
    /// 4 m cells, west of the chunk: past the 3-cell + 0.01 halo, inside the 4.6-cell edge fade reach. The edge fade grid
    /// starts at coarse cell 0, whose west corner normal reads the corner in slot -1.</summary>
    internal static FoliageFixture EdgeScope(float edge)
    {
        var fixture = EdgeChunk(edge);
        var layer = Layer("cover-a", Surface("coarse"), 13, 0, 1, 32, 32, Enumerable.Repeat((byte)255, 32 * 32).ToArray(), [10]);
        fixture.Document.Foliage = new(1, [ClearanceLayer(layer, rules: [], ordinals: [new("coarse", 0)], edge: edge)]);
        return fixture;
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
