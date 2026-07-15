using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    // Pins the DPI-aware UI text fix: a point-space text block snaps its ORIGIN (the ascent baseline) to the device
    // grid ONCE, so every glyph of a word shares one baseline. The old path snapped each glyph's top independently,
    // and glyphs with different vertical bearings (YOff) then rounded to different device rows whenever the effective
    // scale was fractional (a DpiFont drawn at a Theme scale below 1) - the per-glyph "height wave" seen in the F1
    // diagnostics HUD on a Retina display. Headless: it exercises SpriteBatch.SnapTextOrigin (the pure static) plus
    // the trivial per-glyph placement formula, with the exact metrics measured from the real baked font.
    public class DrawStringBaselineSnapTests
    {
        // Measured for the engine default face at DpiFont(32).For(~2.0), drawn at Theme.Scale 0.5, for "Performance".
        const float DeviceScale = 1.9988765f;   // device px per point (UiViewport DpiScale)
        const float K = 0.25014052f;            // font.RenderScale * scale (atlas texels -> points, then Theme scale)
        const float BaselinePoint = 42.266666f; // position.Y + font.Ascent * scale, a fractional point coordinate
        // Vertical bearings of the glyphs in "Performance" (atlas texels above the baseline), P and f taller than x-height.
        static readonly int[] YOffs = { -39, -30, -30, -42, -30, -30, -30, -30, -30, -30 };

        // The reconstructed device-px baseline of one glyph given its snapped top: top - YOff*k (in device px). Every
        // glyph of a coherent word must reconstruct the SAME baseline. The spread across glyphs is the wave amplitude.
        static float BaselineSpread(System.Func<int, float> glyphTopPoint)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (int yOff in YOffs)
            {
                float baseline = (glyphTopPoint(yOff) - yOff * K) * DeviceScale;   // reconstructed baseline, device px
                if (baseline < min) min = baseline;
                if (baseline > max) max = baseline;
            }
            return max - min;
        }

        [Fact]
        public void Single_origin_snap_keeps_every_glyph_on_one_baseline()
        {
            var scale = new Vector2(DeviceScale, DeviceScale);
            (float _, float snappedBaseline) =
                SpriteBatch.SnapTextOrigin(0f, BaselinePoint, scale, Vector2.Zero);

            // Fixed path: snap once, then each glyph top is snappedBaseline + YOff*k (no per-glyph rounding).
            float spread = BaselineSpread(yOff => snappedBaseline + yOff * K);

            Assert.True(spread < 1e-3f, $"glyphs of one word must share a baseline, spread was {spread} device px");
        }

        [Fact]
        public void Old_per_glyph_snap_would_wave_the_baseline()
        {
            // Documents the bug the fix removes: snapping each glyph's top independently splits the baseline by up to
            // a device pixel, which is the visible per-glyph height wave. This is the OLD DrawString computation.
            float spread = BaselineSpread(yOff =>
                ViewportMath.SnapToDevicePixel(BaselinePoint + yOff * K, DeviceScale, 0f));

            Assert.True(spread > 0.4f, $"the old per-glyph snap should wave (measured spread {spread} device px)");
        }

        [Fact]
        public void Snap_is_a_noop_when_disarmed()
        {
            // Outside a point-space UiViewport, _deviceScale is zero and snapping must pass the origin through untouched
            // (world / screen-space text is unaffected - this is why every golden stays byte-identical).
            (float penX, float baseline) =
                SpriteBatch.SnapTextOrigin(3.7f, 42.9f, Vector2.Zero, Vector2.Zero);

            Assert.Equal(3.7f, penX, 6);
            Assert.Equal(42.9f, baseline, 6);
        }

        [Fact]
        public void Snapped_baseline_lands_on_a_whole_device_pixel()
        {
            var scale = new Vector2(DeviceScale, DeviceScale);
            (float _, float baseline) = SpriteBatch.SnapTextOrigin(0f, BaselinePoint, scale, Vector2.Zero);

            float deviceRow = baseline * DeviceScale;
            Assert.Equal(MathF.Round(deviceRow), deviceRow, 4);   // the block baseline sits on an integer device row
        }
    }
}
