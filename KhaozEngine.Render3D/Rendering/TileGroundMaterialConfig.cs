using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>One layer of a tile-ground material: a tileable albedo (RGBA8, the same WxH as every other layer in
    /// the set), a tint, and the tiling rate (texture repeats per world metre). Render-data only. The renderer
    /// uploads the pixels into one layer of the material's texture array, and the layer's INDEX in the set is the
    /// slot a mesh vertex names. A layer that sets none of the relief members (<see cref="SurfaceRgba"/>,
    /// <see cref="HeightMapped"/>, <see cref="FeatherBreakup"/>, <see cref="AntiTiling"/>) is a plain layer, and a
    /// set of plain layers draws through the unchanged albedo-only pipeline. See <see cref="TileGroundRelief"/>.</summary>
    public sealed class TileGroundLayerImage
    {
        /// <summary>Row-major RGBA8 pixels, exactly width * height * 4 bytes for the set's size. RGB is the albedo.
        /// Alpha is ignored unless <see cref="HeightMapped"/> says it carries this layer's height map, which is where
        /// the height lives so the relief pipeline reads it with the albedo tap it already takes.</summary>
        public byte[] AlbedoRgba = Array.Empty<byte>();

        /// <summary>This layer's surface texels, row-major RGBA8 at the SET's surface size (which may differ from the
        /// albedo size): a tangent-space normal in RGB (glTF convention, +x along +u and +y toward the top of the
        /// image) and perceptual roughness in A. Null for a layer with neither map. The relief pipeline uploads the
        /// non-null ones, in slot order, into one surface array beside the albedo array, so the array holds only the
        /// layers that use it. At least one of <see cref="NormalMapped"/> and <see cref="RoughnessMapped"/> must say
        /// which half is authored.</summary>
        public byte[]? SurfaceRgba;

        /// <summary>True when <see cref="SurfaceRgba"/>'s RGB is an authored normal map. False keeps the lattice
        /// normal and takes no normal decode, whatever the RGB holds.</summary>
        public bool NormalMapped;

        /// <summary>True when <see cref="SurfaceRgba"/>'s alpha is an authored roughness map. False keeps the
        /// pipeline's map-less specular, whatever the alpha holds.</summary>
        public bool RoughnessMapped;

        /// <summary>True when <see cref="AlbedoRgba"/>'s alpha is this layer's height map (0 low, 1 high, with
        /// <see cref="TileGroundRelief.NeutralHeight"/> level with a layer that has none). It resolves this layer's
        /// blends against its neighbours, so the higher surface wins locally.</summary>
        public bool HeightMapped;

        /// <summary>How crisply this layer's height map resolves its blends, from
        /// <see cref="TileGroundRelief.MinimumHeightBlendSharpness"/> (a soft, height-biased ramp) to
        /// <see cref="TileGroundRelief.MaximumHeightBlendSharpness"/>. Read only when <see cref="HeightMapped"/>.</summary>
        public float HeightBlendSharpness = TileGroundRelief.DefaultHeightBlendSharpness;

        /// <summary>How far this layer's edge wanders when it is a feathered overlay, 0 (the straight inward ramp)
        /// to 1. A deterministic world-space warp of the feather, so it needs no map.</summary>
        public float FeatherBreakup;

        /// <summary>Hex-tiling of this layer's top-down taps against a visible repeat, or None.</summary>
        public TileGroundAntiTiling AntiTiling;

        /// <summary>Multiplied into the sampled texel. A textured catalog material takes white (the texture IS the
        /// colour), a flat-colour fallback layer takes white over a filled image rather than tinting a grey.</summary>
        public Color Tint = Color.White;

        /// <summary>Texture repeats per world metre. The default puts a 2 m repeat on the ground, which at 1 unit =
        /// 1 metre is two tiles per repeat and reads at the intended grain.</summary>
        public float TilesPerMetre = 0.5f;

        /// <summary>The layer this one blends toward on steep ground, or null for none. See
        /// <see cref="TileGroundSteepLayer"/>.</summary>
        public TileGroundSteepLayer? Steep;
    }

    /// <summary>Pure configuration for a tile-ground material set: the slot ceiling, full mip-chain sizing, and the
    /// std140 params packing that rides after the shared frame block in the pipeline's ONE uniform buffer. No GPU,
    /// headless-testable.</summary>
    public static class TileGroundMaterialConfig
    {
        /// <summary>Maximum layers (material slots) in one set. Two arrays of 64 vec4 of params is 2 KB, and a
        /// catalog larger than this is split across several sets by the caller.</summary>
        public const int MaxMaterials = 64;

        /// <summary>Params size in bytes: <c>vec4 TintTiling[MaxMaterials]</c>, one <c>vec4 Misc</c>, one
        /// <c>vec4 Triplanar</c> and <c>vec4 SteepBand[MaxMaterials]</c>, which is what <see cref="BuildParams"/>
        /// returns and what the material's own uniform buffer holds.</summary>
        public const uint ParamsBytes = (2 * MaxMaterials + 2) * 16;

        /// <summary>Index of the <c>Misc</c> vector in the <see cref="BuildParams"/> result (it follows the 64
        /// TintTiling entries). Misc.x is the base specular strength and Misc.y is the material layer count.</summary>
        public const int MiscIndex = MaxMaterials;

        /// <summary>Index of the <c>Triplanar</c> vector, right after <c>Misc</c>. See
        /// <see cref="TileGroundSteepShading.TriplanarParams"/>.</summary>
        public const int TriplanarIndex = MiscIndex + 1;

        /// <summary>Index of the first <c>SteepBand</c> entry, right after <c>Triplanar</c>. Layer i's companion
        /// lands at <c>SteepBandIndex + i</c>. See <see cref="TileGroundSteepShading.SteepBand"/>.</summary>
        public const int SteepBandIndex = TriplanarIndex + 1;

        /// <summary>Index of the first relief <c>Surface</c> entry, after the 64 SteepBand entries. Only the relief
        /// params block (<see cref="BuildReliefParams"/>) carries it. See <see cref="TileGroundRelief.SurfaceEntry"/>.</summary>
        public const int SurfaceIndex = SteepBandIndex + MaxMaterials;

        /// <summary>Index of the first relief <c>Blend</c> entry, after the 64 Surface entries. See
        /// <see cref="TileGroundRelief.BlendEntry"/>.</summary>
        public const int BlendIndex = SurfaceIndex + MaxMaterials;

        /// <summary>Index of the relief block's one <c>Relief</c> vector, after the 64 Blend entries. See
        /// <see cref="TileGroundRelief.ReliefParams"/>.</summary>
        public const int ReliefIndex = BlendIndex + MaxMaterials;

        /// <summary>Relief params size in bytes: the plain block (<see cref="ParamsBytes"/>), then
        /// <c>vec4 Surface[MaxMaterials]</c>, <c>vec4 Blend[MaxMaterials]</c> and one <c>vec4 Relief</c>, which is
        /// what <see cref="BuildReliefParams"/> returns and what a relief material's uniform buffer holds.</summary>
        public const uint ReliefParamsBytes = (ReliefIndex + 1) * 16;

        /// <summary>Full mip-chain level count for a WxH texture: floor(log2(max(w,h))) + 1.</summary>
        public static uint MipLevelCount(int width, int height)
        {
            int max = Math.Max(1, Math.Max(width, height));
            uint levels = 1;
            while (max > 1) { max >>= 1; levels++; }
            return levels;
        }

        /// <summary>Pack the per-layer tint and tiling plus the globals into the std140 params block: 64 entries of
        /// (tint.rgb, tilesPerMetre), then Misc = (baseSpecStrength, layerCount, 0, 0), then the slope shading's
        /// Triplanar vector and 64 SteepBand entries (<see cref="TileGroundSteepShading"/>). Layer i lands at entry
        /// i of each array, so a vertex naming slot i reads that layer's params. Entries past the layer count are
        /// ZEROED rather than defaulted, so a mesh naming a slot the set never filled renders black instead of
        /// borrowing another material's look, which is what makes that mesher bug visible. A zero SteepBand entry is
        /// a layer with no companion.</summary>
        public static Vector4[] BuildParams(IReadOnlyList<TileGroundLayerImage> layers, float baseSpecStrength)
        {
            if (layers.Count < 1 || layers.Count > MaxMaterials)
                throw new TileGroundUploadArgumentException(TileGroundUploadRefusal.LayerCountOutsideRange,
                    $"a tile-ground material needs 1 to {MaxMaterials} layers, got {layers.Count}.", nameof(layers))
                { LayerCount = layers.Count, MinimumLayerCount = 1, MaximumLayerCount = MaxMaterials };

            var tail = new Vector4[ParamsBytes / 16];
            for (int i = 0; i < layers.Count; i++)
            {
                Vector4 t = layers[i].Tint;
                tail[i] = new Vector4(t.X, t.Y, t.Z, layers[i].TilesPerMetre);
            }
            tail[MiscIndex] = new Vector4(baseSpecStrength, layers.Count, 0f, 0f);
            TileGroundSteepShading.Pack(layers, tail);
            return tail;
        }

        /// <summary>The relief pipeline's params block: everything <see cref="BuildParams"/> packs, at the same
        /// indices, then each layer's Surface and Blend entries and the Relief vector
        /// (<see cref="TileGroundRelief"/>). Entries past the layer count stay zero, which is a plain layer. Refuses
        /// exactly what <see cref="BuildParams"/> refuses, then what <see cref="TileGroundRelief.Validate"/>
        /// refuses.</summary>
        public static Vector4[] BuildReliefParams(IReadOnlyList<TileGroundLayerImage> layers, float baseSpecStrength)
        {
            Vector4[] plain = BuildParams(layers, baseSpecStrength);
            var tail = new Vector4[ReliefParamsBytes / 16];
            Array.Copy(plain, tail, plain.Length);
            TileGroundRelief.Pack(layers, tail);
            return tail;
        }
    }
}
