using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocCompatibility;

internal sealed class LegacyWaterOracleWorld
{
    internal TileWorldDocument Document { get; }
    internal TileWorldCatalogs Catalogs { get; }

    LegacyWaterOracleWorld(TileWorldDocument document, TileWorldCatalogs catalogs)
    {
        Document = document;
        Catalogs = catalogs;
    }

    internal static LegacyWaterOracleWorld Create()
    {
        var document = new TileWorldDocument { Id = "public-legacy-water-oracle" };
        document.GetOrCreateRegion(new(0, 0));
        document.GetOrCreateRegion(new(1, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < 2 * TileRegion.Size; x++)
                document.SetUnderlay(x, z, 0, 2);
        TileWorldCatalogs catalogs = TileWorldCatalogs.LoadJson("""
            {"materials":[{"id":1,"name":"water","color":"#2266aa","kind":"Water"},
                          {"id":2,"name":"ground","color":"#668844"}]}
            """, "public-legacy-water-oracle");

        // Each clipped river piece has one rim corner, both clear of the seam and its quarter-metre samples.
        Basin(document, new(52, 12, 24, 4), -100);
        Basin(document, new(72, 16, 4, 12), -100);
        document.SetCornerHeightCm(52, 12, 0, 0);
        document.SetCornerHeightCm(76, 28, 0, 0);
        Basin(document, new(80, 36, 8, 8), -60);
        document.SetCornerHeightCm(80, 36, 0, 40);

        Basin(document, new(12, 30, 3, 3), 20);
        for (int z = 31; z <= 32; z++)
            for (int x = 13; x <= 14; x++)
                document.SetCornerHeightCm(x, z, 0, -130);

        document.SetUnderlay(30, 40, 0, 1);
        document.SetCornerHeightCm(30, 40, 0, 10);
        document.SetCornerHeightCm(31, 40, 0, 8);
        document.SetCornerHeightCm(30, 41, 0, 7);
        document.SetCornerHeightCm(31, 41, 0, 8);
        return new(document, catalogs);
    }

    internal static LegacyWaterOracleWorld WithPlaneOneWater()
    {
        LegacyWaterOracleWorld world = Create();
        world.Document.SetUnderlay(4, 4, 1, 1);
        return world;
    }

    internal static bool IsRiver(LegacyWaterSource source) => source.Body.Rects.Any(r => r.Z == 12);
    internal static bool IsShoal(LegacyWaterSource source) => source.Body.Rects.Any(r => r.X == 12 && r.Z == 30);
    internal static bool IsPuddle(LegacyWaterSource source) => source.Body.Rects.Any(r => r.X == 30 && r.Z == 40);

    internal static IEnumerable<MapExactXz> SeamSamples()
    {
        for (int quarter = 49; quarter < 64; quarter++)
            yield return new(new(64, 1), new(-quarter, 4));
    }

    static void Basin(TileWorldDocument document, TileRect rect, short bedCm)
    {
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++)
                document.SetUnderlay(x, z, 0, 1);
        for (int z = rect.Z; z <= rect.Z1; z++)
            for (int x = rect.X; x <= rect.X1; x++)
                document.SetCornerHeightCm(x, z, 0, bedCm);
    }
}
