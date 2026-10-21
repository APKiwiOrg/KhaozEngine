using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Colours water depth and finds dry, non-void neighbours on an expanded raster lattice.</summary>
internal sealed class WaterPainter
{
    readonly WorldMapWater _settings;
    readonly int _radius;
    readonly int _columnOffset;
    readonly int _rowOffset;
    readonly int _maskWidth;
    readonly bool[] _dry;

    internal WaterPainter(SurfaceSampler sampler, RasterCanvas canvas, WorldMapWater settings)
    {
        _settings = settings;
        _radius = settings.ShorelinePixels;
        // Each region's shoreline halo uses the owning chunk's original sampling lattice.
        _columnOffset = checked(_radius - canvas.Left);
        _rowOffset = checked(_radius - canvas.Top);
        _maskWidth = checked(canvas.Right - canvas.Left + 2 * _radius);
        int maskHeight = checked(canvas.Bottom - canvas.Top + 2 * _radius);
        _dry = _radius > 0 ? new bool[checked(_maskWidth * maskHeight)] : [];
        if (_radius <= 0) return;
        for (int row = 0; row < maskHeight; row++)
            for (int column = 0; column < _maskWidth; column++)
            {
                SurfaceSample sample = sampler.Sample(canvas.MapPoint(column - _columnOffset, row - _rowOffset));
                _dry[row * _maskWidth + column] = sample.HasGround && !sample.IsWet;
            }
    }

    internal Color Paint(int x, int y, SurfaceSample sample)
    {
        for (int dy = -_radius; dy <= _radius; dy++)
            for (int dx = -_radius; dx <= _radius; dx++)
                if (_radius > 0 && _dry[(y + _rowOffset + dy) * _maskWidth + x + _columnOffset + dx])
                    return _settings.Shoreline;

        float depth = MathUtil.Clamp01((sample.WaterSurface - sample.Height) / _settings.DeepDepthMetres);
        return Color.Lerp(_settings.Shallow, _settings.Deep, depth);
    }
}
