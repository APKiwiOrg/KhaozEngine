using System;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The FORWARDS get their own regression net (#244): each of the four widgets has to carry its text scale into
    // the scaled DrawString AND into its vertical centring term, and a dropped argument is invisible to any pure
    // test. Per widget, three captures: the chrome alone (empty text), the chrome plus text at the default scale,
    // and the same at 0.5. Diffing against the empty capture isolates exactly the pixels the TEXT contributed,
    // whatever colour the widget paints it, so the assertions do not depend on a brightness threshold:
    //   - text at the default scale must change pixels at all (the draw happens),
    //   - halving the scale must roughly halve the text's footprint (the scale reaches the DrawString),
    //   - and the footprint must stay vertically centred where it was (the centring term took the scale too).
    // No golden image (self-relative only). Skipped unless KE_GPU_TESTS=1.
    public sealed class WidgetTextScaleForwardGpuTests
    {
        const int W = 320, H = 160;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static readonly Rect Widget = new(30, 60, 260, 40);

        [GpuFact]
        public void Dropdown_forwards_its_TextScale_to_the_trigger_label()
        {
            AssertScaleReachesTheDraw(
                (ctx, font, white, label, scale) =>
                {
                    var dd = new Dropdown(new[] { new DropdownOption(label, 0) }, Widget);
                    if (scale is { } s) dd.TextScale = s;
                    dd.Draw(ctx.Batch, white, font);
                });
        }

        [GpuFact]
        public void Dropdown_forwards_its_TextScale_to_the_open_option_rows()
        {
            AssertScaleReachesTheDraw(
                (ctx, font, white, label, scale) =>
                {
                    var dd = new Dropdown(new[] { new DropdownOption(label, 0) }, new Rect(30, 20, 260, 40));
                    if (scale is { } s) dd.TextScale = s;
                    dd.Open();
                    dd.DrawOverlay(ctx.Batch, white, font, new Pointer());
                });
        }

        [GpuFact]
        public void TextInput_forwards_its_TextScale_to_the_field_text()
        {
            AssertScaleReachesTheDraw(
                (ctx, font, white, label, scale) =>
                {
                    var field = new TextInput(Widget, font) { Text = label };
                    if (scale is { } s) field.TextScale = s;
                    field.Draw(ctx.Batch, white);
                });
        }

        [GpuFact]
        public void TreeView_forwards_its_TextScale_to_the_node_labels()
        {
            AssertScaleReachesTheDraw(
                (ctx, font, white, label, scale) =>
                {
                    var tree = new TreeView(Widget) { RowHeight = Widget.Height };
                    tree.Roots.Add(new TreeNode(LocalizedText.Raw(label)));
                    if (scale is { } s) tree.TextScale = s;
                    tree.Draw(ctx.Batch, white, font);
                });
        }

        [GpuFact]
        public void ProgressBar_forwards_its_OverlayTextScale_to_the_overlay_text()
        {
            AssertScaleReachesTheDraw(
                (ctx, font, white, label, scale) =>
                {
                    var bar = new ProgressBar(Widget, 0.5f) { OverlayText = LocalizedText.Raw(label) };
                    if (scale is { } s) bar.OverlayTextScale = s;
                    bar.Draw(ctx.Batch, white, font);
                });
        }

        // `scale` null leaves the widget's own default in place, so the "text draws" capture also exercises the
        // untouched field rather than an explicitly assigned 1f.
        delegate void DrawWidget(Render2DContext ctx, SpriteFont font, Texture2D white, string label, float? scale);

        static void AssertScaleReachesTheDraw(DrawWidget draw)
        {
            const string Label = "Mmmmmmmm";

            byte[] chrome = Capture(draw, "", null);
            byte[] full = Capture(draw, Label, null);
            byte[] half = Capture(draw, Label, 0.5f);

            Assert.False(full.AsSpan().SequenceEqual(chrome), "the label must draw at the default scale");
            Assert.False(half.AsSpan().SequenceEqual(full), "a non-default text scale must change the drawn pixels");

            (int wFull, int hFull, float cyFull) = TextExtent(full, chrome);
            (int wHalf, int hHalf, float cyHalf) = TextExtent(half, chrome);

            Assert.True(wFull > 8 && hFull > 4, $"the default-scale label should be visible (w={wFull} h={hFull})");
            Assert.InRange((double)wHalf / wFull, 0.3, 0.7);
            Assert.InRange((double)hHalf / hFull, 0.3, 0.7);

            // The centring term took the scale too: a smaller label stays on the widget's centre line rather than
            // hanging from where the taller line's top used to be.
            Assert.True(MathF.Abs(cyHalf - cyFull) <= 2f,
                $"the scaled label should stay vertically centred (centre {cyFull} -> {cyHalf})");
        }

        static byte[] Capture(DrawWidget draw, string label, float? scale) =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                draw(ctx, font, white, label, scale);
                ctx.Batch.End();
            });

        // Bounding box (and vertical centre) of the pixels that differ between a capture WITH text and the same
        // scene with an empty label: the text's own footprint, independent of the widget's palette.
        static (int Width, int Height, float CenterY) TextExtent(byte[] withText, byte[] chrome)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (withText[i] == chrome[i] && withText[i + 1] == chrome[i + 1] &&
                        withText[i + 2] == chrome[i + 2] && withText[i + 3] == chrome[i + 3]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < minX) return (0, 0, 0f);
            return (maxX - minX + 1, maxY - minY + 1, (minY + maxY) * 0.5f);
        }
    }
}
