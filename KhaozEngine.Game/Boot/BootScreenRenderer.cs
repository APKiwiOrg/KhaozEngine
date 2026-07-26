using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Draws the <see cref="BootScreen"/>'s visuals for a given <see cref="BootView"/> snapshot: the optional
    /// background hook or flat fill, the optional logo, the title, the progress bar (showing the fraction fill, or -
    /// while indeterminate - the marquee over a bare track) and the current-step label, or - in the failure state -
    /// the error heading, message, and retry / quit buttons. Factored out of the scene so the exact same draw path is
    /// used by the live scene and by a headless PNG capture. The caller owns <c>batch.Begin</c>/<c>End</c> and
    /// <c>gui.Begin</c>. The button hit-test uses the pointer passed to <c>gui.Begin</c>.
    /// </summary>
    public static class BootScreenRenderer
    {
        /// <summary>
        /// Render the boot screen into <paramref name="bounds"/> with a fixed <see cref="SpriteFont"/>. The font is
        /// drawn at the theme scales (title / step) as baked, so on a HiDPI point-space pass the glyphs are
        /// bilinear-resampled by those scales. Prefer the <see cref="DpiFont"/> overload, which bakes each label at
        /// its exact device-pixel size for texel-crisp text. <paramref name="allowRetry"/>/<paramref name="allowQuit"/>
        /// decide which failure buttons appear. <paramref name="elapsedSeconds"/> drives the indeterminate marquee.
        /// <paramref name="retryClicked"/>/<paramref name="quitClicked"/> report a button press this frame (always
        /// false outside the failure state).
        /// </summary>
        public static void Draw(
            SpriteBatch batch, GuiSurface gui, Texture2D white, SpriteFont font,
            Rect bounds, in BootView view, BootScreenTheme theme,
            bool allowRetry, bool allowQuit, float elapsedSeconds,
            out bool retryClicked, out bool quitClicked)
            => DrawCore(batch, gui, white, _ => font, bounds, view, theme,
                allowRetry, allowQuit, elapsedSeconds, out retryClicked, out quitClicked);

        /// <summary>
        /// Render the boot screen into <paramref name="bounds"/> (in the same point space the batch was begun with)
        /// with a DPI-aware <paramref name="font"/>: every string is drawn from an atlas baked at its exact
        /// device-pixel size (<c>font.For(textScale * dpiScale)</c>, drawn 1:1) so text is texel-crisp on HiDPI
        /// instead of a fixed-oversample atlas bilinear-resampled by the theme scales. <paramref name="dpiScale"/> is
        /// the point-to-device scale of that batch's viewport (1 on a standard display, 2 on Retina). Build the font
        /// with <c>Surface2D.LoadDefaultDpiFont(pointSize, cacheSlots: 4)</c> so the title and the smaller labels can
        /// each stay baked. The other parameters match the <see cref="SpriteFont"/> overload.
        /// </summary>
        public static void Draw(
            SpriteBatch batch, GuiSurface gui, Texture2D white, DpiFont font, float dpiScale,
            Rect bounds, in BootView view, BootScreenTheme theme,
            bool allowRetry, bool allowQuit, float elapsedSeconds,
            out bool retryClicked, out bool quitClicked)
            => DrawCore(batch, gui, white, textScale => font.For(textScale * dpiScale), bounds, view, theme,
                allowRetry, allowQuit, elapsedSeconds, out retryClicked, out quitClicked);

        // Shared body. `role` maps a DrawString text scale to the SpriteFont to draw it with: the fixed-font overload
        // returns the same font at any scale (legacy behaviour). The DpiFont overload returns a device-scale atlas so
        // the glyph maps 1 texel to 1 device pixel. Layout is identical either way - the atlas reports its metrics at
        // the logical point height regardless of the bake density.
        static void DrawCore(
            SpriteBatch batch, GuiSurface gui, Texture2D white, Func<float, SpriteFont> role,
            Rect bounds, in BootView view, BootScreenTheme theme,
            bool allowRetry, bool allowQuit, float elapsedSeconds,
            out bool retryClicked, out bool quitClicked)
        {
            retryClicked = false;
            quitClicked = false;

            if (theme.DrawBackground is { } bg)
                bg(batch, white, bounds);
            else
                batch.Draw(white, new Vector4(bounds.X, bounds.Y, bounds.Width, bounds.Height), (Color)theme.Background);

            float cx = bounds.X + bounds.Width * 0.5f;
            float cy = bounds.Y + bounds.Height * 0.5f;

            if (theme.Logo is { } logo && theme.LogoHeight > 0f)
            {
                float lh = theme.LogoHeight;
                float lw = logo.Height > 0 ? lh * (logo.Width / (float)logo.Height) : lh;
                batch.Draw(logo, new Vector4(cx - lw * 0.5f, cy - 150f - lh, lw, lh), Color.White);
            }

            DrawCentered(batch, role, Resolve(theme.Title), cx, cy - 96f, theme.TitleColor, theme.TitleScale);

            if (view.State == BootState.Failed)
            {
                DrawFailure(batch, gui, role, cx, cy, view, theme, allowRetry, allowQuit, out retryClicked, out quitClicked);
                return;
            }

            var barRect = new Rect(cx - theme.BarWidth * 0.5f, cy - theme.BarHeight * 0.5f, theme.BarWidth, theme.BarHeight);
            // An indeterminate step has no meaningful fraction, and `view.Fraction` is stale from the last
            // determinate step, so a static fill drawn under the bouncing marquee would read as two competing
            // indicators in one track.
            var bar = new ProgressBar(barRect, view.Indeterminate ? 0f : view.Fraction)
            {
                TrackColor = theme.BarTrack,
                FillColor = theme.BarFill,
                BorderColor = theme.BarBorder,
                Style = theme.BarStyle,
            };
            bar.Draw(batch, white);

            if (view.Indeterminate)
                DrawMarquee(batch, white, bar.InnerBounds, theme, elapsedSeconds);

            string label = view.State == BootState.Restarting
                ? BootStrings.Resolve(BootStrings.Restarting)
                : Resolve(view.StepLabel);
            DrawCentered(batch, role, label, cx, barRect.Bottom + 18f, theme.StepColor, theme.StepScale);
        }

        static void DrawFailure(
            SpriteBatch batch, GuiSurface gui, Func<float, SpriteFont> role, float cx, float cy,
            in BootView view, BootScreenTheme theme, bool allowRetry, bool allowQuit,
            out bool retryClicked, out bool quitClicked)
        {
            retryClicked = false;
            quitClicked = false;

            DrawCentered(batch, role, BootStrings.Resolve(BootStrings.ErrorTitle), cx, cy - 34f, theme.ErrorTitleColor, theme.TitleScale * 0.8f);

            string message = view.FailureMessage is { } fm ? Resolve(fm) : BootStrings.Resolve(BootStrings.ErrorGeneric);
            DrawCentered(batch, role, message, cx, cy + 4f, theme.ErrorBodyColor, theme.StepScale);

            const float bw = 130f, bh = 40f, gap = 16f;
            int count = (allowRetry ? 1 : 0) + (allowQuit ? 1 : 0);
            if (count == 0) return;

            float totalW = count * bw + (count - 1) * gap;
            float x = cx - totalW * 0.5f;
            float y = cy + 48f;

            // The button caption draws at the widget's own 1:1 scale, so the unit-scale role font (a device-scale
            // atlas under the DpiFont overload) is texel-exact.
            SpriteFont buttonFont = role(1f);

            // Pre-resolve the caption through the fallback catalog (so an engine key still shows English with no wired
            // catalog), then hand the button the final text. The button widget itself resolves against the ambient
            // catalog only, which would miss the built-in English fallback.
            if (allowRetry)
            {
                if (gui.Button(buttonFont, new Rect(x, y, bw, bh), LocalizedText.Raw(BootStrings.Resolve(BootStrings.Retry)), theme.ButtonStyle))
                    retryClicked = true;
                x += bw + gap;
            }
            if (allowQuit)
            {
                if (gui.Button(buttonFont, new Rect(x, y, bw, bh), LocalizedText.Raw(BootStrings.Resolve(BootStrings.Quit)), theme.ButtonStyle))
                    quitClicked = true;
            }
        }

        // A small highlight quad swept back and forth across the track, so an indeterminate step still shows activity.
        static void DrawMarquee(SpriteBatch batch, Texture2D white, Rect inner, BootScreenTheme theme, float elapsedSeconds)
        {
            if (inner.Width <= 0f || inner.Height <= 0f) return;
            float quadW = inner.Width * 0.22f;
            float phase = elapsedSeconds * 0.6f;
            float u = phase - MathF.Floor(phase);
            float pingPong = u < 0.5f ? u * 2f : (1f - u) * 2f;
            float x = inner.X + (inner.Width - quadW) * pingPong;
            // Drawn at the theme's own alpha. The marquee used to be knocked back to 0.6 so it did not swallow the
            // fraction fill it swept over, but nothing is drawn under it now, so that only blended it toward the
            // track and cost the fill's hue and brightness. A game wanting a translucent marquee sets the alpha.
            batch.Draw(white, new Vector4(x, inner.Y, quadW, inner.Height), (Color)theme.MarqueeColor);
        }

        // Resolve boot text through the fallback catalog, so an engine boot.* key shows English when no catalog is
        // wired, a game key resolves through its wired catalog, and a raw value returns verbatim.
        static string Resolve(LocalizedText text) => text.Resolve(BootStrings.FallbackCatalog);

        static void DrawCentered(SpriteBatch batch, Func<float, SpriteFont> role, string text, float centerX, float y, Vector4 color, float scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            // The role font for this text scale (a device-size atlas under the DpiFont overload, so drawing at `scale`
            // maps 1 atlas texel to 1 device pixel - crisp - instead of resampling a fixed-oversample atlas). Metrics
            // are reported at the logical point height regardless of bake density, so centring is unchanged.
            SpriteFont f = role(scale);
            Vector2 size = f.Measure(text) * scale;
            batch.DrawString(f, text, new Vector2(centerX - size.X * 0.5f, y), (Color)color, scale);
        }
    }
}
