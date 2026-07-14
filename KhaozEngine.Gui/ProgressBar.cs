using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A thin horizontal progress / fill bar (health, XP, cast, load): a themed track, an accent fill scaled by
    /// <see cref="Fraction"/> (always clamped 0..1) sitting inside the border frame, and an optional centered overlay
    /// label. The accent fill is flat, matching <see cref="Slider"/>'s fill look. Corners and border come from
    /// <see cref="Style"/>. Non-interactive: there is no Update, so call <see cref="Draw"/> each frame.
    /// </summary>
    public sealed class ProgressBar
    {
        /// <summary>The full bar rect (keep the height small for a thin bar).</summary>
        public Rect Bounds;

        float _fraction;
        /// <summary>Fill amount, always clamped to 0..1 on assignment.</summary>
        public float Fraction
        {
            get => _fraction;
            set => _fraction = value < 0f ? 0f : value > 1f ? 1f : value;
        }

        /// <summary>Track (background) fill.</summary>
        public Vector4 TrackColor = GuiTheme.Default.Surface;
        /// <summary>Accent fill colour (overridable, defaults to the theme accent).</summary>
        public Vector4 FillColor = GuiTheme.Default.Accent;
        /// <summary>Border frame colour.</summary>
        public Vector4 BorderColor = GuiTheme.Default.Border;
        /// <summary>Overlay-text colour.</summary>
        public Vector4 OverlayTextColor = GuiTheme.Default.Text;

        /// <summary>Look knobs (corner radius / shadow / border thickness) for the track + frame, defaulting to
        /// <see cref="GuiStyle.Default"/>.</summary>
        public GuiStyle Style = GuiStyle.Default;
        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque).</summary>
        public float Opacity = 1f;

        /// <summary>Optional centered label drawn over the bar (default <c>null</c> = none). A <see cref="LocalizedText"/>
        /// so a caption resolves through the catalog. Wrap a pure number / percentage in <see cref="LocalizedText.Raw"/>
        /// (a non-localizable token).</summary>
        public LocalizedText? OverlayText;

        /// <summary>Create a bar with an initial <paramref name="fraction"/> (clamped 0..1).</summary>
        public ProgressBar(Rect bounds, float fraction = 0f)
        {
            Bounds = bounds;
            Fraction = fraction;
        }

        /// <summary>The area inside the border frame, where the accent fill lives (inset by <see cref="Style"/>'s
        /// border thickness). Pure geometry.</summary>
        public Rect InnerBounds
        {
            get
            {
                float bt = Style.BorderThickness > 0f ? Style.BorderThickness : 0f;
                return new Rect(
                    Bounds.X + bt, Bounds.Y + bt,
                    MathF.Max(0f, Bounds.Width - 2f * bt),
                    MathF.Max(0f, Bounds.Height - 2f * bt));
            }
        }

        /// <summary>The accent fill rect at the current <see cref="Fraction"/> (width scales inside the border). Pure geometry.</summary>
        public Rect FillRect
        {
            get
            {
                Rect inner = InnerBounds;
                return new Rect(inner.X, inner.Y, inner.Width * _fraction, inner.Height);
            }
        }

        /// <summary>
        /// Draw the track + border, the accent fill, and the optional overlay text. <paramref name="white"/> is a 1x1
        /// white texture. <paramref name="font"/> renders <see cref="OverlayText"/> and is only needed when it is set.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont? font = null)
        {
            GuiDraw.FillStyled(batch, white, Bounds, Style,
                GuiDraw.WithOpacity(TrackColor, Opacity), GuiDraw.WithOpacity(BorderColor, Opacity));

            Rect fill = FillRect;
            if (fill.Width > 0f)
                GuiDraw.Fill(batch, white, fill, GuiDraw.WithOpacity(FillColor, Opacity));

            if (font != null && OverlayText is { } text)
            {
                string s = text.Resolve();
                if (s.Length > 0)
                {
                    Vector2 pos = GuiDraw.AlignedTextPos(Bounds, font.Measure(s), font.LineHeight, GuiAlign.Center);
                    batch.DrawString(font, s, pos, (Color)GuiDraw.WithOpacity(OverlayTextColor, Opacity));
                }
            }
        }
    }
}
