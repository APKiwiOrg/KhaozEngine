using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class ScrollablePanelTests
    {
        // 200x300 panel; 20 items of 40 + 4 spacing -> stride 44, content 880, maxScroll 580.
        static readonly Rect Box = new(100, 100, 200, 300);

        static ScrollablePanel Make() => new(Box) { ItemCount = 20, ItemHeight = 40, ItemSpacing = 4 };

        static InputState Frame(Vector2 pos, bool down, float scroll = 0)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, scroll, 960, 540);
        }

        [Fact]
        public void Max_scroll_is_content_minus_viewport()
        {
            Assert.Equal(580f, Make().MaxScroll, 2);
        }

        [Fact]
        public void Wheel_over_the_panel_scrolls_and_clamps_within_range()
        {
            var sp = Make();
            var p = new Pointer();
            var inside = new Vector2(150, 200);
            p.Update(Frame(inside, false, scroll: -5));   // wheel down 5 notches * 30 = 150
            sp.Update(p, Frame(inside, false, scroll: -5));
            Assert.Equal(150f, sp.ScrollOffset, 2);

            p.Update(Frame(inside, false, scroll: -100));  // overshoot -> clamp to maxScroll
            sp.Update(p, Frame(inside, false, scroll: -100));
            Assert.Equal(580f, sp.ScrollOffset, 2);

            p.Update(Frame(inside, false, scroll: 1000));  // back past the top -> clamp to 0
            sp.Update(p, Frame(inside, false, scroll: 1000));
            Assert.Equal(0f, sp.ScrollOffset, 2);
        }

        [Fact]
        public void Wheel_outside_the_panel_does_not_scroll()
        {
            var sp = Make();
            var p = new Pointer();
            var outside = new Vector2(10, 10);
            p.Update(Frame(outside, false, scroll: -5));
            sp.Update(p, Frame(outside, false, scroll: -5));
            Assert.Equal(0f, sp.ScrollOffset, 2);
        }

        [Fact]
        public void Dragging_inside_scrolls_by_the_pointer_delta()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 300), false));
            p.Update(Frame(new Vector2(150, 300), true));   // press inside
            sp.Update(p, Frame(new Vector2(150, 300), true));
            p.Update(Frame(new Vector2(150, 260), true));   // drag up 40px
            sp.Update(p, Frame(new Vector2(150, 260), true));
            Assert.Equal(40f, sp.ScrollOffset, 2);          // content moves up as the finger goes up
        }

        [Fact]
        public void ItemBounds_account_for_the_scroll_offset()
        {
            var sp = Make();
            sp.ScrollTo(44f);                               // one stride
            Assert.Equal(100f - 44f, sp.ItemBounds(0).Y, 2);
            Assert.Equal(100f, sp.ItemBounds(1).Y, 2);      // item 1 slid up into the top
        }

        [Fact]
        public void TappedItemIndex_maps_a_tap_to_the_right_row()
        {
            var sp = Make();
            var p = new Pointer();
            void Tap(Vector2 at)
            {
                p.Update(Frame(at, false));
                p.Update(Frame(at, true));
                p.Update(Frame(at, false));
            }
            Tap(new Vector2(150, 130));                     // y rel 30 -> row 0
            Assert.Equal(0, sp.TappedItemIndex(p));
            Tap(new Vector2(150, 150));                     // y rel 50 -> row 1
            Assert.Equal(1, sp.TappedItemIndex(p));
        }

        [Fact]
        public void Tap_in_the_spacing_gap_returns_no_row()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 142), false));  // rel 42, past the 40px item into the 4px gap
            p.Update(Frame(new Vector2(150, 142), true));
            p.Update(Frame(new Vector2(150, 142), false));
            Assert.Equal(-1, sp.TappedItemIndex(p));
        }

        [Fact]
        public void Update_blocks_the_pointer_when_enabled()
        {
            var sp = Make();
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 200), false));
            sp.Update(p, Frame(new Vector2(150, 200), false));
            Assert.True(p.IsBlocked(new Vector2(150, 200)));
        }
    }
}
