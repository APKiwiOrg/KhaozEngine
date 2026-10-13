using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Resolves symbol dimensions and their rotation-independent query reach without footprint allocation.</summary>
internal static class WorldMapSymbolSizing
{
    internal static Vector2 HalfExtents(WorldMapFootprint footprint, WorldMapSymbol symbol, float wallWidth)
    {
        if (symbol.SizeMetres is { } size) return size * 0.5f;
        if (symbol.Kind != WorldMapSymbolKind.Wall) return footprint.HalfExtents;
        return footprint.HalfExtents.X >= footprint.HalfExtents.Y
            ? new Vector2(footprint.HalfExtents.X, wallWidth * 0.5f)
            : new Vector2(wallWidth * 0.5f, footprint.HalfExtents.Y);
    }

    internal static float MaximumRadius(WorldMapStyle style)
    {
        float maximum = Radius(style.FallbackBlocking);
        // Indexed so the per-chunk reach never boxes an interface enumerator.
        IReadOnlyList<WorldMapSymbolRule> rules = style.SymbolRules;
        for (int i = 0; i < rules.Count; i++)
            maximum = MathF.Max(maximum, Radius(rules[i].Symbol));
        return maximum;
    }

    internal static void Validate(WorldMapSymbol symbol)
    {
        if (symbol.SizeMetres is not { } size) return;
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X <= 0f || size.Y <= 0f ||
            !float.IsFinite((size * 0.5f).Length()))
            throw new ArgumentException("Symbol size must have finite positive components and a finite half diagonal.",
                nameof(WorldMapSymbol.SizeMetres));
    }

    static float Radius(WorldMapSymbol symbol) =>
        symbol.Kind != WorldMapSymbolKind.Hidden && symbol.SizeMetres is { } size ? (size * 0.5f).Length() : 0f;
}
