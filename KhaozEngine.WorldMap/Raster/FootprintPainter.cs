using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Composites all building shadows, then ordered footprint fills and inset outlines.</summary>
internal sealed class FootprintPainter(RasterCanvas canvas, WorldMapStyle style)
{
    internal void Paint(IReadOnlyList<WorldMapFootprint> footprints)
    {
        var resolved = new List<(WorldMapFootprint Footprint, WorldMapSymbol Symbol)>(footprints.Count);
        foreach (WorldMapFootprint footprint in footprints)
        {
            if (2f * MathF.Max(footprint.HalfExtents.X, footprint.HalfExtents.Y) * canvas.EffectivePixelsPerMetre < 0.5f)
                continue;
            WorldMapSymbol symbol = WorldMapSymbolResolver.SymbolFor(style, footprint);
            if (symbol.Kind != WorldMapSymbolKind.Hidden) resolved.Add((footprint, symbol));
        }
        resolved.Sort(static (a, b) =>
        {
            int kind = a.Symbol.Kind.CompareTo(b.Symbol.Kind);
            return kind != 0 ? kind : a.Footprint.Id.CompareTo(b.Footprint.Id);
        });

        foreach (var item in resolved)
            if (item.Symbol.Kind == WorldMapSymbolKind.Building)
                PaintShape(new CoverageSampler(item.Footprint, item.Symbol.Kind, style.WallWidthMetres,
                    style.BuildingShadowOffsetMetres), style.BuildingShadow, Color.Transparent, 0f);

        foreach (var item in resolved)
        {
            float inset = item.Symbol.Kind == WorldMapSymbolKind.Wall ? 0f :
                MathF.Max(0f, style.OutlinePixels / canvas.EffectivePixelsPerMetre);
            PaintShape(new CoverageSampler(item.Footprint, item.Symbol.Kind, style.WallWidthMetres, Vector2.Zero),
                item.Symbol.Fill, item.Symbol.Outline, inset);
        }
    }

    void PaintShape(CoverageSampler shape, Color fill, Color outline, float inset)
    {
        MapRect bounds = shape.Bounds;
        if (!bounds.Intersects(canvas.Rect)) return;
        int left = PixelEdge((bounds.X - canvas.Rect.X) * canvas.EffectivePixelsPerMetre, roundUp: false);
        int right = PixelEdge((bounds.Right - canvas.Rect.X) * canvas.EffectivePixelsPerMetre, roundUp: true);
        int top = PixelEdge((canvas.Rect.Top - bounds.Top) * canvas.EffectivePixelsPerMetre, roundUp: false);
        int bottom = PixelEdge((canvas.Rect.Top - bounds.Y) * canvas.EffectivePixelsPerMetre, roundUp: true);
        for (int row = top; row < bottom; row++)
            for (int column = left; column < right; column++)
            {
                (float full, float interior) = shape.Sample(canvas, column, row, inset);
                // Fill and outline occupy disjoint samples, so neither attenuates the other's contribution.
                float fillAlpha = interior * MathUtil.Clamp01(fill.A);
                float outlineAlpha = (full - interior) * MathUtil.Clamp01(outline.A);
                float alpha = fillAlpha + outlineAlpha;
                if (alpha <= 0f) continue;
                var colour = new Color(
                    (fill.R * fillAlpha + outline.R * outlineAlpha) / alpha,
                    (fill.G * fillAlpha + outline.G * outlineAlpha) / alpha,
                    (fill.B * fillAlpha + outline.B * outlineAlpha) / alpha, alpha);
                canvas.Blend(column, row, colour, 1f);
            }
    }

    int PixelEdge(float edge, bool roundUp) =>
        (int)Math.Clamp(roundUp ? MathF.Ceiling(edge) : MathF.Floor(edge), 0f, canvas.Size);
}
