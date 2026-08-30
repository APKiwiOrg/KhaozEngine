using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The COMPLETENESS of Tooltip.Opacity (#245) gets its own regression net: the bubble paints six separate
    // colours (background, border, title, right-hand title value, separator rule, and each body line), and a
    // pure test cannot see one of them going out unfaded. Under the standard src-alpha blend a colour at alpha
    // 0 leaves the destination untouched, so a fully transparent tooltip must be byte-identical to drawing no
    // tooltip at all - which fails the moment ONE colour skips the fade. The default (1f) capture is pinned
    // against the same scene with the field never touched, so today's tooltips stay byte-exact. No golden image
    // (self-relative only). Skipped unless KE_GPU_TESTS=1.
    public sealed class TooltipOpacityGpuTests
    {
        const int W = 320, H = 200;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void Opacity_zero_paints_nothing_and_the_default_matches_an_untouched_tooltip()
        {
            byte[] hidden = Capture(visible: false, opacity: 1f);
            byte[] transparent = Capture(visible: true, opacity: 0f);
            byte[] defaulted = Capture(visible: true, opacity: null);
            byte[] opaque = Capture(visible: true, opacity: 1f);
            byte[] half = Capture(visible: true, opacity: 0.5f);

            // Every colour is faded: nothing survives at alpha 0. A missed colour leaves pixels here.
            Assert.Equal(hidden, transparent);
            // The default is a no-op: setting 1f explicitly and never touching the field render the same.
            Assert.Equal(defaulted, opaque);
            // The bubble really does draw, and a partial fade lands strictly between the two ends.
            Assert.False(opaque.AsSpan().SequenceEqual(hidden), "the tooltip must draw something at full opacity");
            Assert.False(half.AsSpan().SequenceEqual(opaque), "a mid opacity must differ from the opaque draw");
            Assert.False(half.AsSpan().SequenceEqual(hidden), "a mid opacity must still draw something");
        }

        // One tooltip exercising every painted colour: a two-column title, the separator rule, and two body
        // lines. `opacity` null leaves the field at its constructed default.
        static byte[] Capture(bool visible, float? opacity) =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 16f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                var tip = new Tooltip(font, font) { Viewport = new Vector2(W, H), ShowTitleSeparator = true };
                if (opacity is { } o) tip.Opacity = o;
                if (visible)
                    tip.Show(LocalizedText.Raw("Copper Ore"), LocalizedText.Raw("x128"), new[]
                    {
                        new TooltipLine("Common material", new Vector4(0.7f, 0.8f, 1f, 1f)),
                        new TooltipLine("Sells for 3g", new Vector4(1f, 0.9f, 0.4f, 1f)),
                    }, new Vector2(W * 0.5f, H * 0.75f));
                tip.Draw(ctx.Batch, white);

                ctx.Batch.End();
            });
    }
}
