using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for the optional text <c>scale</c> forwarded through the Gui text sinks (GuiSurface.Label /
    /// GuiSurface.Button via the shared <see cref="GuiDraw.AlignedTextPos"/>, and the retained <see cref="Label"/> /
    /// <see cref="TextLayout"/> path). Pure positioning math - no GPU. The scaled draw itself is verified on-device
    /// by DrawStringScaleGpuTests.
    /// </summary>
    public class GuiTextScaleTests
    {
        // A 200x40 rect at (100,50); a line measured 60px wide, 20px tall, line height 20px.
        static readonly Rect R = new(100, 50, 200, 40);
        static readonly Vector2 Measured = new(60, 20);
        const float LineHeight = 20f;

        // Fake measurer for TextLayout: every char 10px wide, line height 20px. No GPU.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        [Fact]
        public void AlignedTextPos_scale_one_reproduces_the_unscaled_centred_layout()
        {
            // The exact pre-scale formula the button/label used inline; scale=1 must stay byte-identical.
            var expected = new Vector2(
                R.X + (R.Width - Measured.X) * 0.5f,
                R.Y + (R.Height - LineHeight) * 0.5f);
            var pos = GuiDraw.AlignedTextPos(R, Measured, LineHeight, GuiAlign.Center, scale: 1f, pad: 0f);
            Assert.Equal(expected, pos);
        }

        [Fact]
        public void AlignedTextPos_scales_the_measured_width_and_line_height()
        {
            // scale 2: width 120 -> centred x = 100 + (200-120)/2 = 140; y centres on 40px scaled line -> 50.
            var pos = GuiDraw.AlignedTextPos(R, Measured, LineHeight, GuiAlign.Center, scale: 2f, pad: 0f);
            Assert.Equal(140f, pos.X, 3);
            Assert.Equal(50f, pos.Y, 3);
        }

        [Fact]
        public void AlignedTextPos_left_and_right_honour_pad_and_scaled_width()
        {
            var left = GuiDraw.AlignedTextPos(R, Measured, LineHeight, GuiAlign.Left, scale: 2f, pad: 6f);
            Assert.Equal(106f, left.X, 3);                       // rect.X + pad, independent of scale

            var right = GuiDraw.AlignedTextPos(R, Measured, LineHeight, GuiAlign.Right, scale: 2f, pad: 6f);
            Assert.Equal(300f - 120f - 6f, right.X, 3);          // rect.Right - width*scale - pad
        }

        [Fact]
        public void AlignedX_default_scale_matches_the_explicit_scale_one()
        {
            var font = new FixedFont();
            float a = TextLayout.AlignedX(font, "abc", left: 100, width: 200, TextAlign.Center);
            float b = TextLayout.AlignedX(font, "abc", left: 100, width: 200, TextAlign.Center, scale: 1f);
            Assert.Equal(a, b);
        }

        [Fact]
        public void AlignedX_center_scales_the_measured_width()
        {
            var font = new FixedFont();
            // "abc" = 30px * scale 2 = 60px -> centred in 200px region: 100 + (200-60)/2 = 170.
            Assert.Equal(170f, TextLayout.AlignedX(font, "abc", left: 100, width: 200, TextAlign.Center, scale: 2f), 3);
        }
    }
}
