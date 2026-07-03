using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One terrain surface layer: a tileable albedo + tangent-space normal (RGBA8, same WxH as every other
    /// layer), a tint, the tiling rate (tiles per world metre), and a scalar roughness.</summary>
    public sealed class TerrainMaterialLayer
    {
        public byte[] AlbedoRgba = Array.Empty<byte>();
        public byte[] NormalRgba = Array.Empty<byte>();
        public Color Tint = Color.White;
        public float TilesPerMetre = 0.25f;
        public float Roughness = 0.85f;
    }

    /// <summary>The five terrain material layers in channel order (grass/dirt/rock/sand/snow, matching
    /// <see cref="TerrainSplatWeights"/>) plus global render params. Realize it once with
    /// <c>scene.LoadTerrainMaterial(...)</c>; the resulting handle is shared by every chunk.</summary>
    public sealed class TerrainLayeredMaterial
    {
        public int Width;
        public int Height;
        public TerrainMaterialLayer Grass = new();
        public TerrainMaterialLayer Dirt = new();
        public TerrainMaterialLayer Rock = new();
        public TerrainMaterialLayer Sand = new();
        public TerrainMaterialLayer Snow = new();
        public float TriplanarSharpness = 8f;
        public SplatProjection Projection = SplatProjection.Triplanar;
        public float BaseSpecStrength = 0.15f;

        /// <summary>Optional override for how the ground samples its detail textures at a distance (anisotropy level,
        /// filter, mip LOD bias). Null (the default) uses the engine's tuned default (<see cref="TerrainSamplerConfig.Default"/>,
        /// anisotropic 16x + a +1 bias) and is byte-identical to prior behaviour. Lower the anisotropy / switch to
        /// trilinear / raise the bias to reduce the distance "fuzz" a high-frequency tiling albedo throws off as the
        /// camera moves (at the cost of some grazing sharpness).</summary>
        public TerrainSamplerConfig? Sampler;

        /// <summary>The five layers in channel order (grass, dirt, rock, sand, snow).</summary>
        public IReadOnlyList<TerrainMaterialLayer> Layers => new[] { Grass, Dirt, Rock, Sand, Snow };

        /// <summary>Throw if the material is malformed: non-positive dimensions, or any layer whose albedo/normal
        /// byte length does not match Width*Height*4 (the texture-array layers must all be the same RGBA8 size).</summary>
        public void Validate()
        {
            if (Width <= 0 || Height <= 0)
                throw new ArgumentException($"TerrainLayeredMaterial needs positive dimensions, got {Width}x{Height}.");
            int expected = Width * Height * 4;
            var layers = Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].AlbedoRgba.Length != expected)
                    throw new ArgumentException($"layer {i} albedo is {layers[i].AlbedoRgba.Length} bytes, expected {expected} ({Width}x{Height} RGBA8).");
                if (layers[i].NormalRgba.Length != expected)
                    throw new ArgumentException($"layer {i} normal is {layers[i].NormalRgba.Length} bytes, expected {expected} ({Width}x{Height} RGBA8).");
            }
        }
    }
}
