using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Behavioural GPU coverage without a golden file: render a SlotGrid with an icon and a half cooldown offscreen,
    // read the pixels back, and assert tolerance-based regional properties robust across backends - the cooldown
    // wash darkens its covered half, and the icon is actually drawn. No GoldenCompare / committed grid (cross-backend
    // baking is out of scope). Skipped unless KE_GPU_TESTS is set (needs a real device).
    public sealed class SlotGridContentGpuTests
    {
        const int W = 260, H = 140;

        static float LumAt(byte[] rgba, int x, int y)
        {
            int i = (y * W + x) * 4;
            return (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3f;
        }

        // Average luminance over the box [x0,x1) x [y0,y1).
        static float BoxLum(byte[] rgba, int x0, int y0, int x1, int y1)
        {
            float sum = 0f; int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += LumAt(rgba, x, y); n++; }
            return sum / n;
        }

        [GpuFact]
        public void SlotGrid_half_cooldown_darkens_the_covered_half_and_the_icon_is_drawn()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.5f, 0f, 0.5f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                IconAtlas atlas = IconAtlas.Bake(ctx, cell: 64);

                var grid = new SlotGrid(new Rect(20, 20, 0, 0), count: 2, columns: 2)
                {
                    SlotSize = 100f,
                    Spacing = 8f,
                    IconInset = 10f,
                    IconAtlas = atlas,
                };
                grid.SetContent(0, new SlotContent(Icons.Heart, Vector4.One, cooldown: 0.5f));
                grid.SetContent(1, new SlotContent(Icons.Heart, Vector4.One, cooldown: 0f));

                ctx.Batch.Begin();
                grid.Draw(ctx.Batch, white);
                ctx.Batch.End();
            });

            // The wash darkens the covered half: slot 0's left region (dark at fraction 0.5) is darker than slot 1's
            // same region (identical icon + frame beneath, no sweep). Slot 1 sits +108 px to slot 0's right.
            float covered = BoxLum(rgba, 40, 62, 56, 78);      // slot 0, left half
            float uncovered = BoxLum(rgba, 148, 62, 164, 78);  // slot 1, same offset, no sweep
            Assert.True(covered < uncovered - 10f,
                $"the cooldown wash should darken the covered half (covered={covered:F1}, uncovered={uncovered:F1})");

            // The icon is actually rendered: slot 1's centre (over the heart, white-tinted) is far brighter than a
            // frame-only strip on slot 1's left edge (x < 138, outside the inset icon rect).
            float iconBox = BoxLum(rgba, 170, 62, 186, 78);    // slot 1 centre, over the heart body
            float frameBox = BoxLum(rgba, 130, 60, 136, 80);   // slot 1 left frame strip (bare frame)
            Assert.True(iconBox > frameBox + 25f,
                $"the slot icon should render brighter than the bare frame (icon={iconBox:F1}, frame={frameBox:F1})");
        }
    }
}
