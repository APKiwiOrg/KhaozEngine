using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.App;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage of the retained <see cref="TabBar"/>: the pure per-tab layout math (<see cref="TabBar.TabRect"/>),
    /// the tap-to-activate + <see cref="TabBar.ChangedThisFrame"/> change signal, and the click-through gate. No
    /// texture/font drawing (Update only computes interaction).
    /// </summary>
    public class TabBarTests
    {
        static readonly Rect Bar = new(100, 100, 300, 40);   // 3 tabs -> 100px each

        static readonly IReadOnlyList<LocalizedText> Labels = new[]
        {
            LocalizedText.Raw("Goals"), LocalizedText.Raw("Tree"), LocalizedText.Raw("More"),
        };

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

        static TabBar NewBar() => new(Labels, font: null, Bar);

        // A press-origin tap (press and release both inside), the way the button fires.
        bool Tap(TabBar bar, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); bar.Update(p);
            p.Update(Frame(at, true)); bar.Update(p);
            p.Update(Frame(at, false)); return bar.Update(p);
        }

        static Vector2 CenterOf(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);

        [Fact]
        public void TabRect_evenly_splits_bounds_with_no_gap()
        {
            var bar = NewBar();
            Assert.Equal(3, bar.Count);

            Rect t0 = bar.TabRect(0), t1 = bar.TabRect(1), t2 = bar.TabRect(2);

            Assert.Equal(100f, t0.X);
            Assert.Equal(100f, t0.Width, 3);
            Assert.Equal(t0.Right, t1.X, 3);          // tabs abut, no gap
            Assert.Equal(t1.Right, t2.X, 3);
            Assert.Equal(Bar.Right, t2.Right, 3);     // last edge lands exactly on the bar's right
            Assert.Equal(Bar.Y, t0.Y);
            Assert.Equal(Bar.Height, t0.Height);
        }

        // --- TabStripDrawGeometry: the whole-unit DRAW rounding that keeps the shared frame + dividers crisp in a
        // non-snapping design pass. Distinct from TabRect (fractional, for exact hit-testing) which is unchanged. ---

        [Fact]
        public void TabStripDrawGeometry_rounds_every_split_edge_to_a_whole_unit()
        {
            // 100 / 3 splits at 33.33.. which would render soft in a design pass; the draw geometry rounds each edge.
            var (frame, edges) = GuiDraw.TabStripDrawGeometry(new Rect(100f, 100f, 100f, 40f), 3);

            Assert.Equal(4, edges.Length);                        // count + 1 edges
            foreach (float e in edges) Assert.Equal(e, MathF.Round(e), 4);   // every edge is integral
            Assert.Equal(new[] { 100f, 133f, 167f, 200f }, edges);
            Assert.Equal(new Rect(100f, 100f, 100f, 40f), frame);
        }

        [Fact]
        public void TabStripDrawGeometry_frame_spans_the_rounded_bounds_exactly()
        {
            var b = new Rect(10.4f, 20.6f, 99.7f, 30.2f);
            var (frame, edges) = GuiDraw.TabStripDrawGeometry(b, 2);

            Assert.Equal(3, edges.Length);
            Assert.Equal(edges[0], frame.X, 4);                  // frame left == first edge
            Assert.Equal(edges[2], frame.Right, 4);              // frame right == last edge
            Assert.Equal(MathF.Round(b.X), edges[0], 4);
            Assert.Equal(MathF.Round(b.Right), edges[2], 4);
            Assert.Equal(MathF.Round(b.Y), frame.Y, 4);
            Assert.Equal(MathF.Round(b.Bottom), frame.Bottom, 4);
        }

        [Fact]
        public void TabStripDrawGeometry_edges_are_monotonic_with_interior_dividers_between()
        {
            var (_, edges) = GuiDraw.TabStripDrawGeometry(new Rect(0f, 0f, 250f, 20f), 3);

            Assert.Equal(4, edges.Length);
            for (int i = 1; i < edges.Length; i++)
                Assert.True(edges[i] >= edges[i - 1]);           // non-decreasing: tabs never invert
            Assert.Equal(new[] { 83f, 167f }, new[] { edges[1], edges[2] });   // interior dividers
        }

        [Fact]
        public void Tap_in_a_tab_activates_it_and_raises_ChangedThisFrame()
        {
            var bar = NewBar();
            var p = new Pointer();

            bool ret = Tap(bar, p, CenterOf(bar.TabRect(1)));

            Assert.True(ret);
            Assert.Equal(1, bar.ActiveIndex);
            Assert.True(bar.ChangedThisFrame);
        }

        [Fact]
        public void Tap_on_the_already_active_tab_does_not_raise_ChangedThisFrame()
        {
            var bar = NewBar();          // ActiveIndex defaults to 0
            var p = new Pointer();

            bool ret = Tap(bar, p, CenterOf(bar.TabRect(0)));

            Assert.False(ret);
            Assert.Equal(0, bar.ActiveIndex);
            Assert.False(bar.ChangedThisFrame);
        }

        [Fact]
        public void Tap_outside_the_bar_changes_nothing()
        {
            var bar = NewBar();
            bar.ActiveIndex = 2;
            var p = new Pointer();

            bool ret = Tap(bar, p, new Vector2(10, 10));

            Assert.False(ret);
            Assert.Equal(2, bar.ActiveIndex);
            Assert.False(bar.ChangedThisFrame);
        }

        [Fact]
        public void ChangedThisFrame_clears_on_the_next_frame_without_a_tap()
        {
            var bar = NewBar();
            var p = new Pointer();

            Tap(bar, p, CenterOf(bar.TabRect(1)));
            Assert.True(bar.ChangedThisFrame);

            p.Update(Frame(CenterOf(bar.TabRect(1)), false));   // idle frame, no new tap
            bar.Update(p);
            Assert.False(bar.ChangedThisFrame);
        }

        [Fact]
        public void Setting_ActiveIndex_does_not_raise_ChangedThisFrame_and_clamps()
        {
            var bar = NewBar();
            var p = new Pointer();
            p.Update(Frame(new Vector2(10, 10), false));
            bar.Update(p);                     // establishes ChangedThisFrame == false

            bar.ActiveIndex = 1;
            Assert.Equal(1, bar.ActiveIndex);
            Assert.False(bar.ChangedThisFrame);

            bar.ActiveIndex = 99;              // clamps to the last tab
            Assert.Equal(2, bar.ActiveIndex);
            bar.ActiveIndex = -5;              // clamps to the first
            Assert.Equal(0, bar.ActiveIndex);
        }

        [Fact]
        public void Update_reserves_bounds_on_the_pointer()
        {
            var bar = NewBar();
            var p = new Pointer();
            p.Update(Frame(CenterOf(Bar), false));

            bar.Update(p);

            Assert.True(p.IsBlocked(CenterOf(Bar)));            // bar reserved for click-through
            Assert.False(p.IsBlocked(new Vector2(10, 10)));    // outside: not blocked
        }

        [Fact]
        public void Empty_labels_throws()
        {
            Assert.Throws<ArgumentException>(() => new TabBar(Array.Empty<LocalizedText>()));
        }

        [Fact]
        public void TabRect_out_of_range_throws()
        {
            var bar = NewBar();
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.TabRect(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => bar.TabRect(-1));
        }

        // --- TextScale: label-only, so it defaults to 1f and never perturbs the hit-geometry. ---

        [Fact]
        public void TextScale_defaults_to_one()
        {
            var bar = NewBar();
            Assert.Equal(1f, bar.TextScale);
        }

        [Fact]
        public void TabRect_is_independent_of_TextScale()
        {
            var bar = NewBar();
            Rect before0 = bar.TabRect(0), before1 = bar.TabRect(1), before2 = bar.TabRect(2);

            bar.TextScale = 0.5f;

            Assert.Equal(before0, bar.TabRect(0));
            Assert.Equal(before1, bar.TabRect(1));
            Assert.Equal(before2, bar.TabRect(2));
        }
    }
}
