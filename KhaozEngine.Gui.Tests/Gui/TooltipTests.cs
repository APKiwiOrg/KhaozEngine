using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
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

        // The same 10px/char metrics, tallying how often each exact string is measured. Wrapping a body line
        // measures that line's own full text once, as the does-it-fit probe, before any candidate slice is
        // measured, so the tally for a source line IS the number of word-wrap passes over it.
        sealed class CountingFont : ITextMeasurer
        {
            readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

            public float LineHeight => 20f;

            public Vector2 Measure(string text)
            {
                _counts[text] = _counts.TryGetValue(text, out int n) ? n + 1 : 1;
                return new(text.Length * 10f, 20f);
            }

            public int WrapsOf(string sourceLine) => _counts.TryGetValue(sourceLine, out int n) ? n : 0;
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly TooltipMetrics M = TooltipMetrics.Default;

        static TooltipLine[] One(string s) => new[] { new TooltipLine(s, Vector4.One) };

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        // Drive a fresh press-then-release gesture ending at `at`, so the pointer reports a tap released there.
        Pointer ReleaseAt(Vector2 at)
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
        public void Long_line_wraps_capping_width_and_growing_height()
        {
            // maxWidth 100 -> content width 80 (minus PadX*2) = 8 chars/line. "one two three four" wraps to
            // ["one two"(70), "three"(50), "four"(40)] = 3 lines.
            var r = Tooltip.ComputeBounds(Font, "", Font, One("one two three four"), new Vector2(400, 300), View, M, maxWidth: 100f);
            // widest wrapped line "one two" = 70 -> panelW 90, capped under the 100 max.
            Assert.Equal(90f, r.Width);
            Assert.True(r.Width <= 100f);
            // 3 lines: 3*(20+3) - 3 = 66 -> panelH 82.
            Assert.Equal(82f, r.Height);

            // Unbounded: one 180px line -> panelW 200, single-line panelH 36. Wrapping narrows and heightens.
            var unbounded = Tooltip.ComputeBounds(Font, "", Font, One("one two three four"), new Vector2(400, 300), View, M);
            Assert.True(r.Width < unbounded.Width);
            Assert.True(r.Height > unbounded.Height);
        }

        [Fact]
        public void Short_line_does_not_wrap_under_a_generous_max_width()
        {
            // "hi" (20) fits well within the 80px content budget, so bounds match the unbounded layout exactly.
            var bounded = Tooltip.ComputeBounds(Font, "", Font, One("hi"), new Vector2(400, 300), View, M, maxWidth: 100f);
            var unbounded = Tooltip.ComputeBounds(Font, "", Font, One("hi"), new Vector2(400, 300), View, M);
            Assert.Equal(unbounded.Width, bounded.Width);
            Assert.Equal(unbounded.Height, bounded.Height);
        }

        [Fact]
        public void Unbreakable_token_hard_breaks_within_the_max_width()
        {
            // A single 18-char token with no spaces, content budget 80 (8 chars) -> three chunks (8,8,2) = 3 lines.
            var r = Tooltip.ComputeBounds(Font, "", Font, One("aaaaaaaaaaaaaaaaaa"), new Vector2(400, 300), View, M, maxWidth: 100f);
            Assert.True(r.Width <= 100f);
            Assert.Equal(100f, r.Width);            // widest chunk 80 -> panelW 100
            Assert.Equal(82f, r.Height);            // 3 lines -> panelH 82 (single-line would be 36)
        }

        [Fact]
        public void Two_column_title_row_is_never_squeezed_below_its_width_by_the_cap()
        {
            // Two-column title "LongTitleName"(130) + gap(12) + "0/3"(30) = 172px title row, wider than the
            // 100px cap. The title row is single-line and un-wrappable, so the bubble must stay wide enough to
            // hold it (else the left name overlaps the right-aligned value). Body line is short.
            var r = Tooltip.ComputeBounds(Font, "LongTitleName", "0/3", Font, Font, One("x"), new Vector2(400, 300), View, M, maxWidth: 100f);
            Assert.Equal(192f, r.Width);            // 172 title row + PadX*2(20); NOT clamped to the 100 cap
            Assert.True(r.Width > 100f);
        }

        [Fact]
        public void Single_column_title_still_clips_at_the_cap()
        {
            // With no right value the title has nothing to overlap, so a title longer than the cap still clips
            // at maxWidth (the width floor only applies to the two-column title row).
            var r = Tooltip.ComputeBounds(Font, "LongTitleName", "", Font, Font, One("x"), new Vector2(400, 300), View, M, maxWidth: 100f);
            Assert.Equal(100f, r.Width);
        }

        [Fact]
        public void Flip_below_still_triggers_with_the_taller_wrapped_bubble()
        {
            // The wrapped bubble is 82px tall; above the anchor at y=50 it would start at 50-82-10 = -42 < 0,
            // so it flips below to anchor.Y + AnchorOffsetY.
            var wrapped = Tooltip.ComputeBounds(Font, "", Font, One("one two three four"), new Vector2(400, 50), View, M, maxWidth: 100f);
            Assert.Equal(50f + 10f, wrapped.Y);

            // The unwrapped single-line bubble (36px) fits above at 50-36-10 = 4, so it does NOT flip: the taller
            // wrapped size is what forces the flip.
            var unbounded = Tooltip.ComputeBounds(Font, "", Font, One("one two three four"), new Vector2(400, 50), View, M);
            Assert.Equal(4f, unbounded.Y);
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

        // --- TooltipLine.Scale / Tooltip.TitleScale: additive, defaulting to 1f. ---

        [Fact]
        public void Default_scale_of_one_is_inert_on_both_TooltipLine_and_TitleScale()
        {
            // The pre-existing 2-arg construction still compiles and defaults Scale to 1f. An explicit 1f is the
            // same value, and threading an explicit titleScale: 1f through the fullest overload reproduces the
            // plain overload's bounds exactly (the known 70x61 baseline from Auto_sizes_to_content_and_sits_above_the_anchor).
            var twoArg = new TooltipLine("World", Vector4.One);
            var explicitOne = new TooltipLine("World", Vector4.One, 1f);
            Assert.Equal(1f, twoArg.Scale);
            Assert.Equal(twoArg, explicitOne);

            var plain = Tooltip.ComputeBounds(Font, "Hello", Font, new[] { twoArg }, new Vector2(300, 200), View, M);
            var fullest = Tooltip.ComputeBounds(Font, "Hello", "", Font, Font, new[] { explicitOne },
                new Vector2(300, 200), View, M, float.PositiveInfinity, titleScale: 1f);
            Assert.Equal(plain, fullest);
            Assert.Equal(70f, plain.Width);
            Assert.Equal(61f, plain.Height);
        }

        [Fact]
        public void Body_line_scale_halves_its_width_and_height_contribution()
        {
            // "longline" (8 chars) measures 80 wide, 20 tall at scale 1. At scale 0.5 it contributes 40 wide, 10
            // tall. No title, one line: contentH = LineHeight*scale (the trailing LineSpacing is subtracted back
            // off since there is no next line), panelH = contentH + PadY*2(16).
            var plain = Tooltip.ComputeBounds(Font, "", Font, One("longline"), new Vector2(400, 300), View, M);
            var half = Tooltip.ComputeBounds(Font, "", Font,
                new[] { new TooltipLine("longline", Vector4.One, 0.5f) }, new Vector2(400, 300), View, M);

            Assert.Equal(100f, plain.Width);    // 80 + PadX*2
            Assert.Equal(36f, plain.Height);    // 20 + PadY*2
            Assert.Equal(60f, half.Width);      // 40 + PadX*2
            Assert.Equal(26f, half.Height);     // 10 + PadY*2
        }

        [Fact]
        public void TitleScale_scales_the_title_row_width_and_height()
        {
            // Title "Hello" (50 wide, 20 tall) with a short body line "x" (10 wide) that never dominates the
            // width, so the title row alone drives contentW. titleScale 2 doubles both the title's measured
            // width and its LineHeight term in contentH. The body line's own (unscaled here) 20+3-3=20 term and
            // TitleGap(5)/PadX/PadY are unaffected.
            var lines = One("x");
            var scaled = Tooltip.ComputeBounds(Font, "Hello", "", Font, Font, lines, new Vector2(300, 200), View, M,
                float.PositiveInfinity, titleScale: 2f);
            var plain = Tooltip.ComputeBounds(Font, "Hello", "", Font, Font, lines, new Vector2(300, 200), View, M,
                float.PositiveInfinity, titleScale: 1f);

            Assert.Equal(120f, scaled.Width);   // title 50*2=100 -> +PadX*2(20)
            Assert.Equal(70f, plain.Width);     // title 50*1=50 -> +PadX*2(20)
            Assert.Equal(81f, scaled.Height);   // (20*2+5) title + 20 body + PadY*2(16)
            Assert.Equal(61f, plain.Height);    // (20*1+5) title + 20 body + PadY*2(16), matches the known baseline
        }

        [Fact]
        public void Line_scale_shifts_the_wrap_budget_so_a_higher_scale_wraps_sooner()
        {
            // maxWidth 100 -> content budget 80. "one two" measures 70 unscaled: fits the budget at scale 1 (stays
            // one line), but at scale 2 it occupies 140 design px, so it must wrap at the font-space budget 80/2=40
            // - "one"(30) then "two"(30), each still at scale 2.
            var unscaled = new[] { new TooltipLine("one two", Vector4.One, 1f) };
            var doubled = new[] { new TooltipLine("one two", Vector4.One, 2f) };

            var rNormal = Tooltip.ComputeBounds(Font, "", Font, unscaled, new Vector2(400, 300), View, M, maxWidth: 100f);
            var rScaled = Tooltip.ComputeBounds(Font, "", Font, doubled, new Vector2(400, 300), View, M, maxWidth: 100f);

            Assert.Equal(90f, rNormal.Width);    // 70 + PadX*2, single line
            Assert.Equal(36f, rNormal.Height);   // 20*1 + PadY*2, single line

            Assert.Equal(80f, rScaled.Width);    // widest wrapped chunk "one"/"two" = 30*2=60 -> +PadX*2(20)
            Assert.True(rScaled.Width <= 100f);  // stays within the cap
            // Two wrapped lines at scale 2: contentH = (20*2+3)*2 - 3 = 83 -> panelH 83+16=99.
            Assert.Equal(99f, rScaled.Height);
        }

        [Fact]
        public void Draw_layout_wraps_the_body_once_and_hands_the_wrapped_lines_back()
        {
            // The draw path takes its bounds AND the lines it walks from a single layout pass. Measuring the
            // bounds alone and measuring them for a draw therefore cost exactly the same, because the wrap
            // happens once either way. Draw used to word-wrap a second time with the identical font, lines and
            // width cap to get the lines back, so every visible tooltip wrapped its body twice per frame.
            const string body = "one two three four";
            var lines = One(body);

            var boundsOnly = new CountingFont();
            Rect measured = Tooltip.ComputeBounds(boundsOnly, "", "", boundsOnly, boundsOnly, lines,
                new Vector2(400, 300), View, M, 100f, 1f);

            var drawLayout = new CountingFont();
            Rect forDraw = Tooltip.ComputeBounds(drawLayout, "", "", drawLayout, drawLayout, lines,
                new Vector2(400, 300), View, M, 100f, 1f, out List<TooltipLine> visual);

            Assert.Equal(measured, forDraw);
            Assert.Equal(1, boundsOnly.WrapsOf(body));
            Assert.Equal(1, drawLayout.WrapsOf(body));

            // And what comes back is the wrapped body the bounds were measured from, ready to draw.
            Assert.Equal(3, visual.Count);
            Assert.Equal("one two", visual[0].Text);
            Assert.Equal("three", visual[1].Text);
            Assert.Equal("four", visual[2].Text);
        }

        [Fact]
        public void TooltipLine_Of_carries_the_scale_through()
        {
            TooltipLine line = TooltipLine.Of(LocalizedText.Raw("hi"), Vector4.One, 0.5f);
            Assert.Equal("hi", line.Text);
            Assert.Equal(0.5f, line.Scale);
        }
    }
}
