using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // TabBar draws its labels inline with no internal whole-draw helper to byte-compare against (unlike Button), so
    // this pins the forward relative: two chrome-identical bars in one capture, offset vertically by a whole
    // number of pixels, one at TextScale=1f and one at 0.5f. The label lit extent roughly halves, and the
    // frame/border chrome - every row of the bar band outside a generous margin around the label's vertical band -
    // stays byte-identical between the two regions, proving TextScale touches the label only. No golden image
    // (self-relative only). Skipped unless KE_GPU_TESTS=1.
    public sealed class TabBarTextScaleGpuTests
    {
        const int W = 400, H = 260;
        const int Dy = 120;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static readonly Rect Bar1 = new(20, 20, 320, 60);
        static readonly Rect Bar2 = new(20, 20 + Dy, 320, 60);

        static readonly IReadOnlyList<LocalizedText> Labels = new[]
        {
            LocalizedText.Raw("Goals"), LocalizedText.Raw("Tree"), LocalizedText.Raw("More"),
        };

        [GpuFact]
        public void TextScale_halves_the_label_extent_and_leaves_the_chrome_byte_identical()
        {
            float lineHeight = 0f;
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                lineHeight = font.LineHeight;
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                var bar1 = new TabBar(Labels, font, Bar1) { TextScale = 1f };
                var bar2 = new TabBar(Labels, font, Bar2) { TextScale = 0.5f };
                bar1.Draw(ctx.Batch, white);
                bar2.Draw(ctx.Batch, white);

                ctx.Batch.End();
            });

            // Restrict to tab 0's column only: the combined span across all 3 tabs' labels is dominated by the
            // fixed tab-to-tab spacing, not by any one label's width, so it would barely move with TextScale.
            int tab0Left = (int)Bar1.X;
            int tab0Right = (int)(Bar1.X + Bar1.Width / Labels.Count);
            (int w1, int h1) = LitExtent(rgba, tab0Left, tab0Right, (int)Bar1.Y, (int)Bar1.Bottom);
            (int w2, int h2) = LitExtent(rgba, tab0Left, tab0Right, (int)Bar2.Y, (int)Bar2.Bottom);
            Assert.True(w1 > 2 && h1 > 2, $"scale-1 label should be visible (w={w1} h={h1})");
            Assert.InRange((double)w2 / w1, 0.3, 0.7);
            Assert.InRange((double)h2 / h1, 0.3, 0.7);

            // Chrome band: the label is vertically centred at the same relative row in both bars regardless of
            // scale (AlignedTextPos centres about rect.Height), so exclude a band around that centre wide enough
            // to cover the TALLER (TextScale=1f) label plus anti-aliasing bleed - a strict superset of the
            // TextScale=0.5f band - then assert every other row in the bar band is byte-identical between the two
            // regions (Bar2 is Bar1 shifted down by exactly Dy whole pixels, so row y in Bar1 <-> row y+Dy in Bar2).
            float centerY = Bar1.Y + Bar1.Height * 0.5f;
            float half = lineHeight * 0.5f + 3f;
            int bandTop = (int)MathF.Floor(centerY - half);
            int bandBottom = (int)MathF.Ceiling(centerY + half);

            var chromeA = new List<byte>();
            var chromeB = new List<byte>();
            for (int y = (int)Bar1.Y; y < (int)Bar1.Bottom; y++)
            {
                if (y >= bandTop && y <= bandBottom) continue;
                int rowA = y * W * 4;
                int rowB = (y + Dy) * W * 4;
                for (int x = 0; x < W * 4; x++)
                {
                    chromeA.Add(rgba[rowA + x]);
                    chromeB.Add(rgba[rowB + x]);
                }
            }
            Assert.Equal(chromeA, chromeB);
        }

        // Width/height of the label-ink pixels within [xFrom, xTo) x [yFrom, yTo), isolated via the red channel:
        // with the TabBar defaults (ActiveStyle.Text ~ (140,200,255), InactiveStyle.Text ~ (160,160,170) from
        // GuiTheme.Default), every non-text pixel this bar draws tops out at R=100 (the active tab's accent
        // border, its brightest chrome element) while both label colours sit at R>=140, so R>120 cleanly
        // separates ink from chrome without depending on hue.
        static (int Width, int Height) LitExtent(byte[] rgba, int xFrom, int xTo, int yFrom, int yTo)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = yFrom; y < yTo; y++)
            {
                for (int x = xFrom; x < xTo; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 120)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < minX) return (0, 0);
            return (maxX - minX + 1, maxY - minY + 1);
        }
    }
}
