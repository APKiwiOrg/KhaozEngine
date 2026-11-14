using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>How a tile-ground layer hides its texture repeat. Hex-tiling (Mikkelsen 2022) samples each top-down
    /// tap three times from a hexagonal lattice of randomly placed copies and blends them by height, or by luminance
    /// for a layer without a height map. Side projections on steep ground stay plain.</summary>
    public enum TileGroundAntiTiling
    {
        /// <summary>One tap per projection, the texture repeats as authored.</summary>
        None = 0,
        /// <summary>Hex cells take random offsets only, which keeps a directional texture (wood, rows of setts)
        /// pointing the way it was authored.</summary>
        Offset = 1,
        /// <summary>Hex cells take random offsets and rotations, which hides the repeat best on an isotropic texture
        /// (grass, dirt, gravel, random stones).</summary>
        Rotate = 2,
    }

    /// <summary>
    /// The relief half of the tile-ground pipeline: the constants of its height blend, feather breakup and
    /// hex-tiling, the rule that selects it, and the packing of each layer's relief members into the params block.
    /// Pure, no GPU.
    /// <para>A set none of whose layers uses a relief member draws through the plain tile-ground program, which is
    /// unchanged, so a consumer that ships no maps keeps today's shader, output and cost. Any relief member on any
    /// layer selects the relief program for the whole set, and inside it a layer without maps still takes the plain
    /// formulas: the lattice normal, the map-less specular and a linear blend against other map-less layers.</para>
    /// <para>HEIGHT BLEND. Where layers meet, at blended corners and at feathered overlay edges, the weights are
    /// resolved by the layers' height maps. Layers without one join one flat group at <see cref="NeutralHeight"/>
    /// whose members keep their linear split. The group and each height-mapped layer take the logit
    /// <c>ln(weight) + HeightInfluence * height</c>, and the resolved weights are a softmax of those logits scaled by
    /// the fragment's sharpness, the weight-mean of the height-mapped layers' <see cref="TileGroundLayerImage.HeightBlendSharpness"/>.
    /// A layer with weight 0 stays at 0, so a shared corner keeps its one material and a shared edge depends only
    /// on its two materials and the world-anchored heights, which is what keeps the blend seamless across tiles,
    /// regions and both ground LODs.</para>
    /// <para>FEATHER BREAKUP. A feathered overlay's weight o becomes <c>o ^ (1 + 6 * breakup * m)</c>, with m a
    /// deterministic two-octave world-space value noise in [0, 1]. The exponent never drops below 1, so the overlay
    /// never reaches further out than the straight feather and its fringe at the tile boundary stays as soft as it
    /// was: an exponent below 1 would steepen that fringe into a visible line along the tile grid, which is what
    /// the breakup is there to hide. The visible edge instead wanders inward, as deep as the 0.9 contour of the
    /// ramp at full breakup. 0 and 1 are fixed points, so the edge meets an unpainted neighbour exactly and the
    /// overlay's interior is untouched.</para>
    /// </summary>
    public static class TileGroundRelief
    {
        /// <summary>Sharpness a height-mapped layer takes when its catalog row names none. Crisp enough that a stone
        /// outline survives across the transition, soft enough that a low-contrast height map still reads as a
        /// gradual blend.</summary>
        public const float DefaultHeightBlendSharpness = 8f;

        /// <summary>The softest sharpness: a height-biased linear ramp.</summary>
        public const float MinimumHeightBlendSharpness = 1f;

        /// <summary>The crispest sharpness. Past it the boundary is already one texel wide at any useful height
        /// contrast, and the softmax exponent stays well inside float range.</summary>
        public const float MaximumHeightBlendSharpness = 64f;

        /// <summary>How far a height difference moves a boundary: a full height difference moves it to a 10:1
        /// weight ratio (ln 10). Bounded, so the largest blend a layer can win at the 0.001 weight below which the
        /// shader skips its taps is (0.001 * 10) to the sharpness, 1 percent at the softest, and a corner never pops.</summary>
        public const float HeightInfluence = 2.30258509f;

        /// <summary>The height of a layer without a height map: level with the middle of an authored one, so stones
        /// above it win and joints below it fill.</summary>
        public const float NeutralHeight = 0.5f;

        /// <summary>The breakup noise's first-octave wavelength in metres. The second octave runs at about a third of
        /// it, so an edge wanders over a metre or two with smaller irregularities along it.</summary>
        public const float BreakupWavelengthMetres = 1.6f;

        /// <summary>Hex-tiling lattice scale in hex rows per texture repeat, Mikkelsen's 2 * sqrt(3), so a hex
        /// spans a little over half a repeat and every repeat is covered by differently placed copies.</summary>
        public const float HexLatticeScale = 3.46410162f;

        /// <summary>True when any layer uses a relief member, which selects the relief program for the whole set.
        /// <see cref="TileGroundLayerImage.HeightBlendSharpness"/> alone selects nothing, because it only shapes a
        /// height map's blend.</summary>
        public static bool UsesRelief(IReadOnlyList<TileGroundLayerImage> layers)
        {
            ArgumentNullException.ThrowIfNull(layers);
            for (int i = 0; i < layers.Count; i++)
            {
                TileGroundLayerImage l = layers[i];
                if (l.SurfaceRgba is not null || l.HeightMapped || l.FeatherBreakup != 0f
                    || l.AntiTiling != TileGroundAntiTiling.None) return true;
            }
            return false;
        }

        /// <summary>How many layers carry a <see cref="TileGroundLayerImage.SurfaceRgba"/>, which is the surface
        /// array's layer count.</summary>
        public static int SurfaceLayerCount(IReadOnlyList<TileGroundLayerImage> layers)
        {
            ArgumentNullException.ThrowIfNull(layers);
            int count = 0;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].SurfaceRgba is not null) count++;
            return count;
        }

        /// <summary>Throws <see cref="TileGroundReliefArgumentException"/> with its
        /// <see cref="TileGroundReliefArgumentException.Reason"/> and
        /// <see cref="TileGroundReliefArgumentException.LayerIndex"/> for the first layer whose relief members
        /// disagree: a map flag with no surface texels, surface texels no flag claims, a sharpness outside
        /// [<see cref="MinimumHeightBlendSharpness"/>, <see cref="MaximumHeightBlendSharpness"/>], a breakup outside
        /// [0, 1], or an anti-tiling value the enum does not define. A sharpness is checked only on a height-mapped
        /// layer, the one place it is read.</summary>
        public static void Validate(IReadOnlyList<TileGroundLayerImage> layers)
        {
            ArgumentNullException.ThrowIfNull(layers);
            for (int i = 0; i < layers.Count; i++) Check(i, layers[i]);
        }

        /// <summary>One layer's <c>Surface</c> entry: (surface layer + 1, normal mapped, roughness mapped,
        /// anti-tiling). The surface layer is offset by one so zero can mean none, and the flags are 0 or 1.</summary>
        public static Vector4 SurfaceEntry(TileGroundLayerImage layer, int surfaceLayer) => new(
            layer.SurfaceRgba is null ? 0f : surfaceLayer + 1,
            layer.NormalMapped ? 1f : 0f,
            layer.RoughnessMapped ? 1f : 0f,
            (float)layer.AntiTiling);

        /// <summary>One layer's <c>Blend</c> entry: (height mapped, sharpness, feather breakup, 0). A layer without a
        /// height map packs sharpness 0, because the shader never reads it there.</summary>
        public static Vector4 BlendEntry(TileGroundLayerImage layer) => new(
            layer.HeightMapped ? 1f : 0f,
            layer.HeightMapped ? layer.HeightBlendSharpness : 0f,
            layer.FeatherBreakup,
            0f);

        /// <summary>The <c>Relief</c> vector of the params block: (<see cref="HeightInfluence"/>,
        /// <see cref="NeutralHeight"/>, 1 / <see cref="BreakupWavelengthMetres"/>, <see cref="HexLatticeScale"/>).</summary>
        public static Vector4 ReliefParams => new(HeightInfluence, NeutralHeight, 1f / BreakupWavelengthMetres,
            HexLatticeScale);

        /// <summary>Writes every layer's Surface and Blend entries and the Relief vector into
        /// <paramref name="tail"/>, after <see cref="Validate"/>. Surface layers are numbered in slot order over the
        /// layers that carry one, which is the order the renderer uploads them.</summary>
        internal static void Pack(IReadOnlyList<TileGroundLayerImage> layers, Vector4[] tail)
        {
            Validate(layers);
            int surfaceLayer = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                tail[TileGroundMaterialConfig.SurfaceIndex + i] = SurfaceEntry(layers[i], surfaceLayer);
                tail[TileGroundMaterialConfig.BlendIndex + i] = BlendEntry(layers[i]);
                if (layers[i].SurfaceRgba is not null) surfaceLayer++;
            }
            tail[TileGroundMaterialConfig.ReliefIndex] = ReliefParams;
        }

        static void Check(int index, TileGroundLayerImage layer)
        {
            ArgumentNullException.ThrowIfNull(layer);
            bool flagged = layer.NormalMapped || layer.RoughnessMapped;
            if (flagged && layer.SurfaceRgba is null)
                throw new TileGroundReliefArgumentException(TileGroundReliefRefusal.MapFlagWithoutSurface,
                    $"tile-ground layer {index} says it has a {(layer.NormalMapped ? "normal" : "roughness")} map but " +
                    "carries no surface texels.", "layers")
                { LayerIndex = index };
            if (!flagged && layer.SurfaceRgba is not null)
                throw new TileGroundReliefArgumentException(TileGroundReliefRefusal.SurfaceWithoutMapFlag,
                    $"tile-ground layer {index} carries surface texels that neither NormalMapped nor RoughnessMapped " +
                    "claims, so nothing would read them.", "layers")
                { LayerIndex = index };
            // Negated comparisons, so NaN is refused with the out-of-range values.
            if (layer.HeightMapped && !(layer.HeightBlendSharpness >= MinimumHeightBlendSharpness
                                        && layer.HeightBlendSharpness <= MaximumHeightBlendSharpness))
                throw new TileGroundReliefArgumentException(TileGroundReliefRefusal.HeightBlendSharpnessOutsideRange,
                    $"tile-ground layer {index} has height blend sharpness {Text(layer.HeightBlendSharpness)}, outside " +
                    $"[{Text(MinimumHeightBlendSharpness)}, {Text(MaximumHeightBlendSharpness)}].", "layers")
                { LayerIndex = index, Value = layer.HeightBlendSharpness };
            if (!(layer.FeatherBreakup >= 0f && layer.FeatherBreakup <= 1f))
                throw new TileGroundReliefArgumentException(TileGroundReliefRefusal.FeatherBreakupOutsideRange,
                    $"tile-ground layer {index} has feather breakup {Text(layer.FeatherBreakup)}, outside [0, 1].", "layers")
                { LayerIndex = index, Value = layer.FeatherBreakup };
            if (!Enum.IsDefined(layer.AntiTiling))
                throw new TileGroundReliefArgumentException(TileGroundReliefRefusal.AntiTilingUndefined,
                    $"tile-ground layer {index} has anti-tiling value {(int)layer.AntiTiling}, which " +
                    $"{nameof(TileGroundAntiTiling)} does not define.", "layers")
                { LayerIndex = index, Value = (int)layer.AntiTiling };
        }

        static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
