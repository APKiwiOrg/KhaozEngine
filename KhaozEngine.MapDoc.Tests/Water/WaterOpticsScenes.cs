using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Water;

/// <summary>The five DG9.5 optics scenes. Each is a valid resolver-2 document whose bodies compile completely: four
/// open waters at <c>SurfaceY</c> 0 over exterior floors, and <see cref="WaterFixtures.CaveLakeAlone"/> under its
/// enclosing roof.</summary>
internal static class WaterOpticsScenes
{
    internal static MapDocument Build(string scene) => scene switch
    {
        // A -0.3 m ledge for x 0 to 4 beside -2 m for x 4 to 8, the cell past x 4 sloping between them.
        "ledge" => WaterFixtures.ExteriorWater("ledge", "ledge-water", WaterFixtures.Frame, 0, 0, 8, 4, (x, _) => x <= 4 ? -30 : -200),
        // A 2 m deep channel for x 3 to 5 between 0 m banks.
        "river" => WaterFixtures.ExteriorWater("channel", "river", WaterFixtures.Frame, 0, 0, 8, 4, (x, _) => x is >= 3 and <= 5 ? -200 : 0),
        // 8 m cells: a -30 m shelf for x 0 to 256 and -100 m ocean for x 256 to 512.
        "shelf" => WaterFixtures.ExteriorWater("shelf", "shelf-sea", EightMetreFrame, 0, 0, 64, 8, (x, _) => x < 32 ? -3000 : -10000),
        "cave-lake" => WaterFixtures.CaveLakeAlone(),
        // One body over a -2 m floor crossing the slot seam at x 64.
        "cross-region" => WaterFixtures.ExteriorWater("seam", "cross-region", WaterFixtures.Frame, 32, 0, 96, 4, (_, _) => -200),
        _ => throw new ArgumentException("unknown optics scene", nameof(scene)),
    };

    static MapLatticeFrame EightMetreFrame => new(new(8, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0);
}
