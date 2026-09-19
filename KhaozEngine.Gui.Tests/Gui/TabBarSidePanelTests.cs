using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The additive knobs a side panel's tab strip needs on the retained <see cref="TabBar"/>: a closed state
    /// with no active tab, and an opt-out of the pointer reservation. Both default to the shipped behaviour, so
    /// each fact here also pins what a bar that asks for neither still does.
    /// </summary>
    public class TabBarSidePanelTests
    {
        static readonly Rect Bar = new(100f, 100f, 300f, 40f);

        static readonly IReadOnlyList<LocalizedText> Labels = new[]
        {
            LocalizedText.Raw("One"), LocalizedText.Raw("Two"), LocalizedText.Raw("Three"),
        };

        // One per test-class instance (xUnit builds a fresh instance per fact), so the press and release edges
        // derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var buttons = new HashSet<MouseButton>();
            if (down) buttons.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(buttons);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                buttons, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        bool Tap(TabBar bar, Pointer pointer, Vector2 at)
        {
            pointer.Update(Frame(at, false)); bar.Update(pointer);
            pointer.Update(Frame(at, true)); bar.Update(pointer);
            pointer.Update(Frame(at, false)); return bar.Update(pointer);
        }

        static Vector2 CentreOf(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);

        static TabBar NewBar() => new(Labels, font: null, Bar);

        [Fact]
        public void A_bar_that_never_asks_for_it_cannot_hold_no_active_tab()
        {
            var bar = NewBar();

            Assert.False(bar.AllowNoActiveTab);
            Assert.Equal(0, bar.ActiveIndex);

            bar.ActiveIndex = TabBar.NoActiveTab;
            Assert.Equal(0, bar.ActiveIndex);
            bar.ActiveIndex = -5;
            Assert.Equal(0, bar.ActiveIndex);
        }

        [Fact]
        public void The_opt_in_lets_the_bar_draw_with_nothing_active()
        {
            var bar = NewBar();
            bar.AllowNoActiveTab = true;

            bar.ActiveIndex = TabBar.NoActiveTab;
            Assert.Equal(-1, bar.ActiveIndex);

            // Only -1 is reachable: further negatives still clamp, and the top end is unchanged.
            bar.ActiveIndex = -99;
            Assert.Equal(-1, bar.ActiveIndex);
            bar.ActiveIndex = 99;
            Assert.Equal(2, bar.ActiveIndex);
        }

        [Fact]
        public void A_tap_from_no_active_tab_activates_the_tapped_one()
        {
            var bar = NewBar();
            bar.AllowNoActiveTab = true;
            bar.ActiveIndex = TabBar.NoActiveTab;
            var pointer = new Pointer();

            bool changed = Tap(bar, pointer, CentreOf(bar.TabRect(0)));

            // Nothing was active, so even the first tab is a change: a closed panel opens on the tab it was
            // closed from.
            Assert.True(changed);
            Assert.True(bar.ChangedThisFrame);
            Assert.Equal(0, bar.ActiveIndex);
        }

        [Fact]
        public void A_disabled_tap_from_no_active_tab_is_still_swallowed()
        {
            var bar = new TabBar(new[]
            {
                new TabBarItem(LocalizedText.Raw("Live")),
                new TabBarItem(LocalizedText.Raw("Soon"), Enabled: false),
            }, bounds: Bar)
            {
                AllowNoActiveTab = true,
                ActiveIndex = TabBar.NoActiveTab,
            };
            var pointer = new Pointer();

            bool changed = Tap(bar, pointer, CentreOf(bar.TabRect(1)));

            Assert.False(changed);
            Assert.Equal(-1, bar.ActiveIndex);
            Assert.True(pointer.IsConsumed);
        }

        [Fact]
        public void The_order_the_opt_in_and_the_closed_state_are_assigned_in_does_not_matter()
        {
            // An object initializer runs in source order, so the -1 lands BEFORE the opt-in here. A floor applied
            // in the setter opened this panel on tab 0 with nothing to say why.
            var closedFirst = new TabBar(Labels, font: null, Bar)
            {
                ActiveIndex = TabBar.NoActiveTab,
                AllowNoActiveTab = true,
            };
            Assert.Equal(TabBar.NoActiveTab, closedFirst.ActiveIndex);

            // Until the opt-in arrives the bar still reads as it always has: one tab active, the first. And it
            // BEHAVES that way too, not only reads that way: a tap on the first tab is no change.
            var notYet = new TabBar(Labels, font: null, Bar) { ActiveIndex = TabBar.NoActiveTab };
            Assert.Equal(0, notYet.ActiveIndex);
            var pointer = new Pointer();
            Assert.False(Tap(notYet, pointer, CentreOf(notYet.TabRect(0))));
            Assert.False(notYet.ChangedThisFrame);
            Assert.Equal(0, notYet.ActiveIndex);
        }

        [Fact]
        public void Taking_the_opt_in_away_re_clamps_onto_the_first_tab()
        {
            var bar = NewBar();
            bar.AllowNoActiveTab = true;
            bar.ActiveIndex = TabBar.NoActiveTab;

            bar.AllowNoActiveTab = false;

            Assert.Equal(0, bar.ActiveIndex);
        }

        [Fact]
        public void The_bar_reserves_its_region_by_default_and_stops_when_told_to()
        {
            var bar = NewBar();
            Assert.True(bar.BlocksPointer);

            var pointer = new Pointer();
            pointer.Update(Frame(CentreOf(Bar), false));
            bar.Update(pointer);
            Assert.True(pointer.IsBlocked(CentreOf(Bar)));

            var free = NewBar();
            free.BlocksPointer = false;
            var second = new Pointer();
            second.Update(Frame(CentreOf(Bar), false));
            free.Update(second);
            Assert.False(second.IsBlocked(CentreOf(Bar)));
        }

        [Fact]
        public void A_bar_that_reserves_nothing_still_selects_a_tab()
        {
            var bar = NewBar();
            bar.BlocksPointer = false;
            var pointer = new Pointer();

            bool changed = Tap(bar, pointer, CentreOf(bar.TabRect(2)));

            Assert.True(changed);
            Assert.Equal(2, bar.ActiveIndex);
            Assert.False(pointer.IsBlocked(CentreOf(Bar)));
        }
    }
}
