using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyTerrainImportRegressionTests
{
    static readonly RegionCoord West = new(-1, 0);
    static readonly RegionCoord East = new(0, 0);

    // A document converted through the one global lattice never disagrees across a seam, so the control feeds the
    // comparison converted lattices with one shared corner moved on the east side of the oracle world's seam.
    [Fact]
    public void SharedCornerMismatches_ReportsACornerTheTwoSidesOfASeamDisagreeOn()
    {
        LegacyOracleWorld w = LegacyOracleWorld.Create();
        Assert.Empty(LegacyOracleConverter.SharedCornerMismatches(w.Document, West, Converted(w.Document)));

        int shared = w.Document.CornerHeightCm(0, 3, 1);
        IReadOnlyList<string> mismatches = LegacyOracleConverter.SharedCornerMismatches(w.Document, West,
            MovedCorner(w.Document, East, plane: 1, x: 0, z: 3));

        Assert.Equal(new[] { $"plane 1 corner (0, 3): region {West} has {shared}, region {East} has {shared + 7}" },
            mismatches);
    }

    static Func<RegionCoord, int, int[]> Converted(TileWorldDocument document)
        => (region, plane) => LegacyOracleConverter.ToNative(document, region, plane).Patch.Heights;

    static Func<RegionCoord, int, int[]> MovedCorner(TileWorldDocument document, RegionCoord moved, int plane,
        int x, int z)
        => (region, p) =>
        {
            int[] heights = LegacyOracleConverter.ToNative(document, region, p).Patch.Heights;
            if (region == moved && p == plane)
                heights[(z - region.OriginZ) * (TileRegion.Size + 1) + x - region.OriginX] += 7;
            return heights;
        };
}
