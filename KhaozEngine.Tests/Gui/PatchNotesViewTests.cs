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
