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

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static TabBar NewBar() => new(Labels, font: null, Bar);

        // A press-origin tap (press and release both inside), the way the button fires.
        static bool Tap(TabBar bar, Pointer p, Vector2 at)
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
    }
}
