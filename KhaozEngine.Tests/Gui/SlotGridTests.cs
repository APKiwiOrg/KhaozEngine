using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class SlotGridTests
    {
        static InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // An "up" frame at `at` (windowFocused defaults true) so IsHoveringIn reports the hovered slot.
        static Pointer Hovering(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));
            return p;
        }

        // Press then release at the same point -> a valid press-origin tap (IsTapIn).
        static Pointer Tapping(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));   // up
            p.Update(Frame(at, true));    // press at `at`
            p.Update(Frame(at, false));   // release at `at`
            return p;
        }

        // 5x6 inventory: 30 slots, 5 columns, slot 40, spacing 4, origin (100,100).
        static SlotGrid Grid() => new(new Rect(100, 100, 0, 0), count: 30, columns: 5) { SlotSize = 40f, Spacing = 4f };

        static void AssertRect(float x, float y, float w, float h, Rect r)
        {
            Assert.Equal(x, r.X, 3);
            Assert.Equal(y, r.Y, 3);
            Assert.Equal(w, r.Width, 3);
            Assert.Equal(h, r.Height, 3);
        }

        [Fact]
        public void Rows_is_ceil_count_over_columns()
        {
            Assert.Equal(6, Grid().Rows);
            Assert.Equal(1, new SlotGrid(new Rect(0, 0, 0, 0), 10, 10).Rows);   // 10-slot hotbar
            Assert.Equal(2, new SlotGrid(new Rect(0, 0, 0, 0), 11, 10).Rows);   // partial last row
            Assert.Equal(0, new SlotGrid(new Rect(0, 0, 0, 0), 0, 5).Rows);     // empty
        }

        [Fact]
        public void SlotRect_lays_out_columns_then_rows_with_spacing()
        {
            var g = Grid();
            AssertRect(100, 100, 40, 40, g.SlotRect(0));   // origin
            AssertRect(144, 100, 40, 40, g.SlotRect(1));   // one column over: x += 40 + 4
            AssertRect(100, 144, 40, 40, g.SlotRect(5));   // wraps to row 1, col 0: y += 44
            AssertRect(188, 144, 40, 40, g.SlotRect(7));   // row 1, col 2
        }

        [Fact]
        public void ContentSize_covers_full_columns_and_rows()
        {
            var g = Grid();                        // 5 cols, 6 rows, slot 40, spacing 4
            Assert.Equal(5 * 40 + 4 * 4, g.ContentSize.X, 3);   // 216
            Assert.Equal(6 * 40 + 5 * 4, g.ContentSize.Y, 3);   // 260
        }

        [Fact]
        public void ContentSize_partial_single_row_is_only_count_wide()
        {
            var g = new SlotGrid(new Rect(0, 0, 0, 0), count: 3, columns: 10) { SlotSize = 40f, Spacing = 4f };
            Assert.Equal(3 * 40 + 2 * 4, g.ContentSize.X, 3);   // 128, not the full 10 columns
            Assert.Equal(40f, g.ContentSize.Y, 3);              // one row tall
        }

        [Fact]
        public void SlotAt_finds_the_slot_and_reports_gaps_as_off()
        {
            var g = Grid();
            Assert.Equal(0, g.SlotAt(new Vector2(120, 120)));   // inside slot 0 [100,140)
            Assert.Equal(1, g.SlotAt(new Vector2(160, 120)));   // inside slot 1 [144,184)
            Assert.Equal(-1, g.SlotAt(new Vector2(142, 120)));  // in the 4px gap (140..144)
            Assert.Equal(-1, g.SlotAt(new Vector2(5, 5)));      // off-grid
        }

        [Fact]
        public void Update_reports_the_hovered_slot_from_the_pointer()
        {
            var g = Grid();
            g.Update(Hovering(new Vector2(160, 120)));   // slot 1
            Assert.Equal(1, g.HoveredSlot);
            Assert.Equal(-1, g.PressedSlot);
        }

        [Fact]
        public void Update_hover_in_a_gap_reports_no_slot()
        {
            var g = Grid();
            g.Update(Hovering(new Vector2(142, 120)));   // gap between slot 0 and 1
            Assert.Equal(-1, g.HoveredSlot);
        }

        [Fact]
        public void Update_reports_the_pressed_slot()
        {
            var g = Grid();
            var p = new Pointer();
            p.Update(Frame(new Vector2(160, 120), false));   // up
            p.Update(Frame(new Vector2(160, 120), true));    // press in slot 1
            g.Update(p);
            Assert.Equal(1, g.PressedSlot);
        }

        [Fact]
        public void Tap_fires_onclick_for_the_slot_and_returns_its_index()
        {
            var g = Grid();
            int clicked = -1;
            g.OnSlotClicked = i => clicked = i;
            int ret = g.Update(Tapping(new Vector2(160, 120)));   // slot 1
            Assert.Equal(1, ret);
            Assert.Equal(1, clicked);
        }

        [Fact]
        public void Tap_that_began_in_another_slot_does_not_fire_either_slot()
        {
            var g = Grid();
            int clicked = -1;
            g.OnSlotClicked = i => clicked = i;
            var p = new Pointer();
            p.Update(Frame(new Vector2(120, 120), false));   // up over slot 0
            p.Update(Frame(new Vector2(120, 120), true));    // press in slot 0
            p.Update(Frame(new Vector2(160, 120), false));   // release over slot 1
            int ret = g.Update(p);
            Assert.Equal(-1, ret);        // press-origin invariant: origin in 0, release in 1
            Assert.Equal(-1, clicked);
        }

        [Fact]
        public void Update_blocks_the_footprint_for_click_through()
        {
            var g = Grid();
            var p = Hovering(new Vector2(160, 120));
            g.Update(p);
            Assert.True(p.IsBlocked(new Vector2(120, 120)));    // a slot
            Assert.False(p.IsBlocked(new Vector2(1000, 1000))); // outside the footprint
        }
    }
}
