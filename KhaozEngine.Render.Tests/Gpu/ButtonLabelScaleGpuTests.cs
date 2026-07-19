using System;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The FORWARD gets its own regression net: Button.Draw must forward LabelScale into the internal
    // GuiDraw.DrawButton, and dropping that forward (the pre-existing #232 bug) is invisible to any pure test.
    // Capture A is today's exact internal call (scale defaulted). Capture B is the retained Button at its default
    // LabelScale=1f - byte-identical proves the forward reproduces today's draw exactly. Capture C is the same
    // button at LabelScale=0.5f: it differs from B, and the label's lit-pixel extent roughly halves, proving a
    // non-1f scale actually reaches the draw. No golden image (self-relative only). Skipped unless KE_GPU_TESTS=1.
    public sealed class ButtonLabelScaleGpuTests
    {
        const int W = 320, H = 120;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static readonly Rect ButtonRect = new(40, 20, 240, 80);

        [GpuFact]
        public void LabelScale_default_forwards_todays_exact_call_and_a_non_default_scales_the_label()
        {
            byte[] a = Capture(viaButton: false, scale: 1f);
            byte[] b = Capture(viaButton: true, scale: 1f);
            byte[] c = Capture(viaButton: true, scale: 0.5f);

            Assert.Equal(a, b);
            Assert.False(b.AsSpan().SequenceEqual(c), "a non-1f LabelScale must change the drawn pixels");

            (int wB, int hB) = LitExtent(b);
            (int wC, int hC) = LitExtent(c);
            Assert.True(wB > 2 && hB > 2, $"scale-1 label should be visible (w={wB} h={hB})");
            Assert.InRange((double)wC / wB, 0.3, 0.7);
            Assert.InRange((double)hC / hB, 0.3, 0.7);
        }

        // viaButton=false renders the internal 10-arg GuiDraw.DrawButton call directly (scale defaulted to 1f) -
        // today's exact call, callable here via InternalsVisibleTo. viaButton=true renders a retained Button with
        // LabelScale set, drawn via Button.Draw.
        static byte[] Capture(bool viaButton, float scale) =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                if (viaButton)
                {
                    var button = new Button(ButtonRect, LocalizedText.Raw("Go"), font) { LabelScale = scale };
                    button.Draw(ctx.Batch, white);
                }
                else
                {
                    GuiDraw.DrawButton(ctx.Batch, white, font, ButtonRect, LocalizedText.Raw("Go"), GuiStyle.Default,
                        enabled: true, selected: false, hover: false, press: false);
                }

                ctx.Batch.End();
            });

        // Width/height of the near-white (label) pixels in the capture: high enough to exclude the button's
        // Fill/Border/Hover colours (all well under 180 on at least one channel for GuiStyle.Default), isolating
        // the label glyphs regardless of where the button chrome sits.
        static (int Width, int Height) LitExtent(byte[] rgba)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 180 && rgba[i + 1] > 180 && rgba[i + 2] > 180)
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
