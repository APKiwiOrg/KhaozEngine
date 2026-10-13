using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>One tile-ground layer's steep companion, render data only: the slot of the layer it blends toward
    /// and the slope band, in degrees, that blend runs across. Below <see cref="FromDegrees"/> the layer renders
    /// alone and past <see cref="ToDegrees"/> the companion has replaced it. The band is measured on the
    /// interpolated ground normal, so it needs no vertex data of its own. The companion's own companion is never
    /// followed.</summary>
    public sealed class TileGroundSteepLayer
    {
        /// <summary>Slot of the companion layer in the same set. Another layer, never this one. A tile world's set
        /// also refuses its reserved missing-material slot here.</summary>
        public int CompanionSlot;

        /// <summary>Slope in degrees where the companion starts to show. Strictly inside (0, 90).</summary>
        public float FromDegrees;

        /// <summary>Slope in degrees where the companion has fully replaced the layer. Strictly inside (0, 90) and
        /// above <see cref="FromDegrees"/>.</summary>
        public float ToDegrees;
    }

    /// <summary>
    /// The slope-aware half of the tile-ground pipeline: the engine constants of its triplanar projection and the
    /// packing of each layer's <see cref="TileGroundSteepLayer"/> into the material's params block. Pure, no GPU.
    /// <para>TRIPLANAR. Every layer is sampled top-down (world XZ) on flat and gentle ground, exactly as the
    /// pipeline always has. Once the ground normal tilts past <see cref="TriplanarStartDegrees"/> two side
    /// projections fade in, one along world X (u = world Z) and one along world Z (u = world X), each with v
    /// running down world Y so a texture keeps its up. They reach their full normal-weighted share at
    /// <see cref="TriplanarFullDegrees"/>, weighted by the normal's axis components raised to
    /// <see cref="TriplanarSharpness"/>. A fragment below the start takes no side tap at all.</para>
    /// <para>STEEP COMPANION. Wherever a tap of a layer with a companion is taken, the tap becomes
    /// mix(layer, companion, s), s a smoothstep over the layer's band on the normal's Y component.</para>
    /// </summary>
    public static class TileGroundSteepShading
    {
        /// <summary>Slope in degrees where the side projections start to fade in. Below it the ground takes the
        /// single top-down tap it always took. 45 degrees stretches a top-down texel by about 1.4, which still reads
        /// as texture, and keeps every slope a walkable hill normally has on the unchanged path.</summary>
        public const float TriplanarStartDegrees = 45f;

        /// <summary>Slope in degrees where the side projections reach their full normal-weighted share. Faces past
        /// about 60 degrees are the ones a top-down projection smears into streaks
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1289).</summary>
        public const float TriplanarFullDegrees = 60f;

        /// <summary>Exponent on the normal's absolute axis components before they are normalised into projection
        /// weights. 4 keeps the cross-fade between the top and a side projection narrow, so a cliff face reads as
        /// one projection rather than a ghosted double image, without the hard seam a higher power draws.</summary>
        public const float TriplanarSharpness = 4f;

        /// <summary>The <c>Triplanar</c> vector of the params block: (cos start, cos full, sharpness, 0). Cosines,
        /// because the shader compares them with the normal's Y component and takes no inverse trigonometry.</summary>
        public static Vector4 TriplanarParams => new(
            Cos(TriplanarStartDegrees), Cos(TriplanarFullDegrees), TriplanarSharpness, 0f);

        /// <summary>One layer's <c>SteepBand</c> entry: (companion slot + 1, cos to, cos from, 0), or zero for a
        /// layer with no companion. The slot is offset by one so zero can mean none. The cosines are stored low
        /// then high, which is the edge order GLSL's smoothstep requires.</summary>
        public static Vector4 SteepBand(TileGroundSteepLayer? steep) => steep is null
            ? Vector4.Zero
            : new Vector4(steep.CompanionSlot + 1, Cos(steep.ToDegrees), Cos(steep.FromDegrees), 0f);

        /// <summary>Throws <see cref="ArgumentException"/> naming the first layer whose companion is unsound: one
        /// that names itself, names a slot outside <paramref name="layers"/>, carries a band that does not rise
        /// strictly inside (0, 90) degrees, or carries one whose two ends pack to the same cosine. Run by <see cref="TileGroundMaterialConfig.BuildParams"/>.</summary>
        public static void Validate(IReadOnlyList<TileGroundLayerImage> layers)
        {
            ArgumentNullException.ThrowIfNull(layers);
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Steep is { } steep) Check(i, steep, layers.Count);
        }

        /// <summary>Writes the Triplanar vector and every layer's SteepBand entry into <paramref name="tail"/>, after
        /// <see cref="Validate"/>.</summary>
        internal static void Pack(IReadOnlyList<TileGroundLayerImage> layers, Vector4[] tail)
        {
            Validate(layers);
            tail[TileGroundMaterialConfig.TriplanarIndex] = TriplanarParams;
            for (int i = 0; i < layers.Count; i++)
                tail[TileGroundMaterialConfig.SteepBandIndex + i] = SteepBand(layers[i].Steep);
        }

        static void Check(int slot, TileGroundSteepLayer steep, int layerCount)
        {
            if (steep.CompanionSlot == slot)
                throw new ArgumentException($"tile-ground layer {slot} names itself as its steep companion.", "layers");
            if (steep.CompanionSlot < 0 || steep.CompanionSlot >= layerCount)
                throw new ArgumentException(
                    $"tile-ground layer {slot} names slot {steep.CompanionSlot} as its steep companion, outside the " +
                    $"set's {layerCount} layers.", "layers");
            if (!(steep.FromDegrees > 0f && steep.ToDegrees < 90f && steep.FromDegrees < steep.ToDegrees))
                throw new ArgumentException(
                    $"tile-ground layer {slot} has the steep band {Text(steep.FromDegrees)} to {Text(steep.ToDegrees)} " +
                    "degrees, which must rise strictly inside (0, 90).", "layers");
            // The shader smoothsteps between the two packed cosines, and smoothstep is undefined unless its low
            // edge sits strictly below its high one. A band narrow enough, or close enough to 0 degrees, packs both
            // ends to the same float. GroundMaterialSteepRules makes the same check with the same expression.
            if (!(Cos(steep.ToDegrees) < Cos(steep.FromDegrees)))
                throw new ArgumentException(
                    $"tile-ground layer {slot} has the steep band {Text(steep.FromDegrees)} to {Text(steep.ToDegrees)} " +
                    "degrees, whose ends pack to the same cosine. Widen the band.", "layers");
        }

        static float Cos(float degrees) => MathF.Cos(degrees * (MathF.PI / 180f));

        static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
