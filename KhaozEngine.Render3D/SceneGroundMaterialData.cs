using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>Detached tile-ground input for the same admitted mesh upload as model and water resources.</summary>
public sealed class SceneGroundMaterialData
{
    readonly TileGroundLayerImage[] _layers;
    public int Width { get; }
    public int Height { get; }
    public float BaseSpecularStrength { get; }
    public TerrainSamplerConfig? Sampler { get; }
    public IReadOnlyList<TileGroundLayerImage> Layers => Array.AsReadOnly(_layers.Select(Copy).ToArray());
    internal IReadOnlyList<TileGroundLayerImage> BorrowLayers => _layers;

    public SceneGroundMaterialData(int width, int height, IReadOnlyList<TileGroundLayerImage> layers,
        float baseSpecularStrength = .15f, TerrainSamplerConfig? sampler = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (layers.Count is < 1 or > TileGroundMaterialConfig.MaxMaterials ||
            layers.Any(l => l is null || l.AlbedoRgba.LongLength != checked((long)width * height * 4)))
            throw new ArgumentException("Ground layers require equal RGBA8 dimensions.", nameof(layers));
        _layers = layers.Select(Copy).ToArray();
        _ = TileGroundMaterialConfig.BuildParams(_layers, baseSpecularStrength);
        Width = width; Height = height; BaseSpecularStrength = baseSpecularStrength; Sampler = sampler;
    }
    static TileGroundLayerImage Copy(TileGroundLayerImage l) => new()
    {
        AlbedoRgba = l.AlbedoRgba.ToArray(),
        Tint = l.Tint,
        TilesPerMetre = l.TilesPerMetre,
        Steep = l.Steep is not { } s ? null : new()
        { CompanionSlot = s.CompanionSlot, FromDegrees = s.FromDegrees, ToDegrees = s.ToDegrees }
    };
}
