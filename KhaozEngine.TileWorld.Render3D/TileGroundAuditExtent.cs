using System;

namespace KhaozEngine.TileWorld;

/// <summary>An emission range with widened exclusive edges. Unlike a general TileRect, the last
/// addressable tile may have an exclusive edge of int.MaxValue + 1.</summary>
internal readonly record struct TileGroundSurfaceExtent(int X, int Z, int Width, int Height)
{
    internal long X1 => checked((long)X + Width);
    internal long Z1 => checked((long)Z + Height);
    internal TileGroundAuditExtent Expand(int margin) => new(checked((long)X - margin),
        checked((long)Z - margin), checked(X1 + margin), checked(Z1 + margin));
}

/// <summary>The complete required footprint, including any unaddressable portion. Intersection is only
/// for safe witness reads. It never turns partial support into available optical provenance.</summary>
internal readonly record struct TileGroundAuditExtent(long X, long Z, long X1, long Z1)
{
    internal bool IsAddressable => X >= int.MinValue && Z >= int.MinValue &&
        X1 <= (long)int.MaxValue + 1 && Z1 <= (long)int.MaxValue + 1;

    internal TileGroundAuditExtent AddressablePart => new(Math.Max(X, int.MinValue), Math.Max(Z, int.MinValue),
        Math.Min(X1, (long)int.MaxValue + 1), Math.Min(Z1, (long)int.MaxValue + 1));
}
