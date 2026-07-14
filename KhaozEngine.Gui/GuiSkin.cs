using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>How a <see cref="GuiSkin"/>'s stretchable regions (the four edges and the centre) map onto the
    /// destination. Corners are NEVER scaled either way; this only governs the middle band.</summary>
    public enum GuiSkinCenter
    {
        /// <summary>Stretch the centre + edges to fill (the default nine-slice look).</summary>
        Stretch,
        /// <summary>Repeat the centre + edge source cells at their native source-pixel size, clipping the last row /
        /// column (patterned frames, tiled fills).</summary>
        Tile,
    }

    /// <summary>
    /// A sprite skin for a <see cref="GuiStyle"/>: a nine-slice frame drawn from a <see cref="Render2D.Texture2D"/>
    /// (or an atlas sub-region of one) so a widget renders textured chrome instead of a flat quad. Rides the same
    /// texture + source-UV mechanism the Gui already uses for <see cref="IconAtlas"/>; when a style carries a skin,
    /// every widget that draws through <see cref="GuiDraw.FillStyled"/> (Panel, Button, ProgressBar, TextInput,
    /// ScrollablePanel, Dropdown, PopupPanel, SlotGrid, ...) paints the skin instead of its flat fill, with the
    /// resolved state colour multiplied over the texture as a tint (set the style's Fill to white for the skin's
    /// native colours, and Hover/Press as tints).
    /// <para>
    /// The four <c>Inset*</c> values are in SOURCE pixels: the corners are that many pixels of the source region and
    /// render at that same size in the destination (unscaled), while the middle band stretches or tiles per
    /// <see cref="Center"/>. When the destination is too small for both opposing corners, the destination insets
    /// scale down proportionally so the corners meet in the middle (the source stays fixed).
    /// </para>
    /// <para>
    /// A skinned frame owns the silhouette, so <see cref="GuiDraw.FillStyled"/> skips the procedural
    /// <see cref="GuiStyle.CornerRadius"/> / border drawing when a skin is set. <see cref="GuiStyle.ShadowSize"/> still
    /// draws its drop shadow underneath. Per-state skins (a different texture on hover/press) are a future extension:
    /// today the state tint multiplies over the one skin.
    /// </para>
    /// </summary>
    public sealed class GuiSkin
    {
        /// <summary>The source texture (may be a shared atlas).</summary>
        public Texture2D Texture = null!;

        /// <summary>The source sub-region within <see cref="Texture"/> as (u0, v0, u1, v1) in 0..1. Default
        /// (0,0,1,1) = the whole texture.</summary>
        public Vector4 Source = new(0f, 0f, 1f, 1f);

        /// <summary>Pixel width of the <see cref="Source"/> region (used to map the pixel insets to UV and to size
        /// the native tiles). Defaults to the whole texture width via the factories.</summary>
        public float SourcePixelWidth;
        /// <summary>Pixel height of the <see cref="Source"/> region.</summary>
        public float SourcePixelHeight;

        /// <summary>Left nine-slice inset in source pixels.</summary>
        public float InsetLeft;
        /// <summary>Top nine-slice inset in source pixels.</summary>
        public float InsetTop;
        /// <summary>Right nine-slice inset in source pixels.</summary>
        public float InsetRight;
        /// <summary>Bottom nine-slice inset in source pixels.</summary>
        public float InsetBottom;

        /// <summary>How the middle band (edges + centre) maps onto the destination (default
        /// <see cref="GuiSkinCenter.Stretch"/>).</summary>
        public GuiSkinCenter Center = GuiSkinCenter.Stretch;

        /// <summary>True when the skin has a texture to draw.</summary>
        public bool HasTexture => Texture != null;

        /// <summary>
        /// The skin's frame insets in DESTINATION space for a widget of <paramref name="dest"/> size, as
        /// (X=left, Y=top, Z=right, W=bottom). This is the exact inset the nine-slice draws its corners/edges at:
        /// the source-pixel insets (corners render unscaled), scaled down proportionally on an axis when the
        /// destination is too small for both opposing corners (so the corners just meet). It is the single source
        /// of truth shared with <c>GuiDraw.NineSlicePatches</c>, so interior-content math can never drift from
        /// what the skin actually paints. Pure geometry, headless-testable.
        /// </summary>
        public Vector4 DestinationInsets(Primitives.Rect dest)
        {
            float l = System.MathF.Max(0f, InsetLeft), t = System.MathF.Max(0f, InsetTop);
            float r = System.MathF.Max(0f, InsetRight), b = System.MathF.Max(0f, InsetBottom);
            if (l + r > dest.Width && l + r > 0f) { float k = System.MathF.Max(0f, dest.Width) / (l + r); l *= k; r *= k; }
            if (t + b > dest.Height && t + b > 0f) { float k = System.MathF.Max(0f, dest.Height) / (t + b); t *= k; b *= k; }
            return new Vector4(l, t, r, b);
        }

        /// <summary>
        /// A nine-slice skin over the WHOLE of <paramref name="texture"/> with a uniform pixel <paramref name="inset"/>
        /// on all four edges. Source pixel dims come from the texture.
        /// </summary>
        public static GuiSkin NineSlice(Texture2D texture, float inset) =>
            NineSlice(texture, inset, inset, inset, inset);

        /// <summary>
        /// A nine-slice skin over the WHOLE of <paramref name="texture"/> with per-edge pixel insets. Source pixel
        /// dims come from the texture.
        /// </summary>
        public static GuiSkin NineSlice(Texture2D texture, float left, float top, float right, float bottom,
            GuiSkinCenter center = GuiSkinCenter.Stretch) => new()
        {
            Texture = texture,
            Source = new Vector4(0f, 0f, 1f, 1f),
            SourcePixelWidth = texture.Width,
            SourcePixelHeight = texture.Height,
            InsetLeft = left,
            InsetTop = top,
            InsetRight = right,
            InsetBottom = bottom,
            Center = center,
        };

        /// <summary>
        /// A nine-slice skin over an atlas SUB-REGION (<paramref name="source"/> = (u0,v0,u1,v1) in 0..1, with its
        /// pixel size <paramref name="sourcePixelWidth"/> x <paramref name="sourcePixelHeight"/>) and per-edge pixel
        /// insets. Use when the frame is one cell of a shared atlas.
        /// </summary>
        public static GuiSkin FromAtlas(Texture2D texture, Vector4 source, float sourcePixelWidth, float sourcePixelHeight,
            float left, float top, float right, float bottom, GuiSkinCenter center = GuiSkinCenter.Stretch) => new()
        {
            Texture = texture,
            Source = source,
            SourcePixelWidth = sourcePixelWidth,
            SourcePixelHeight = sourcePixelHeight,
            InsetLeft = left,
            InsetTop = top,
            InsetRight = right,
            InsetBottom = bottom,
            Center = center,
        };
    }
}
