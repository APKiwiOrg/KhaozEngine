using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>
/// Maps a PixelLab export direction name to its grid-sheet row index. Row order is the
/// KhaozEngine.Sprites.Direction8 integer order (S, SE, E, NE, N, NW, W, SW), which is exactly
/// what PixelLabSpriteLoader.FromGridSheet expects. Pinned against the live enum by
/// PixelLabSheetAssembler.Tests.DirectionRowsTests so an enum reorder fails loudly.
/// </summary>
public static class DirectionRows
{
    public const int RowCount = 8;

    public static readonly IReadOnlyDictionary<string, int> NameToRow = new Dictionary<string, int>
    {
        ["south"] = 0,
        ["south-east"] = 1,
        ["east"] = 2,
        ["north-east"] = 3,
        ["north"] = 4,
        ["north-west"] = 5,
        ["west"] = 6,
        ["south-west"] = 7,
    };
}
