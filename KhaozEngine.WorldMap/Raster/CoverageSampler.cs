using System;
using System.Numerics;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Samples oriented default or custom geometry with a fixed four by four pixel pattern.</summary>
internal sealed class CoverageSampler
{
    const int SamplesPerAxis = 4;
    readonly Vector2 _center;
    readonly Vector2 _halfExtents;
    readonly float _cos;
    readonly float _sin;
    readonly bool _ellipse;
    readonly SymbolGeometrySampler? _geometry;

    internal CoverageSampler(WorldMapFootprint footprint, WorldMapSymbol symbol, float wallWidth, Vector2 offset)
    {
        _center = footprint.Center + offset;
        _halfExtents = WorldMapSymbolSizing.HalfExtents(footprint, symbol, wallWidth);
        _cos = MathF.Cos(footprint.Rotation);
        _sin = MathF.Sin(footprint.Rotation);
        _geometry = symbol.Geometry is { } geometry ? new SymbolGeometrySampler(geometry, _halfExtents) : null;
        _ellipse = _geometry is null && symbol.Kind is WorldMapSymbolKind.Tree or WorldMapSymbolKind.Obstacle;
        Vector2 extent = _ellipse
            ? new Vector2(MathF.Sqrt(Square(_cos * _halfExtents.X) + Square(_sin * _halfExtents.Y)),
                MathF.Sqrt(Square(_sin * _halfExtents.X) + Square(_cos * _halfExtents.Y)))
            : new Vector2(MathF.Abs(_cos) * _halfExtents.X + MathF.Abs(_sin) * _halfExtents.Y,
                MathF.Abs(_sin) * _halfExtents.X + MathF.Abs(_cos) * _halfExtents.Y);
        Bounds = new MapRect(_center.X - extent.X, _center.Y - extent.Y, extent.X * 2f, extent.Y * 2f);
    }

    internal MapRect Bounds { get; }

    internal (float Full, float Fill) Sample(RasterCanvas canvas, int column, int row, float inset)
    {
        Vector2 inner = _halfExtents - new Vector2(inset);
        int full = 0, fill = 0;
        for (int y = 0; y < SamplesPerAxis; y++)
            for (int x = 0; x < SamplesPerAxis; x++)
            {
                Vector2 delta = canvas.MapPoint(column + (x + 0.5f) / SamplesPerAxis - 0.5f,
                    row + (y + 0.5f) / SamplesPerAxis - 0.5f) - _center;
                // The inverse of a clockwise map rotation returns the point to the local axes.
                var local = new Vector2(_cos * delta.X - _sin * delta.Y, _sin * delta.X + _cos * delta.Y);
                if (_geometry is not null)
                {
                    (bool covered, bool interior) = _geometry.Sample(local, inset);
                    if (covered) full++;
                    if (interior) fill++;
                    continue;
                }
                if (!Contains(local, _halfExtents)) continue;
                full++;
                if (Contains(local, inner)) fill++;
            }
        const float sampleCount = SamplesPerAxis * SamplesPerAxis;
        return (full / sampleCount, fill / sampleCount);
    }

    bool Contains(Vector2 local, Vector2 halfExtents)
    {
        if (halfExtents.X <= 0f || halfExtents.Y <= 0f) return false;
        if (!_ellipse) return MathF.Abs(local.X) <= halfExtents.X && MathF.Abs(local.Y) <= halfExtents.Y;
        Vector2 normalized = local / halfExtents;
        return normalized.LengthSquared() <= 1f;
    }

    static float Square(float value) => value * value;
}
