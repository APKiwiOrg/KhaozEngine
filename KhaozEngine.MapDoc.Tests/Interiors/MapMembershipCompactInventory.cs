namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>Independent capacities for the synthetic plain fixture. No production prices or observations.</summary>
internal static class MapMembershipCompactInventory
{
    internal const long EmptyGeometry = 768 + 320 + 64;
    internal const long EmptyView = 256 + EmptyGeometry + 768;
    internal const long EmptyRetained = 128 + 256 + EmptyView + 12640 + (256 + 2 * 136 + 5) + 128;
    internal const long Part = 136, Exact = 12640, Arena = 30240, FootprintArena = 14432;
    // Fifteen arena/store/column owners, eight page/directory arrays and the registry metadata identity.
    internal const long Footprint = FootprintArena + 4096 + 32 + 512 * 24;
    internal const long Shared = Footprint + 512, Cache = 256;
    internal const long FrozenBounds = 80 + 5 * 48 + (24 + 128 * 32 + 32) + (24 + 64 * 8 + 32);
    internal const long Index = 64 + 24 + 4 + 24 + 80;
    internal const long Geometry = 768 + FrozenBounds + 128 + 4 + Index + 128 * 2 + 64;
    internal const long FootprintView = 128 + 512 + Shared + 128 * 2 + 8;
    internal const long View = 256 + Geometry + Arena + 8 + FootprintView + 192 + 96 + 8;
    internal const long Point = 256 + 2 * Part + 5;
    internal const long Scratch = 256 + View + Exact + 40 + 24 * 3 + 8 * 3 + 8 + Point;
    internal const long Retained = 128 + Scratch + 128;
    internal const long Root = Retained + Cache;
    // Coverage fixture has 16 cells, 32 triangles and four footprint quads. Three token pages replace one.
    internal const long CoverageArena = Arena + (3 * 4120 + 56) - (4120 + 32);
    internal const long CoverageShared = Shared + 8;
    internal const long CoverageIndex = 64 + 24 + 4 * 16 + 24 + 80 * 31;
    internal const long CoverageGeometry = 768 + FrozenBounds + 128 + 4 * 16 + CoverageIndex + 128 * 2 + 64;
    internal const long CoverageFootprint = 128 + 512 + CoverageShared + 128 * 2 + 8 * 16;
    internal const long CoverageView = 256 + CoverageGeometry + CoverageArena + 8 + CoverageFootprint + 192 + 96 + 8;
    internal const long CoverageRetained = 128 + 256 + CoverageView + Exact + 40 + 24 * 3 + 8 * (1 + 16 + 16) + 8 + Point + 128;
}
