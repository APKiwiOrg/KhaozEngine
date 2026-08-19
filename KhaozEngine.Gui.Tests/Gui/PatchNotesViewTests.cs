using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class PatchNotesViewTests
{
    // A device-free measurer: every glyph is 8 wide, the line is 16 tall (the brief's fake).
    sealed class FakeMeasurer : ITextMeasurer
    {
        public float LineHeight => 16f;
        public Vector2 Measure(string text) => new(text.Length * 8f, 16f);
    }

    static readonly FakeMeasurer Measurer = new();

    // A second, distinct measurer with wider glyphs - used to prove the per-note layout cache (keyed on
    // width + measurer identity) invalidates on a measurer change too, not just a width change.
    sealed class WideFakeMeasurer : ITextMeasurer
    {
        public float LineHeight => 16f;
        public Vector2 Measure(string text) => new(text.Length * 20f, 16f);
    }

    static readonly WideFakeMeasurer WideMeasurer = new();

    static PatchNotesDocument Doc(int builds, int groups = 2, int notes = 2)
    {
        var b = new List<PatchNotesBuild>();
        for (int i = 0; i < builds; i++)
        {
            var gs = new List<PatchNoteGroup>();
            for (int g = 0; g < groups; g++)
            {
                var ns = new List<PatchNote>();
                for (int n = 0; n < notes; n++)
                    ns.Add(new PatchNote(new[] { new PatchNoteSpan($"note {i} {g} {n} text", false) }));
                gs.Add(new PatchNoteGroup((PatchNoteCategory)(g % 5), ns));
            }
            b.Add(new PatchNotesBuild($"0.{builds - i}.0", $"Build {i}", "2026-07-10", gs));
        }
        return new PatchNotesDocument("Test - Player Changelog", b);
    }

    // ---- expansion state -------------------------------------------------------------------------

    [Fact]
    public void Newest_build_starts_expanded_rest_collapsed()
    {
        var view = new PatchNotesView(Doc(3));
        Assert.True(view.IsExpanded(0));
        Assert.False(view.IsExpanded(1));
        Assert.False(view.IsExpanded(2));
    }

    [Fact]
    public void Toggle_flips_state_and_flips_back()
    {
        var view = new PatchNotesView(Doc(3));

        Assert.False(view.IsExpanded(1));
        view.Toggle(1);
        Assert.True(view.IsExpanded(1));
        view.Toggle(1);
        Assert.False(view.IsExpanded(1));

        Assert.True(view.IsExpanded(0));
        view.Toggle(0);
        Assert.False(view.IsExpanded(0));
    }

    [Fact]
    public void Toggle_out_of_range_is_a_no_op_not_a_throw()
    {
        var view = new PatchNotesView(Doc(2));
        view.Toggle(-1);
        view.Toggle(99);
        // untouched
        Assert.True(view.IsExpanded(0));
        Assert.False(view.IsExpanded(1));
    }

    [Fact]
    public void IsExpanded_out_of_range_returns_false_not_a_throw()
    {
        var view = new PatchNotesView(Doc(2));
        Assert.False(view.IsExpanded(-1));
        Assert.False(view.IsExpanded(5));
    }

    // ---- content height --------------------------------------------------------------------------

    [Fact]
    public void MeasureContentHeight_grows_when_a_collapsed_build_expands()
    {
        var view = new PatchNotesView(Doc(2));
        float before = view.MeasureContentHeight(Measurer, 300f);
        view.Toggle(1); // expand the second build
        float after = view.MeasureContentHeight(Measurer, 300f);
        Assert.True(after > before, $"expected {after} > {before}");
    }

    [Fact]
    public void MeasureContentHeight_shrinks_back_when_a_build_collapses()
    {
        var view = new PatchNotesView(Doc(2));
        float baseline = view.MeasureContentHeight(Measurer, 300f);
        view.Toggle(1);
        view.Toggle(1); // back to the original expansion set
        Assert.Equal(baseline, view.MeasureContentHeight(Measurer, 300f), 3);
    }

    [Fact]
    public void MeasureContentHeight_is_stable_across_repeated_calls()
    {
        var view = new PatchNotesView(Doc(3));
        float a = view.MeasureContentHeight(Measurer, 280f);
        float b = view.MeasureContentHeight(Measurer, 280f);
        float c = view.MeasureContentHeight(Measurer, 280f);
        Assert.Equal(a, b, 3);
        Assert.Equal(b, c, 3);
    }

    // Guards the per-note layout cache introduced to avoid re-running LayoutNote for every note on every
    // MeasureContentHeight/Update/Draw call: a width change must invalidate it, not silently reuse a stale
    // wrap computed at the previous width, and returning to an earlier width must recompute correctly rather
    // than serving a result left over from a still-earlier call at that same width.
    [Fact]
    public void MeasureContentHeight_AlternatingWidths_RecomputesCorrectly_NeverServesAStaleWidth()
    {
        var view = new PatchNotesView(Doc(3));
        float wide = view.MeasureContentHeight(Measurer, 400f);
        float narrow = view.MeasureContentHeight(Measurer, 100f);   // narrower -> wraps to more lines -> taller
        float wideAgain = view.MeasureContentHeight(Measurer, 400f);
        float narrowAgain = view.MeasureContentHeight(Measurer, 100f);

        Assert.True(narrow > wide, $"narrower width should wrap to more lines: narrow={narrow}, wide={wide}");
        Assert.Equal(wide, wideAgain, 3);
        Assert.Equal(narrow, narrowAgain, 3);
    }

    // Same cache, the other key component: a different ITextMeasurer identity (e.g. a font swap) must also
    // invalidate the cache, and switching back to the original measurer must not serve the other measurer's
    // (now stale) layout.
    [Fact]
    public void MeasureContentHeight_AlternatingMeasurers_RecomputesCorrectly_NeverLeaksBetweenFonts()
    {
        var view = new PatchNotesView(Doc(3));
        float narrowGlyphs = view.MeasureContentHeight(Measurer, 300f);
        float wideGlyphs = view.MeasureContentHeight(WideMeasurer, 300f);   // same width, wider glyphs -> taller
        float narrowGlyphsAgain = view.MeasureContentHeight(Measurer, 300f);

        Assert.True(wideGlyphs > narrowGlyphs, $"wider glyphs should wrap to more lines: {wideGlyphs} vs {narrowGlyphs}");
        Assert.Equal(narrowGlyphs, narrowGlyphsAgain, 3);
    }

    // ---- scroll clamping (driven through the public input path) -----------------------------------

    static InputState WheelFrame(Vector2 pos, float scrollDelta, int w, int h) =>
        new(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            pos, Vector2.Zero, scrollDelta, w, h);

    static InputState KeyHeldFrame(Key key, Vector2 pos, int w, int h) =>
        new(new HashSet<Key> { key }, new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            pos, Vector2.Zero, 0f, w, h);

    static Vector2 Center(Rect r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    static float ExpectedMaxScroll(PatchNotesView view, Rect viewport)
    {
        Rect content = view.ContentViewport(viewport);
        float total = view.MeasureContentHeight(Measurer, content.Width);
        return System.MathF.Max(0f, total - content.Height);
    }

    [Fact]
    public void Wheel_scroll_clamps_to_zero_at_the_top()
    {
        var viewport = new Rect(0, 0, 320, 220);
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        Assert.True(ExpectedMaxScroll(view, viewport) > 0f); // content overflows: the test is meaningful

        var pointer = new Pointer();
        Vector2 at = Center(view.ContentViewport(viewport));
        // Scroll up hard (positive delta drives the offset toward the top).
        for (int i = 0; i < 30; i++)
        {
            InputState f = WheelFrame(at, 100f, 320, 220);
            pointer.Update(f);
            view.Update(pointer, f, 0.016f, viewport, Measurer);
        }
        Assert.Equal(0f, view.ScrollOffset, 3);
    }

    [Fact]
    public void Wheel_scroll_never_exceeds_content_minus_viewport()
    {
        var viewport = new Rect(0, 0, 320, 220);
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);

        var pointer = new Pointer();
        Vector2 at = Center(view.ContentViewport(viewport));
        for (int i = 0; i < 40; i++)
        {
            InputState f = WheelFrame(at, -100f, 320, 220);
            pointer.Update(f);
            view.Update(pointer, f, 0.016f, viewport, Measurer);
        }
        Assert.Equal(max, view.ScrollOffset, 3);
    }

    [Fact]
    public void Held_down_key_scrolls_and_clamps_at_the_bottom()
    {
        var viewport = new Rect(0, 0, 320, 220);
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);

        var pointer = new Pointer();
        Vector2 at = Center(view.ContentViewport(viewport));
        for (int i = 0; i < 200; i++)
        {
            InputState f = KeyHeldFrame(Key.Down, at, 320, 220);
            pointer.Update(f);
            view.Update(pointer, f, 0.1f, viewport, Measurer);
        }
        Assert.Equal(max, view.ScrollOffset, 3);

        for (int i = 0; i < 200; i++)
        {
            InputState f = KeyHeldFrame(Key.Up, at, 320, 220);
            pointer.Update(f);
            view.Update(pointer, f, 0.1f, viewport, Measurer);
        }
        Assert.Equal(0f, view.ScrollOffset, 3);
    }

    // ---- content drag-to-scroll -------------------------------------------------------------------

    // A large synthetic window, decoupled from the small (320x220) viewport PatchNotesView lays out in:
    // Pointer.Update only accepts a position within [0, Width) x [0, Height) as "in the window", so a big
    // multi-hundred-pixel synthetic drag needs headroom in both directions without leaving that window.
    // The viewport itself is offset well inside this window (not at the window's own origin) for the same
    // reason: a drag toward the top must still land at a non-negative on-screen Y.
    const int DragWindowW = 320, DragWindowH = 20_000;
    static readonly Rect DragViewport = new(0, 10_000, 320, 220);

    // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
    // release edges derive from this test's own frame sequence and nothing crosses between tests.
    readonly MouseFrames _mouse = new();

    InputState MouseDownFrame(Vector2 pos, int w, int h)
    {
        var held = new HashSet<MouseButton> { MouseButton.Left };
        var (edgePressed, edgeReleased) = _mouse.Advance(held);
        return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            held, edgePressed, pos, Vector2.Zero, 0f, w, h, mouseReleased: edgeReleased);
    }

    [Fact]
    public void Dragging_the_content_area_scrolls_it_and_clamps()
    {
        Rect viewport = DragViewport;
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);

        Rect content = view.ContentViewport(viewport);
        Vector2 start = Center(content);
        var pointer = new Pointer();

        // Press inside the content area (no motion yet: this frame only sets the press origin).
        InputState press = MouseDownFrame(start, DragWindowW, DragWindowH);
        pointer.Update(press);
        view.Update(pointer, press, 0.016f, viewport, Measurer);
        Assert.Equal(0f, view.ScrollOffset, 3);

        // Drag upward (negative Y): dragging the content up scrolls further DOWN the document.
        InputState dragUp = MouseDownFrame(start - new Vector2(0f, 50f), DragWindowW, DragWindowH);
        pointer.Update(dragUp);
        view.Update(pointer, dragUp, 0.016f, viewport, Measurer);
        Assert.Equal(50f, view.ScrollOffset, 2);

        // Drag far past the bottom of the content: clamps to max, does not overshoot.
        InputState dragPastEnd = MouseDownFrame(start - new Vector2(0f, 5000f), DragWindowW, DragWindowH);
        pointer.Update(dragPastEnd);
        view.Update(pointer, dragPastEnd, 0.016f, viewport, Measurer);
        Assert.Equal(max, view.ScrollOffset, 2);

        // Drag back down (positive Y) past the top: clamps to zero.
        InputState dragPastTop = MouseDownFrame(start + new Vector2(0f, 5000f), DragWindowW, DragWindowH);
        pointer.Update(dragPastTop);
        view.Update(pointer, dragPastTop, 0.016f, viewport, Measurer);
        Assert.Equal(0f, view.ScrollOffset, 2);
    }

    // ---- header tap toggling ----------------------------------------------------------------------

    [Fact]
    public void Tapping_a_build_header_toggles_its_expansion()
    {
        var viewport = new Rect(0, 0, 640, 480);
        var view = new PatchNotesView(Doc(2));
        Assert.True(view.IsExpanded(0));
        Assert.False(view.IsExpanded(1));

        Rect content = view.ContentViewport(viewport);
        // Build 0's header sits at the very top of the content column.
        var headerPoint = new Vector2(content.X + 10f, content.Y + 10f);

        var pointer = new Pointer();
        InputState press = MouseDownFrame(headerPoint, 640, 480);
        pointer.Update(press);
        view.Update(pointer, press, 0.016f, viewport, Measurer);

        InputState release = WheelFrame(headerPoint, 0f, 640, 480); // same position, button up
        pointer.Update(release);
        view.Update(pointer, release, 0.016f, viewport, Measurer);

        Assert.False(view.IsExpanded(0)); // tap collapsed it
    }

    [Fact]
    public void Tapping_a_header_scrolled_under_the_title_bar_does_not_toggle_it()
    {
        var viewport = new Rect(0, 0, 320, 220);
        var view = new PatchNotesView(Doc(2, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);
        Assert.True(view.IsExpanded(0));

        Rect content = view.ContentViewport(viewport);
        var pointer = new Pointer();

        // Nudge the scroll offset to exactly half of build 0's header row (a single wheel tick with a
        // known delta, hovering the content): the header's Rect now straddles content.Y, half still above
        // it (behind the title-bar padding, not clipped-drawn but still geometrically there) and half
        // inside the visible content.
        const float halfHeader = 16f; // BuildHeaderHeight / 2, mirrored here since the constant is private
        const float scrollWheelSpeed = 40f; // mirrors PatchNotesView's private ScrollWheelSpeed
        InputState wheel = WheelFrame(Center(content), -(halfHeader / scrollWheelSpeed), 320, 220);
        pointer.Update(wheel);
        view.Update(pointer, wheel, 0.016f, viewport, Measurer);
        Assert.Equal(halfHeader, view.ScrollOffset, 2);

        // Tap the UPPER half of that straddling header rect: geometrically part of the header, but above
        // content.Y - i.e. over the chrome outside the scroll area. Must not toggle.
        var aboveContent = new Vector2(content.X + 10f, content.Y - 8f);
        Assert.False(content.Contains(aboveContent), "test setup: the tap must land above the content viewport");

        InputState press = MouseDownFrame(aboveContent, 320, 220);
        pointer.Update(press);
        view.Update(pointer, press, 0.016f, viewport, Measurer);
        InputState release = WheelFrame(aboveContent, 0f, 320, 220);
        pointer.Update(release);
        view.Update(pointer, release, 0.016f, viewport, Measurer);

        Assert.True(view.IsExpanded(0)); // untouched: the tap fell outside the content viewport

        // Control: tapping the LOWER half of the very same header row (still inside content) DOES toggle
        // it - proving the header really is there and it is specifically the out-of-content tap that is
        // rejected, not some unrelated geometry mistake.
        var insideContent = new Vector2(content.X + 10f, content.Y + 8f);
        Assert.True(content.Contains(insideContent), "test setup: the control tap must land inside the content viewport");

        InputState press2 = MouseDownFrame(insideContent, 320, 220);
        pointer.Update(press2);
        view.Update(pointer, press2, 0.016f, viewport, Measurer);
        InputState release2 = WheelFrame(insideContent, 0f, 320, 220);
        pointer.Update(release2);
        view.Update(pointer, release2, 0.016f, viewport, Measurer);

        Assert.False(view.IsExpanded(0)); // the same header, tapped inside content, does toggle
    }

    // ---- scrollbar thumb drag ----------------------------------------------------------------------

    [Fact]
    public void Thumb_drag_moves_the_scroll_offset_proportionally_and_clamps_at_both_ends()
    {
        Rect viewport = DragViewport;
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);

        Rect thumb = view.ScrollbarThumbRect(viewport, Measurer);
        Rect content = view.ContentViewport(viewport);
        float travel = content.Height - thumb.Height;
        Assert.True(travel > 0f, "test is only meaningful when the thumb can travel");

        Vector2 grab = Center(thumb);
        var pointer = new Pointer();

        // Press on the thumb: captures it, but this frame alone must not move the offset.
        InputState press = MouseDownFrame(grab, DragWindowW, DragWindowH);
        pointer.Update(press);
        view.Update(pointer, press, 0.016f, viewport, Measurer);
        Assert.Equal(0f, view.ScrollOffset, 2);

        // Drag the thumb down its full travel, in steps (each step feeds a fresh pointer position so
        // Pointer.Delta reflects the incremental move): the offset should land on the max.
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 at = grab + new Vector2(0f, travel * i / steps);
            InputState f = MouseDownFrame(at, DragWindowW, DragWindowH);
            pointer.Update(f);
            view.Update(pointer, f, 0.016f, viewport, Measurer);
        }
        Assert.Equal(max, view.ScrollOffset, 1);

        // Keep dragging past the end of the track: stays clamped at max, no overshoot.
        InputState overshoot = MouseDownFrame(grab + new Vector2(0f, travel * 3f), DragWindowW, DragWindowH);
        pointer.Update(overshoot);
        view.Update(pointer, overshoot, 0.016f, viewport, Measurer);
        Assert.Equal(max, view.ScrollOffset, 1);

        // Drag back up past the start of the track: clamps to zero.
        InputState back = MouseDownFrame(grab - new Vector2(0f, travel * 3f), DragWindowW, DragWindowH);
        pointer.Update(back);
        view.Update(pointer, back, 0.016f, viewport, Measurer);
        Assert.Equal(0f, view.ScrollOffset, 1);

        // Release: dragging further now (button up) must not move the offset any more.
        InputState release = WheelFrame(grab, 0f, DragWindowW, DragWindowH);
        pointer.Update(release);
        view.Update(pointer, release, 0.016f, viewport, Measurer);
        InputState afterRelease = WheelFrame(grab + new Vector2(0f, travel), 0f, DragWindowW, DragWindowH);
        pointer.Update(afterRelease);
        view.Update(pointer, afterRelease, 0.016f, viewport, Measurer);
        Assert.Equal(0f, view.ScrollOffset, 1);
    }

    [Fact]
    public void Drag_starting_off_the_thumb_does_not_capture_it()
    {
        Rect viewport = DragViewport;
        var view = new PatchNotesView(Doc(1, groups: 4, notes: 6));
        float max = ExpectedMaxScroll(view, viewport);
        Assert.True(max > 0f);

        Rect thumb = view.ScrollbarThumbRect(viewport, Measurer);
        Rect content = view.ContentViewport(viewport);

        // A point in the scrollbar's track column but off the thumb (near the bottom of the track,
        // clear of the thumb which starts at the top while ScrollOffset is 0), and outside the content
        // viewport too, so neither the thumb-drag nor the content-drag path can pick it up.
        var offThumb = new Vector2(thumb.X + thumb.Width * 0.5f, content.Bottom - 2f);
        Assert.False(thumb.Contains(offThumb), "test setup: the press point must actually miss the thumb");
        Assert.False(content.Contains(offThumb), "test setup: the press point must also miss the content area");

        var pointer = new Pointer();
        InputState press = MouseDownFrame(offThumb, DragWindowW, DragWindowH);
        pointer.Update(press);
        view.Update(pointer, press, 0.016f, viewport, Measurer);

        // Drag as if moving the thumb its whole travel: since the press origin missed the thumb, the
        // press-origin invariant must keep this gesture from capturing it.
        float travel = content.Height - thumb.Height;
        InputState drag = MouseDownFrame(offThumb + new Vector2(0f, travel), DragWindowW, DragWindowH);
        pointer.Update(drag);
        view.Update(pointer, drag, 0.016f, viewport, Measurer);

        Assert.Equal(0f, view.ScrollOffset, 2);
    }

    // ---- empty document --------------------------------------------------------------------------

    [Fact]
    public void Empty_document_reports_one_line_of_height_and_has_no_expand_state()
    {
        var view = new PatchNotesView(PatchNotesDocument.Empty);
        Assert.Equal(Measurer.LineHeight, view.MeasureContentHeight(Measurer, 300f), 3);
        Assert.False(view.IsExpanded(0));
        view.Toggle(0); // no-op, no throw
        Assert.False(view.IsExpanded(0));
    }

    // ---- close signal ----------------------------------------------------------------------------

    [Fact]
    public void Escape_requests_close()
    {
        var viewport = new Rect(0, 0, 640, 480);
        var view = new PatchNotesView(Doc(2));
        Assert.False(view.CloseRequested);

        var pointer = new Pointer();
        var escaped = new InputState(
            new HashSet<Key>(), new HashSet<Key> { Key.Escape }, new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 640, 480);
        pointer.Update(escaped);
        bool open = view.Update(pointer, escaped, 0.016f, viewport, Measurer);

        Assert.True(view.CloseRequested);
        Assert.False(open);
    }
}
