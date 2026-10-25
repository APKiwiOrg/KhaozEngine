using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>
/// Maps a PixelLab export direction name to its grid-sheet row index, in the canonical S, SE, E, NE,
/// N, NW, W, SW row order. This is the only definition of that order, asserted directly by
/// PixelLabSheetAssembler.Tests.DirectionRowsTests.
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
