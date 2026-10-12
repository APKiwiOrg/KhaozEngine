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
                MathF.Max(0f, style.OutlinePixels / canvas.PixelsPerMetre);
            PaintShape(new CoverageSampler(item.Footprint, item.Symbol.Kind, style.WallWidthMetres, Vector2.Zero),
                item.Symbol.Fill, item.Symbol.Outline, inset);
        }
    }

    void PaintShape(CoverageSampler shape, Color fill, Color outline, float inset)
    {
        MapRect bounds = shape.Bounds;
        if (!bounds.Intersects(canvas.Rect)) return;
        int left = PixelEdge((bounds.X - canvas.Rect.X) * canvas.PixelsPerMetre, roundUp: false);
        int right = PixelEdge((bounds.Right - canvas.Rect.X) * canvas.PixelsPerMetre, roundUp: true);
        int top = PixelEdge((canvas.Rect.Top - bounds.Top) * canvas.PixelsPerMetre, roundUp: false);
        int bottom = PixelEdge((canvas.Rect.Top - bounds.Y) * canvas.PixelsPerMetre, roundUp: true);
        for (int row = top; row < bottom; row++)
            for (int column = left; column < right; column++)
            {
                (float full, float interior) = shape.Sample(canvas, column, row, inset);
                canvas.Blend(column, row, fill, interior);
                canvas.Blend(column, row, outline, full - interior);
            }
    }

    int PixelEdge(float edge, bool roundUp) =>
        (int)Math.Clamp(roundUp ? MathF.Ceiling(edge) : MathF.Floor(edge), 0f, canvas.Size);
}
