using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for <see cref="TooltipAnchorMode"/> (#246). <see cref="Tooltip.ComputeBounds(ITextMeasurer, string, string, ITextMeasurer, ITextMeasurer, System.Collections.Generic.IReadOnlyList{TooltipLine}, Vector2, Vector2, TooltipMetrics, float, float, TooltipAnchorMode)"/>
    /// is pure layout, so the whole knob is device-free: the default reproduces the pre-existing centred placement
    /// exactly, and the offset mode puts the bubble's LEFT edge beside the anchor (the cursor-style placement a
    /// consumer needed and could not express). The vertical rule and the viewport clamp are shared by both modes.
    /// </summary>
    public class TooltipAnchorModeTests
    {
        // 10px/char, 20px line height. Same metrics as TooltipTests.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly Vector2 Anchor = new(400, 300);
        static TooltipLine[] Body => new[] { new TooltipLine("abcdefgh", Vector4.One) };

        static Rect Bounds(TooltipAnchorMode mode, float offsetX)
        {
            TooltipMetrics m = TooltipMetrics.Default;
            m.AnchorOffsetX = offsetX;
            return Tooltip.ComputeBounds(Font, "Title", "", Font, Font, Body, Anchor, View, m,
                float.PositiveInfinity, 1f, mode);
        }

        [Fact]
        public void AnchorMode_defaults_to_centered_on_the_instance()
        {
            var tip = new Tooltip(null!, null!);
            Assert.Equal(TooltipAnchorMode.Centered, tip.AnchorMode);
            Assert.Equal(0f, tip.Metrics.AnchorOffsetX);
        }

        [Fact]
        public void The_default_overloads_reproduce_the_centred_placement()
        {
            // The pre-existing formula, verbatim: x = anchor.X - width / 2, whatever the width works out to.
            Rect legacy = Tooltip.ComputeBounds(Font, "Title", Font, Body, Anchor, View, TooltipMetrics.Default);
            Assert.Equal(Anchor.X - legacy.Width * 0.5f, legacy.X, 3);

            // And the new parameter defaults to exactly that, so no existing caller moves.
            Rect defaulted = Bounds(TooltipAnchorMode.Centered, offsetX: 0f);
            Assert.Equal(legacy, defaulted);
        }

        [Fact]
        public void Centered_ignores_the_horizontal_offset()
        {
            Assert.Equal(Bounds(TooltipAnchorMode.Centered, 0f), Bounds(TooltipAnchorMode.Centered, 64f));
        }

        [Fact]
        public void Offset_puts_the_left_edge_at_the_anchor_plus_the_offset()
        {
            Rect r = Bounds(TooltipAnchorMode.Offset, offsetX: 14f);
            Assert.Equal(Anchor.X + 14f, r.X, 3);

            // A negative offset places the bubble to the LEFT of the anchor instead.
            Rect left = Bounds(TooltipAnchorMode.Offset, offsetX: -14f);
            Assert.Equal(Anchor.X - 14f, left.X, 3);

            // Zero offset is still not the centred placement: the left edge lands on the anchor.
            Assert.Equal(Anchor.X, Bounds(TooltipAnchorMode.Offset, 0f).X, 3);
        }

        [Fact]
        public void Offset_keeps_the_vertical_rule_and_the_viewport_clamp()
        {
            Rect centered = Bounds(TooltipAnchorMode.Centered, 0f);
            Rect offset = Bounds(TooltipAnchorMode.Offset, 14f);

            // Same size, same y: only the horizontal placement differs between the two modes.
            Assert.Equal(centered.Width, offset.Width, 3);
            Assert.Equal(centered.Height, offset.Height, 3);
            Assert.Equal(centered.Y, offset.Y, 3);

            // A far-right anchor with a positive offset would run the bubble off-screen, and the clamp still applies.
            TooltipMetrics m = TooltipMetrics.Default;
            m.AnchorOffsetX = 40f;
            Rect clamped = Tooltip.ComputeBounds(Font, "Title", "", Font, Font, Body,
                new Vector2(View.X - 4f, 300f), View, m, float.PositiveInfinity, 1f, TooltipAnchorMode.Offset);
            Assert.Equal(View.X - clamped.Width - m.Margin, clamped.X, 3);
        }
    }
}
