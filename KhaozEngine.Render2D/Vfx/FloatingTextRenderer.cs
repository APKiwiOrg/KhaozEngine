using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// Draws a <see cref="FloatingTextStore"/> or a <see cref="FloatingBannerStore"/> through a
    /// <see cref="SpriteBatch"/>. The only piece of the floating-text feature that touches the GPU at all, which is
    /// what leaves the store and the curves headless-testable.
    /// <para>Every line is CENTRED on its point. <c>SpriteBatch.DrawString</c> places a top-left, so the measured
    /// size at the entry's own scale is halved off it here rather than at every call site.</para>
    /// <para>The batch's blend mode is left alone, unlike <see cref="AttentionBeacon"/>: text fades by alpha, and the
    /// straight-alpha mode a Gui pass is already in is the one that reads right over a lit world. A game that wants
    /// an additive glow sets the mode around the call.</para>
    /// </summary>
    public static class FloatingTextRenderer
    {
        /// <summary>
        /// Draws every live entry of <paramref name="store"/>.
        /// <para><paramref name="anchorOf"/> turns an anchor id into a design-space screen point, which is where the
        /// game's own camera lives: a 3D game projects a body, a 2D game reads a sprite. Returning null skips that
        /// entry for this frame WITHOUT dropping it, which is the right answer for a body that is off screen, behind
        /// the camera, or momentarily out of interest, because it comes back in the same column it left. A body that
        /// is gone for good is <see cref="FloatingTextStore.Clear(long)"/>'s business rather than this one's.</para>
        /// <para>Ages nothing. Call <see cref="FloatingTextStore.Age"/> once a frame from the update, not from here,
        /// so a store drawn twice (a split screen, a mirror) does not age twice.</para>
        /// </summary>
        /// <param name="batch">The batch to draw through, already begun.</param>
        /// <param name="font">The font every entry is drawn in.</param>
        /// <param name="store">The live set.</param>
        /// <param name="anchorOf">Resolves an anchor id to a design-space screen point, or null to skip it.</param>
        /// <param name="opacity">A master multiplier over every entry's own alpha, for fading the whole layer out.
        /// At or below zero nothing is drawn at all.</param>
        public static void Draw(SpriteBatch batch, SpriteFont font, FloatingTextStore store,
            Func<long, Vector2?> anchorOf, float opacity = 1f)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(font);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(anchorOf);
            if (opacity <= 0f) return;

            ReadOnlySpan<FloatingText> live = store.Live;
            for (int i = 0; i < live.Length; i++)
            {
                FloatingText e = live[i];
                float alpha = FloatingTextCurves.AlphaAt(e.Age, e.Style) * opacity;
                if (alpha <= 0f) continue;
                Vector2? anchor = anchorOf(e.AnchorId);
                if (anchor is null) continue;

                float scale = FloatingTextCurves.ScaleAt(e.Age, e.Style);
                Vector2 centre = anchor.Value + e.Offset + FloatingTextCurves.OffsetAt(e.Age, e.Style, e.StackIndex);
                DrawCentred(batch, font, e.Text, centre, scale, alpha, e.Style);
            }
        }

        /// <summary>
        /// Draws every live banner of <paramref name="store"/>, each eased from its start point to its end point.
        /// Ages nothing, for the same reason the anchored overload does not.
        /// </summary>
        /// <param name="batch">The batch to draw through, already begun.</param>
        /// <param name="font">The font every banner is drawn in.</param>
        /// <param name="store">The live set.</param>
        /// <param name="opacity">A master multiplier over every banner's own alpha. At or below zero nothing is
        /// drawn at all.</param>
        public static void Draw(SpriteBatch batch, SpriteFont font, FloatingBannerStore store, float opacity = 1f)
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(font);
            ArgumentNullException.ThrowIfNull(store);
            if (opacity <= 0f) return;

            ReadOnlySpan<FloatingBanner> live = store.Live;
            for (int i = 0; i < live.Length; i++)
            {
                FloatingBanner b = live[i];
                float alpha = FloatingTextCurves.AlphaAt(b.Age, b.Style) * opacity;
                if (alpha <= 0f) continue;

                float scale = FloatingTextCurves.ScaleAt(b.Age, b.Style);
                Vector2 centre = FloatingBannerCurves.PositionAt(b.Age, b.Start, b.End, b.Style);
                DrawCentred(batch, font, b.Text, centre, scale, alpha, b.Style);
            }
        }

        // One placement rule for both stores: measure at the entry's own scale, centre on the point, and put the
        // shadow under it at the same scale so the pair holds its offset as the text zooms.
        static void DrawCentred(SpriteBatch batch, SpriteFont font, string text, Vector2 centre, float scale,
            float alpha, in FloatingTextStyle style)
        {
            Vector2 topLeft = centre - font.Measure(text) * scale * 0.5f;
            if (style.Shadow)
                batch.DrawString(font, text, topLeft + style.ShadowOffset * scale,
                    style.ShadowColor.WithAlpha(style.ShadowColor.A * alpha), scale);
            batch.DrawString(font, text, topLeft, style.Color.WithAlpha(style.Color.A * alpha), scale);
        }
    }
}
