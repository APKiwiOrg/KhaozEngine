namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>Independent capacities for the synthetic plain fixture. No production prices or observations.</summary>
internal static class MapMembershipCompactInventory
{
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
}
