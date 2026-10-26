using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Scene;

/// <summary>Invented plain geometry and independently enumerated array capacities.</summary>
internal static class MembershipAdmissionFixtures
{
    internal const int Cells = 4096;
    internal static NativeSceneFixture Create()
    {
        var fixture = NativeSceneFixtures.Create("empty");
        fixture.Document.TileSize = 1;
        var patch = new MapSurfacePatch
        {
            Key = new("floor", 0, 0),
            Width = 64,
            Depth = 64,
            Heights = new int[65 * 65],
            Cells = new MapSurfaceCell[Cells],
            Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray()
        };
        for (int i = 0; i < patch.Cells.Length; i++)
            patch.Cells[i] = patch.Cells[i] with { Topology = MapCellTopology.ForceSwNe, Underlay = 1 };
        patch.Records.Add(new MapSpaceDoc("world", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("cells", new("world", patch.Key), patch.Key,
            Enumerable.Range(0, Cells).ToArray(), new(MapBoundKind.SupportFloor, "floor", null), new(MapBoundKind.OpenTop, null, null)));
        var surface = new MapSurfaceRef("floor", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, new string('a', 64));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        fixture.Document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        });
        return fixture;
    }

    static long Pages(int rows, int stride, int capacity)
    {
        if (rows == 0) return 0;
        int pages = (rows + capacity - 1) / capacity, directory = 1;
        while (directory < pages) directory *= 2;
        return 24L + 8L * directory + pages * ((24L + (long)capacity * stride + 7) & ~7L);
    }
    internal static long Footprint(int cells = Cells)
    {
        long arena = 768 + Pages(cells, 80, 64) + Pages(4 * cells, 32, 128) +
            Pages(4 * cells, 1, 128) + Pages(16 * cells, 16, 256);
        int identities = 15 + (cells + 63) / 64 + 2 * ((4 * cells + 127) / 128) +
            (16 * cells + 255) / 256 + 4;
        return arena + 4096 + ((24L + 4L * cells + 7) & ~7L) + 512 * (identities + 1);
    }
    internal static long Private(int roofs = 0, int cells = Cells)
    {
        long arena = 768 + Pages(cells, 80, 64) + Pages(4 * cells, 32, 128) + Pages(4 * cells, 1, 128) +
            Pages(2 * cells, 160, 32) + Pages(2 * cells, 32, 128) + Pages(cells, 64, 64) +
            Pages(48 * cells, 16, 256) + 3 * Pages(1, 8, 32) + Pages(1, 8, 32) + Pages(1, 32, 32);
        long bounds = 80 + 5 * 48 + Pages(cells + 2, 32, 128) + Pages(1, 8, 64);
        long index = 64 + 24 + 4L * cells + 24 + 80L * (2 * cells - 1);
        long geometry = 768 + bounds + 128 + 4L * cells + index + 128 * 2 + 64;
        long footprint = 128 + 512 + 128 * 2 + 8L * cells;
        long view = 256 + geometry + arena + 8 + footprint + 192 + 96 + 8;
        long point = 256 + 2 * 136 + 5;
        long scratch = 256 + view + 12640 + 40 + 24 * 3 + 8L * (1 + 2 * cells) + 8 + point;
        return 128 + scratch + 128 + 25L * roofs;
    }
}
