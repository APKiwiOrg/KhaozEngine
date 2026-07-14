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
    /// background hook or flat fill, the optional logo, the title, the progress bar (with an indeterminate-activity
    /// marquee when a step reports no measurable fraction) and the current-step label, or - in the failure state - the
    /// error heading, message, and retry / quit buttons. Factored out of the scene so the exact same draw path is
    /// used by the live scene and by a headless PNG capture. The caller owns <c>batch.Begin</c>/<c>End</c> and
    /// <c>gui.Begin</c>. The button hit-test uses the pointer passed to <c>gui.Begin</c>.
    /// </summary>
    public static class BootScreenRenderer
    {
        /// <summary>
        /// Render the boot screen into <paramref name="bounds"/>. <paramref name="allowRetry"/>/<paramref name="allowQuit"/>
        /// decide which failure buttons appear. <paramref name="elapsedSeconds"/> drives the indeterminate marquee.
        /// <paramref name="retryClicked"/>/<paramref name="quitClicked"/> report a button press this frame (always
        /// false outside the failure state).
        /// </summary>
        public static void Draw(
            SpriteBatch batch, GuiSurface gui, Texture2D white, SpriteFont font,
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

            DrawCentered(batch, font, Resolve(theme.Title), cx, cy - 96f, theme.TitleColor, theme.TitleScale);

            if (view.State == BootState.Failed)
            {
                DrawFailure(batch, gui, font, cx, cy, view, theme, allowRetry, allowQuit, out retryClicked, out quitClicked);
                return;
            }

            var barRect = new Rect(cx - theme.BarWidth * 0.5f, cy - theme.BarHeight * 0.5f, theme.BarWidth, theme.BarHeight);
            var bar = new ProgressBar(barRect, view.Fraction)
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
            DrawCentered(batch, font, label, cx, barRect.Bottom + 18f, theme.StepColor, theme.StepScale);
        }

        static void DrawFailure(
            SpriteBatch batch, GuiSurface gui, SpriteFont font, float cx, float cy,
            in BootView view, BootScreenTheme theme, bool allowRetry, bool allowQuit,
            out bool retryClicked, out bool quitClicked)
        {
            retryClicked = false;
            quitClicked = false;

            DrawCentered(batch, font, BootStrings.Resolve(BootStrings.ErrorTitle), cx, cy - 34f, theme.ErrorTitleColor, theme.TitleScale * 0.8f);

            string message = view.FailureMessage is { } fm ? Resolve(fm) : BootStrings.Resolve(BootStrings.ErrorGeneric);
            DrawCentered(batch, font, message, cx, cy + 4f, theme.ErrorBodyColor, theme.StepScale);

            const float bw = 130f, bh = 40f, gap = 16f;
            int count = (allowRetry ? 1 : 0) + (allowQuit ? 1 : 0);
            if (count == 0) return;

            float totalW = count * bw + (count - 1) * gap;
            float x = cx - totalW * 0.5f;
            float y = cy + 48f;

            // Pre-resolve the caption through the fallback catalog (so an engine key still shows English with no wired
            // catalog), then hand the button the final text. The button widget itself resolves against the ambient
            // catalog only, which would miss the built-in English fallback.
            if (allowRetry)
            {
                if (gui.Button(font, new Rect(x, y, bw, bh), LocalizedText.Raw(BootStrings.Resolve(BootStrings.Retry)), theme.ButtonStyle))
                    retryClicked = true;
                x += bw + gap;
            }
            if (allowQuit)
            {
                if (gui.Button(font, new Rect(x, y, bw, bh), LocalizedText.Raw(BootStrings.Resolve(BootStrings.Quit)), theme.ButtonStyle))
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
            Vector4 c = theme.MarqueeColor;
            c.W *= 0.6f;
            batch.Draw(white, new Vector4(x, inner.Y, quadW, inner.Height), (Color)c);
        }

        // Resolve boot text through the fallback catalog, so an engine boot.* key shows English when no catalog is
        // wired, a game key resolves through its wired catalog, and a raw value returns verbatim.
        static string Resolve(LocalizedText text) => text.Resolve(BootStrings.FallbackCatalog);

        static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, float centerX, float y, Vector4 color, float scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            Vector2 size = font.Measure(text) * scale;
            batch.DrawString(font, text, new Vector2(centerX - size.X * 0.5f, y), (Color)color, scale);
        }
    }
}
