using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
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
}
