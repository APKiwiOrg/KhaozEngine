using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>How tiled detail textures are projected onto a splat-terrain surface. Triplanar blends three
    /// world-plane projections by the surface normal (no per-vertex tangent needed, no cliff smear); PlanarXz
    /// projects straight down (cheaper, smears steep faces) as a perf escape hatch.</summary>
    public enum SplatProjection { Triplanar = 0, PlanarXz = 1 }

    /// <summary>One layer of a splat material: a tileable albedo + tangent-space normal (RGBA8, same WxH as every
    /// other layer in the stack), a tint, the tiling rate (texture tiles per world metre), and a scalar roughness.
    /// Render-data only; the renderer uploads the pixels into a texture-array layer.</summary>
    public sealed class SplatLayerImage
    {
        public byte[] AlbedoRgba = Array.Empty<byte>();
        public byte[] NormalRgba = Array.Empty<byte>();
        public Color Tint = Color.White;
        public float TilesPerMetre = 0.25f;
        public float Roughness = 0.85f;
    }

    /// <summary>The per-material fragment uniforms (std140), 112 bytes: per-layer tint+tiling, per-layer scalar
    /// roughness, and globals (triplanar sharpness, projection mode, base specular strength). Field order MUST
    /// mirror the SplatParams UBO block in SplatFrag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SplatParamsData
    {
        public Vector4 TintTiling0;  // xyz = tint, w = tiles/metre  (layer 0)
        public Vector4 TintTiling1;
        public Vector4 TintTiling2;
        public Vector4 TintTiling3;
        public Vector4 TintTiling4;
        public Vector4 Roughness;    // x..w = roughness for layers 0..3
        public Vector4 Misc;         // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
        public const uint SizeInBytes = 112;
    }

    /// <summary>Pure configuration for the 5-layer splat material: the fixed layer count, full mip-chain sizing,
    /// and the std140 params packing. No GPU; headless-testable.</summary>
    public static class SplatMaterialConfig
    {
        /// <summary>Fixed number of splat layers (matches the five terrain weights). The shader hardcodes this.</summary>
        public const int LayerCount = 5;

        /// <summary>Full mip-chain level count for a WxH texture: floor(log2(max(w,h))) + 1.</summary>
        public static uint MipLevelCount(int width, int height)
        {
            int max = Math.Max(1, Math.Max(width, height));
            uint levels = 1;
            while (max > 1) { max >>= 1; levels++; }
            return levels;
        }

        /// <summary>Pack the per-layer scalars (tint/tiling/roughness) + globals into the std140 params block.
        /// Requires exactly <see cref="LayerCount"/> layers, in channel order.</summary>
        public static SplatParamsData BuildParams(IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness, SplatProjection projection, float baseSpecStrength)
        {
            if (layers.Count != LayerCount)
                throw new ArgumentException($"a splat material needs exactly {LayerCount} layers, got {layers.Count}.", nameof(layers));

            static Vector4 TintTiling(SplatLayerImage l)
            {
                Vector4 t = l.Tint;
                return new Vector4(t.X, t.Y, t.Z, l.TilesPerMetre);
            }
            return new SplatParamsData
            {
                TintTiling0 = TintTiling(layers[0]),
                TintTiling1 = TintTiling(layers[1]),
                TintTiling2 = TintTiling(layers[2]),
                TintTiling3 = TintTiling(layers[3]),
                TintTiling4 = TintTiling(layers[4]),
                Roughness = new Vector4(layers[0].Roughness, layers[1].Roughness, layers[2].Roughness, layers[3].Roughness),
                Misc = new Vector4(layers[4].Roughness, triplanarSharpness, (float)projection, baseSpecStrength),
            };
        }
    }
}
