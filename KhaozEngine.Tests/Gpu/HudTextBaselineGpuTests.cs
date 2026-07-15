using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// End-to-end guard for the F1-HUD "per-glyph height wave" fix, on the real baked font. Renders a word through the
/// live <see cref="SpriteBatch.DrawString(SpriteFont, string, Vector2, KhaozEngine.Primitives.Color, float)"/> path
/// in a point-space <see cref="UiViewport"/> (snapping armed) exactly as the diagnostics HUD does - a
/// <see cref="DpiFont"/>(32) drawn at a Theme scale below 1 on a Retina-like framebuffer - and asserts every glyph
/// of the word shares one device-pixel baseline. Before the fix DrawString snapped each glyph's top independently,
/// so glyphs with different vertical bearings split onto different device rows (the wave). Gated by
/// <see cref="GpuFactAttribute"/> (needs a font atlas, so a real device).
/// </summary>
public class HudTextBaselineGpuTests
{
    [GpuFact]
    public void Word_glyphs_share_one_baseline_under_ui_snapping()
    {
        // The user's playtest conditions: framebuffer 1779x1156, Retina => DpiScale ~= 2, HUD text at Theme.Scale 0.5.
        const int FbW = 1779, FbH = 1156, LogW = 890, LogH = 578;
        const float scale = 0.5f;
        const string word = "Performance";

        Render2DSnapshot.Capture(64, 64, new KhaozEngine.Primitives.Color(0, 0, 0, 1), ctx =>
        {
            using DpiFont dpi = ctx.LoadDefaultDpiFont(32f);
            var ui = new UiViewport(FbW, FbH, LogW, LogH);
            float d = ui.DpiScale;
            SpriteFont font = dpi.For(d);
            float k = font.RenderScale * scale;

            ctx.Batch.Begin(ui);   // arm point-space device-pixel snapping, exactly as GameApp's HUD pass does
            var dests = ctx.Batch.DebugGlyphDests(font, word, new Vector2(20f, 33f), scale);
            ctx.Batch.End();

            // Reconstruct each glyph's baseline from its emitted top (top - YOff*k): a coherent word => one baseline.
            float min = float.MaxValue, max = float.MinValue;
            int gi = 0;
            for (int i = 0; i < word.Length; i++)
            {
                GlyphInfo? g = SpriteFont.ResolveGlyph(font.Glyphs, word.AsSpan(), ref i);
                if (g == null || g.W <= 0 || g.H <= 0) continue;
                float baselineDev = (dests[gi++].Y - g.YOff * k) * d;
                min = MathF.Min(min, baselineDev);
                max = MathF.Max(max, baselineDev);
            }
            float spreadDevicePx = max - min;
            Assert.True(spreadDevicePx < 1e-2f,
                $"glyphs of one word must share a baseline, spread was {spreadDevicePx} device px (the wave)");
        });
    }
}
