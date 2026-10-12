using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Applies normalized hillshade and a separate steep-slope multiplier to dry ground.</summary>
internal sealed class ReliefShader
{
    readonly WorldMapRelief _settings;
    readonly Vector3 _light;
    readonly float _flatLight;

    internal ReliefShader(WorldMapRelief settings)
    {
        _settings = settings;
        float azimuth = settings.AzimuthDegrees * (MathF.PI / 180f);
        float altitude = settings.AltitudeDegrees * (MathF.PI / 180f);
        _flatLight = MathF.Sin(altitude);
        float cosAltitude = MathF.Cos(altitude);
        _light = new Vector3(cosAltitude * MathF.Sin(azimuth), cosAltitude * MathF.Cos(azimuth), _flatLight);
    }

    internal Color Shade(SurfaceSampler sampler, Vector2 point, SurfaceSample sample)
    {
        float c = sampler.CellSize;
        float east = HeightOrOwn(sampler.Sample(point + new Vector2(c, 0f)), sample.Height);
        float west = HeightOrOwn(sampler.Sample(point - new Vector2(c, 0f)), sample.Height);
        float north = HeightOrOwn(sampler.Sample(point + new Vector2(0f, c)), sample.Height);
        float south = HeightOrOwn(sampler.Sample(point - new Vector2(0f, c)), sample.Height);
        var gradient = new Vector2((east - west) / (2f * c), (north - south) / (2f * c));
        Vector3 normal = Vector3.Normalize(new Vector3(-gradient.X, -gradient.Y, 1f));
        float lit = MathF.Max(0f, Vector3.Dot(normal, _light)) / _flatLight;
        float factor = 1f + _settings.Strength * (lit - 1f);
        if (gradient.Length() > _settings.SteepSlope) factor *= _settings.SteepDarken;
        return sample.Ground.ScaleRgb(factor);
    }

    static float HeightOrOwn(SurfaceSample sample, float ownHeight) => sample.HasGround ? sample.Height : ownHeight;
}
