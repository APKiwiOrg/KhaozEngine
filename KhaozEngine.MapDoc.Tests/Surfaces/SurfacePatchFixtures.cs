using System;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class SurfacePatchFixtures
{
    internal static MapSurfacePatch Sample() => new()
    {
        Key = new("ground", -1, 0),
        CellMinX = 60,
        CellMinZ = 0,
        Width = 4,
        Depth = 2,
        Heights = new[] { -50000, 0, 1433, 1331, 1363, 518, int.MaxValue, int.MinValue, 7, 8, 9, 10, 11, 12, 13 },
        Cells = new MapSurfaceCell[]
        {
            new(14, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto),
            new(3, 5, MapOverlayCut.DiagonalHalf, 1, MapCellFlags.FeatherOverlay, MapCellTopology.Auto),
            new(3, 5, MapOverlayCut.CornerQuarter, 2, MapCellFlags.Blocked | MapCellFlags.Indoor, MapCellTopology.Auto),
            new(0, 0, MapOverlayCut.Full, 0, MapCellFlags.NoDraw | MapCellFlags.LegacyBridge, MapCellTopology.Auto),
            new(65535, 65535, MapOverlayCut.CornerThreeQuarter, 3, MapCellFlags.None, MapCellTopology.ForceNwSe),
            new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe),
            new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto),
            new(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto),
        },
        Presence = new[] { 0b1111_1101UL },
    };

    internal static MapSurfacePatch Row(int cells)
    {
        if (cells is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(cells));
        return new()
        {
            Key = new("r", 0, 0),
            Width = cells,
            Depth = 1,
            Heights = new int[2 * (cells + 1)],
            Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                MapCellFlags.None, MapCellTopology.Auto), cells).ToArray(),
            Presence = new[] { cells == 64 ? ulong.MaxValue : (1UL << cells) - 1 },
        };
    }
}
