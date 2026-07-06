using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A filled, optionally-bordered rectangle used as a container / backdrop. When <see cref="BlocksPointer"/>
    /// is set, <see cref="Update"/> reserves its region on the <see cref="Pointer"/> so a layer beneath can
    /// check <see cref="Pointer.IsBlocked"/> and skip hit-testing under the panel (modal scrims, popups).
    /// </summary>
    public sealed class Panel
    {
        public Rect Bounds;
        /// <summary>If true, <see cref="Update"/> reserves <see cref="Bounds"/> via <see cref="Pointer.BlockRegion"/>.</summary>
        public bool BlocksPointer;

        public Vector4 Color = GuiTheme.Default.Surface;
        public Vector4 BorderColor = GuiTheme.Default.Border;
        public float BorderThickness;

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow); defaults to the flat <see cref="GuiStyle.Default"/> so
        /// the panel renders byte-identically to pre-7.8.0. The panel keeps its own <see cref="Color"/>/
        /// <see cref="BorderColor"/>/<see cref="BorderThickness"/> (the style's border thickness is overridden by
        /// the panel's). Set <c>Style = GuiStyle.Modern</c> for a rounded, shadowed backdrop.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        public Panel(Rect bounds) { Bounds = bounds; }

        /// <summary>Reserve the pointer region when <see cref="BlocksPointer"/>. Call before lower layers hit-test.</summary>
        public void Update(Pointer pointer)
        {
            if (BlocksPointer) pointer.BlockRegion(Bounds);
        }

        /// <summary>Draw the fill and (if any) border. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            GuiDraw.FillStyled(batch, white, Bounds, Style with { BorderThickness = BorderThickness }, Color, BorderColor);
        }
    }
}
