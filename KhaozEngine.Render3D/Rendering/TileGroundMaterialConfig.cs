using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>One layer of a tile-ground material: a tileable albedo (RGBA8, the same WxH as every other layer in
    /// the set), a tint, and the tiling rate (texture repeats per world metre). Render-data only. The renderer
    /// uploads the pixels into one layer of the material's texture array, and the layer's INDEX in the set is the
    /// slot a mesh vertex names. Albedo only by design: R5 ships no normal maps for tile ground (the register is
    /// flat low-frequency texture under smooth lighting), so there is no normal channel here.</summary>
    public sealed class TileGroundLayerImage
    {
        /// <summary>Row-major RGBA8 pixels, exactly width * height * 4 bytes for the set's size.</summary>
        public byte[] AlbedoRgba = Array.Empty<byte>();

        /// <summary>Multiplied into the sampled texel. A textured catalog material takes white (the texture IS the
        /// colour), a flat-colour fallback layer takes white over a filled image rather than tinting a grey.</summary>
        public Color Tint = Color.White;

        /// <summary>Texture repeats per world metre. The default puts a 2 m repeat on the ground, which at 1 unit =
        /// 1 metre is two tiles per repeat and reads at the intended grain.</summary>
        public float TilesPerMetre = 0.5f;
    }

    /// <summary>Pure configuration for a tile-ground material set: the slot ceiling, full mip-chain sizing, and the
    /// std140 params packing that rides after the shared frame block in the pipeline's ONE uniform buffer. No GPU,
    /// headless-testable.</summary>
    public static class TileGroundMaterialConfig
    {
        /// <summary>Maximum layers (material slots) in one set. 64 vec4 of params is 1 KB, and a catalog larger than
        /// this is split across several sets by the caller.</summary>
        public const int MaxMaterials = 64;

        /// <summary>Params-tail size in bytes: <c>vec4 TintTiling[MaxMaterials]</c> plus one <c>vec4 Misc</c>, which
        /// is what <see cref="BuildParams"/> returns and what the renderer appends after the frame block.</summary>
        public const uint ParamsBytes = (MaxMaterials + 1) * 16;

        /// <summary>Index of the <c>Misc</c> vector in the <see cref="BuildParams"/> result (it follows the 64
        /// TintTiling entries). Misc.x is the base specular strength and Misc.y is the material layer count.</summary>
        public const int MiscIndex = MaxMaterials;

        /// <summary>Full mip-chain level count for a WxH texture: floor(log2(max(w,h))) + 1.</summary>
        public static uint MipLevelCount(int width, int height)
        {
            int max = Math.Max(1, Math.Max(width, height));
            uint levels = 1;
            while (max > 1) { max >>= 1; levels++; }
            return levels;
        }

        /// <summary>Pack the per-layer tint and tiling plus the globals into the std140 params tail: 64 entries of
        /// (tint.rgb, tilesPerMetre) followed by Misc = (baseSpecStrength, layerCount, 0, 0). Layer i lands at entry i, so a
        /// vertex naming slot i reads that layer's params. Entries past the layer count are ZEROED rather than
        /// defaulted, so a mesh naming a slot the set never filled renders black instead of borrowing another
        /// material's look, which is what makes that mesher bug visible.</summary>
        public static Vector4[] BuildParams(IReadOnlyList<TileGroundLayerImage> layers, float baseSpecStrength)
        {
            if (layers.Count < 1 || layers.Count > MaxMaterials)
                throw new ArgumentException(
                    $"a tile-ground material needs 1 to {MaxMaterials} layers, got {layers.Count}.", nameof(layers));

            var tail = new Vector4[MaxMaterials + 1];
            for (int i = 0; i < layers.Count; i++)
            {
                Vector4 t = layers[i].Tint;
                tail[i] = new Vector4(t.X, t.Y, t.Z, layers[i].TilesPerMetre);
            }
            tail[MiscIndex] = new Vector4(baseSpecStrength, layers.Count, 0f, 0f);
            return tail;
        }
    }
}
