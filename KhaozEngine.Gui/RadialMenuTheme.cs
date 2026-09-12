using System.Numerics;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Semantic palette for <see cref="RadialMenu"/>. Defaults are derived from the ambient
    /// <see cref="GuiTheme"/> so a game palette also styles a newly constructed radial menu.
    /// </summary>
    public sealed class RadialMenuTheme
    {
        public Vector4 Shadow = WithAlpha(GuiTheme.Default.Background, 0.38f);
        public Vector4 Surface = WithAlpha(GuiTheme.Default.Surface, 0.78f);
        public Vector4 SurfaceHighlight = WithAlpha(GuiTheme.Default.SurfaceHover, 0.34f);
        public Vector4 Border = WithAlpha(GuiTheme.Default.Border, 0.72f);
        public Vector4 BorderActive = GuiTheme.Default.AccentBright;
        public Vector4 Accent = WithAlpha(GuiTheme.Default.Accent, 0.32f);
        public Vector4 Text = GuiTheme.Default.Text;
        public Vector4 TextMuted = GuiTheme.Default.TextMuted;
        /// <summary>
        /// Compact requirement text drawn beneath disabled entry labels. Its RGB is used directly so a semantic
        /// warning hue stays distinct from the disabled surface tint.
        /// </summary>
        public Vector4 DisabledDetail = GuiTheme.Default.Danger;
        /// <summary>
        /// Color for disabled icons and labels, plus the hue and alpha multiplier for disabled backgrounds and
        /// borders. Background channels keep their source luminance so their contrast remains intact.
        /// </summary>
        public Vector4 Disabled = WithAlpha(GuiTheme.Default.TextDisabled, 0.42f);
        public Vector4 Sheen = WithAlpha(GuiTheme.Default.Text, 0.08f);

        public static RadialMenuTheme Default => new();

        static Vector4 WithAlpha(Vector4 color, float alpha) =>
            new(color.X, color.Y, color.Z, alpha);
    }
}
