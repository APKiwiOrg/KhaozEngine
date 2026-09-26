using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Flat single-colour ground materials, the cheapest the splat and tile-ground pipelines accept, shared by the
/// frame upload shape tests and the ground motion readbacks.</summary>
internal static class GroundLayerImages
{
    /// <summary>Five flat single-colour splat layers, the cheapest material the splat pipeline accepts.</summary>
    internal static List<SplatLayerImage> FlatSplatLayers(int size)
    {
        var layers = new List<SplatLayerImage>();
        for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
        {
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            for (int p = 0; p < albedo.Length; p += 4)
            {
                albedo[p] = (byte)(40 + i * 30); albedo[p + 1] = 110; albedo[p + 2] = 60; albedo[p + 3] = 255;
                normal[p] = 128; normal[p + 1] = 128; normal[p + 2] = 255; normal[p + 3] = 255;
            }
            layers.Add(new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, TilesPerMetre = 0.25f, Roughness = 0.8f });
        }
        return layers;
    }

    /// <summary>Two flat single-colour tile-ground layers, the cheapest material the pipeline accepts that is not the
    /// one-layer special case.</summary>
    internal static List<TileGroundLayerImage> FlatGroundLayers(int size)
    {
        var layers = new List<TileGroundLayerImage>();
        for (int i = 0; i < 2; i++)
        {
            var albedo = new byte[size * size * 4];
            for (int p = 0; p < albedo.Length; p += 4)
            {
                albedo[p] = (byte)(40 + i * 60); albedo[p + 1] = 110; albedo[p + 2] = 60; albedo[p + 3] = 255;
            }
            layers.Add(new TileGroundLayerImage { AlbedoRgba = albedo, TilesPerMetre = 0.25f });
        }
        return layers;
    }
}
