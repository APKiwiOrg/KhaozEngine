using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Look and layout for the <see cref="BootScreen"/>: every colour, the bar geometry, an optional logo, and an
    /// optional custom-background hook are injected here, so a game restyles the boot screen without forking the
    /// scene. Defaults derive from <see cref="GuiTheme.Default"/>, so a game that sets <c>GuiTheme.Default</c> at
    /// startup (its house palette) gets a boot screen that already matches. Assign a <see cref="GuiStyle"/> with a
    /// <see cref="GuiSkin"/> to skin the bar / buttons, set <see cref="Logo"/> for a splash bitmap, or set
    /// <see cref="DrawBackground"/> to paint a fully custom backdrop. Colours are <see cref="Vector4"/> RGBA in 0..1
    /// (the <see cref="Color"/> literals convert implicitly).
    /// </summary>
    public sealed class BootScreenTheme
    {
        /// <summary>Flat background fill painted behind everything (used when <see cref="DrawBackground"/> is null).</summary>
        public Vector4 Background = GuiTheme.Default.Background;

        /// <summary>Fill for the error panel shown in the failure state.</summary>
        public Vector4 ErrorPanelFill = GuiTheme.Default.Surface;

        /// <summary>Border for the error panel.</summary>
        public Vector4 ErrorPanelBorder = GuiTheme.Default.Border;

        /// <summary>Progress-bar track (background) colour.</summary>
        public Vector4 BarTrack = GuiTheme.Default.Surface;

        /// <summary>Progress-bar accent (fill) colour.</summary>
        public Vector4 BarFill = GuiTheme.Default.Accent;

        /// <summary>Progress-bar border colour.</summary>
        public Vector4 BarBorder = GuiTheme.Default.Border;

        /// <summary>Colour of the indeterminate-activity marquee swept across the bar.</summary>
        public Vector4 MarqueeColor = GuiTheme.Default.AccentBright;

        /// <summary>Title text colour.</summary>
        public Vector4 TitleColor = GuiTheme.Default.Text;

        /// <summary>Current-step label colour.</summary>
        public Vector4 StepColor = GuiTheme.Default.TextMuted;

        /// <summary>Error heading colour.</summary>
        public Vector4 ErrorTitleColor = GuiTheme.Default.DangerBright;

        /// <summary>Error body colour.</summary>
        public Vector4 ErrorBodyColor = GuiTheme.Default.Text;

        /// <summary>Style (corner radius / shadow / skin) for the bar track + frame.</summary>
        public GuiStyle BarStyle = GuiStyle.Default;

        /// <summary>Style for the retry / quit buttons in the failure state.</summary>
        public GuiStyle ButtonStyle = GuiStyle.Default;

        /// <summary>The title shown above the bar (a <see cref="KhaozEngine.App.LocalizedText"/>, default
        /// <see cref="BootStrings.Title"/>).</summary>
        public KhaozEngine.App.LocalizedText Title = BootStrings.Title;

        /// <summary>Optional splash logo, drawn centered above the title. A game asset (so the screen is only truly
        /// zero-dependency without it). Null (default) draws no logo.</summary>
        public Texture2D? Logo;

        /// <summary>Rendered logo height in points. The width preserves the texture aspect. Only used when
        /// <see cref="Logo"/> is set.</summary>
        public float LogoHeight = 120f;

        /// <summary>Progress-bar width in points.</summary>
        public float BarWidth = 420f;

        /// <summary>Progress-bar height in points.</summary>
        public float BarHeight = 12f;

        /// <summary>Uniform scale applied to the title text.</summary>
        public float TitleScale = 0.85f;

        /// <summary>Uniform scale applied to the step / status label text.</summary>
        public float StepScale = 0.6f;

        /// <summary>
        /// Optional fully-custom background hook. When set it is called first each frame with the batch, a 1x1 white
        /// texture, and the full screen bounds, REPLACING the flat <see cref="Background"/> fill (a game paints its own
        /// gradient / art here). The bar + text draw on top.
        /// </summary>
        public Action<SpriteBatch, Texture2D, Rect>? DrawBackground;

        /// <summary>A fresh default theme (neutral palette from <see cref="GuiTheme.Default"/>).</summary>
        public static BootScreenTheme Default => new();
    }
}
