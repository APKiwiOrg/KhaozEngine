using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class TooltipTests
    {
        // 10px/char, 20px line height.
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly TooltipMetrics M = TooltipMetrics.Default;

        static TooltipLine[] One(string s) => new[] { new TooltipLine(s, Vector4.One) };

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Drive a fresh press-then-release gesture ending at `at`, so the pointer reports a tap released there.
        static Pointer ReleaseAt(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(new Vector2(-1, -1), false));
            p.Update(Frame(at, true));
            p.Update(Frame(at, false));
            return p;
        }

        [Fact]
        public void Auto_sizes_to_content_and_sits_above_the_anchor()
        {
            // title "Hello"(50) / line "World"(50): contentW=50 -> panelW=70.
            // contentH = (20+5) + (20+3) - 3 = 45 -> panelH = 45+16 = 61.
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(300, 200), View, M);
            Assert.Equal(70f, r.Width);
            Assert.Equal(61f, r.Height);
            Assert.Equal(300f - 35f, r.X);          // centered on anchor X
            Assert.Equal(200f - 61f - 10f, r.Y);    // above anchor (anchorOffset 10)
        }

        [Fact]
        public void Flips_below_the_anchor_when_there_is_no_room_above()
        {
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(300, 30), View, M);
            Assert.Equal(30f + 10f, r.Y);           // flipped: anchor.Y + anchorOffset
        }

        [Fact]
        public void Clamps_horizontally_into_the_viewport()
        {
            var r = Tooltip.ComputeBounds(Font, "Hello", Font, One("World"), new Vector2(10, 200), View, M);
            Assert.Equal(4f, r.X);                  // clamped to left margin
        }

        [Fact]
        public void Width_follows_the_widest_line()
        {
            // longest body line 8 chars = 80 -> contentW 80 -> panelW 100
            var lines = new[] { new TooltipLine("ab", Vector4.One), new TooltipLine("longline", Vector4.One) };
            var r = Tooltip.ComputeBounds(Font, "", Font, lines, new Vector2(400, 300), View, M);
            Assert.Equal(100f, r.Width);
        }

        [Fact]
        public void Two_column_title_row_widens_to_fit_the_right_value()
        {
            // title "AB"(20) + gap(12) + right "99"(20) = 52 title row; body "x"(10) -> contentW 52 -> panelW 72.
            var lines = One("x");
            var r = Tooltip.ComputeBounds(Font, "AB", "99", Font, Font, lines, new Vector2(300, 200), View, M);
            Assert.Equal(72f, r.Width);

            // Without the right value the title row is just the title, so it is narrower.
            var bare = Tooltip.ComputeBounds(Font, "AB", "", Font, Font, lines, new Vector2(300, 200), View, M);
            Assert.True(r.Width > bare.Width);
        }

        [Fact]
        public void Two_column_overload_matches_the_plain_overload_when_the_right_value_is_empty()
        {
            var lines = One("World");
            var plain = Tooltip.ComputeBounds(Font, "Hello", Font, lines, new Vector2(300, 200), View, M);
            var twoCol = Tooltip.ComputeBounds(Font, "Hello", "", Font, Font, lines, new Vector2(300, 200), View, M);
            Assert.Equal(plain.Width, twoCol.Width);
            Assert.Equal(plain.Height, twoCol.Height);
        }

        [Fact]
        public void TapOutside_mode_dismisses_on_a_fresh_release_outside_the_bounds()
        {
            var bounds = new Rect(100, 100, 80, 40);
            Assert.True(Tooltip.ShouldDismiss(TooltipDismiss.TapOutside, showedThisFrame: false, ReleaseAt(new Vector2(10, 10)), bounds));
        }

        [Fact]
        public void TapOutside_mode_keeps_the_tooltip_on_the_frame_it_was_shown()
        {
            // The release that opened the tooltip (a tap on the trigger, outside the bubble) must not close it.
            var bounds = new Rect(100, 100, 80, 40);
            Assert.False(Tooltip.ShouldDismiss(TooltipDismiss.TapOutside, showedThisFrame: true, ReleaseAt(new Vector2(10, 10)), bounds));
        }

        [Fact]
        public void TapOutside_mode_keeps_the_tooltip_on_a_release_inside_the_bounds()
        {
            var bounds = new Rect(100, 100, 80, 40);
            Assert.False(Tooltip.ShouldDismiss(TooltipDismiss.TapOutside, showedThisFrame: false, ReleaseAt(new Vector2(120, 120)), bounds));
        }

        [Fact]
        public void CallerDriven_mode_never_auto_dismisses()
        {
            var bounds = new Rect(100, 100, 80, 40);
            Assert.False(Tooltip.ShouldDismiss(TooltipDismiss.CallerDriven, showedThisFrame: false, ReleaseAt(new Vector2(10, 10)), bounds));
        }
    }
}
