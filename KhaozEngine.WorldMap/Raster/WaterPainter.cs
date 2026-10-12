using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Colours water depth and finds dry, non-void neighbours on an expanded raster lattice.</summary>
internal sealed class WaterPainter
{
    readonly WorldMapWater _settings;
    readonly int _radius;
    readonly int _maskSize;
    readonly bool[] _dry;

    internal WaterPainter(SurfaceSampler sampler, RasterCanvas canvas, WorldMapWater settings)
    {
        _settings = settings;
        _radius = settings.ShorelinePixels;
        _maskSize = checked(canvas.Size + 2 * _radius);
        _dry = _radius > 0 ? new bool[checked(_maskSize * _maskSize)] : [];
        if (_radius <= 0) return;
        for (int row = 0; row < _maskSize; row++)
            for (int column = 0; column < _maskSize; column++)
            {
                SurfaceSample sample = sampler.Sample(canvas.MapPoint(column - _radius, row - _radius));
                _dry[row * _maskSize + column] = sample.HasGround && !sample.IsWet;
            }
    }

    internal Color Paint(int x, int y, SurfaceSample sample)
    {
        for (int dy = -_radius; dy <= _radius; dy++)
            for (int dx = -_radius; dx <= _radius; dx++)
                if (_radius > 0 && _dry[(y + _radius + dy) * _maskSize + x + _radius + dx])
                    return _settings.Shoreline;

        float depth = MathUtil.Clamp01((sample.WaterSurface - sample.Height) / _settings.DeepDepthMetres);
        return Color.Lerp(_settings.Shallow, _settings.Deep, depth);
    }
}
