using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The two members an inventory panel needs out of <see cref="SlotGrid"/>: a rectangular cell (a text row is
    /// not square) and a secondary tap carrying the same press-origin invariant the left tap has, which is what a
    /// per-slot context menu hangs off. Headless: frames are built by hand and fed to a <see cref="Pointer"/>.
    /// </summary>
    public class SlotGridRectangularTests
    {
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, params MouseButton[] held)
        {
            var down = new HashSet<MouseButton>(held);
            var (pressed, released) = _mouse.Advance(down);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, pressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: released);
        }

        // Grimhollow's inventory cell: 92 wide, 34 tall, because the round draws item NAMES rather than icons.
        static SlotGrid Panel() =>
            new(new Rect(20, 40, 0, 0), count: 6, columns: 2) { SlotWidth = 92f, SlotHeight = 34f, Spacing = 4f };

        static void AssertRect(float x, float y, float w, float h, Rect r)
        {
            Assert.Equal(x, r.X, 3);
            Assert.Equal(y, r.Y, 3);
            Assert.Equal(w, r.Width, 3);
            Assert.Equal(h, r.Height, 3);
        }

        [Fact]
        public void SlotSize_defaults_to_the_square_48_both_axes()
        {
            var g = new SlotGrid(new Rect(0, 0, 0, 0), 4, 2);
            Assert.Equal(48f, g.SlotWidth, 3);
            Assert.Equal(48f, g.SlotHeight, 3);
            Assert.Equal(48f, g.SlotSize, 3);
        }

        [Fact]
        public void SlotSize_writes_both_axes()
        {
            var g = new SlotGrid(new Rect(0, 0, 0, 0), 4, 2) { SlotSize = 32f };
            Assert.Equal(32f, g.SlotWidth, 3);
            Assert.Equal(32f, g.SlotHeight, 3);
            AssertRect(0, 0, 32, 32, g.SlotRect(0));
        }

        [Fact]
        public void SlotRect_lays_out_a_rectangular_cell()
        {
            var g = Panel();
            AssertRect(20, 40, 92, 34, g.SlotRect(0));
            AssertRect(116, 40, 92, 34, g.SlotRect(1));    // x += 92 + 4
            AssertRect(20, 78, 92, 34, g.SlotRect(2));     // wraps: y += 34 + 4
        }

        [Fact]
        public void ContentSize_uses_width_across_and_height_down()
        {
            var g = Panel();                                    // 2 cols, 3 rows
            Assert.Equal(2 * 92 + 1 * 4, g.ContentSize.X, 3);    // 188
            Assert.Equal(3 * 34 + 2 * 4, g.ContentSize.Y, 3);    // 110
        }

        [Fact]
        public void SlotAt_hits_the_rectangular_cell()
        {
            var g = Panel();
            Assert.Equal(0, g.SlotAt(new Vector2(100, 60)));   // inside slot 0, past a square 34 would end
            Assert.Equal(-1, g.SlotAt(new Vector2(114, 60)));  // the 4-unit gap between columns
            Assert.Equal(1, g.SlotAt(new Vector2(120, 60)));
        }

        [Fact]
        public void Right_tap_in_a_slot_fires_OnSlotRightClicked()
        {
            var g = Panel();
            int fired = -1;
            g.OnSlotRightClicked = i => fired = i;
            var p = new Pointer();
            var at = new Vector2(120, 60);   // slot 1

            p.Update(Frame(at));
            g.Update(p);
            p.Update(Frame(at, MouseButton.Right));
            g.Update(p);
            Assert.Equal(-1, fired);         // nothing on the press
            Assert.Equal(-1, g.RightClickedSlot);

            p.Update(Frame(at));
            g.Update(p);
            Assert.Equal(1, fired);
            Assert.Equal(1, g.RightClickedSlot);
        }

        [Fact]
        public void Right_tap_keeps_the_press_origin_invariant()
        {
            var g = Panel();
            int fired = -1;
            g.OnSlotRightClicked = i => fired = i;
            var p = new Pointer();

            p.Update(Frame(new Vector2(60, 60)));                             // over slot 0
            g.Update(p);
            p.Update(Frame(new Vector2(60, 60), MouseButton.Right));           // press in slot 0
            g.Update(p);
            p.Update(Frame(new Vector2(160, 60)));                             // release over slot 1
            g.Update(p);

            Assert.Equal(-1, fired);
            Assert.Equal(-1, g.RightClickedSlot);
        }

        [Fact]
        public void Right_tap_does_not_fire_the_left_click_path()
        {
            var g = Panel();
            int left = -1, right = -1;
            g.OnSlotClicked = i => left = i;
            g.OnSlotRightClicked = i => right = i;
            var p = new Pointer();
            var at = new Vector2(60, 60);

            p.Update(Frame(at));
            g.Update(p);
            p.Update(Frame(at, MouseButton.Right));
            int returned = g.Update(p);
            Assert.Equal(-1, returned);
            p.Update(Frame(at));
            returned = g.Update(p);

            Assert.Equal(-1, returned);   // Update still returns the LEFT tap only
            Assert.Equal(-1, left);
            Assert.Equal(0, right);
        }

        [Fact]
        public void Right_tap_off_every_slot_reports_nothing()
        {
            var g = Panel();
            int fired = -1;
            g.OnSlotRightClicked = i => fired = i;
            var p = new Pointer();
            var gap = new Vector2(114, 60);   // the inter-column gap

            p.Update(Frame(gap));
            g.Update(p);
            p.Update(Frame(gap, MouseButton.Right));
            g.Update(p);
            p.Update(Frame(gap));
            g.Update(p);

            Assert.Equal(-1, fired);
            Assert.Equal(-1, g.RightClickedSlot);
        }

        [Fact]
        public void Right_pressed_slot_tracks_a_held_right_press_inside_its_origin_slot()
        {
            var g = Panel();
            var p = new Pointer();
            var at = new Vector2(120, 60);

            p.Update(Frame(at));
            g.Update(p);
            Assert.Equal(-1, g.RightPressedSlot);

            p.Update(Frame(at, MouseButton.Right));
            g.Update(p);
            Assert.Equal(1, g.RightPressedSlot);

            p.Update(Frame(at));
            g.Update(p);
            Assert.Equal(-1, g.RightPressedSlot);
        }

        [Fact]
        public void Right_pressed_slot_clears_outside_and_restores_on_reentry_to_the_press_origin()
        {
            var g = Panel();
            var p = new Pointer();

            p.Update(Frame(new Vector2(60, 60)));
            p.Update(Frame(new Vector2(60, 60), MouseButton.Right));
            g.Update(p);
            Assert.Equal(0, g.RightPressedSlot);

            p.Update(Frame(new Vector2(300, 300), MouseButton.Right));
            g.Update(p);
            Assert.Equal(-1, g.RightPressedSlot);

            p.Update(Frame(new Vector2(60, 60), MouseButton.Right));
            g.Update(p);
            Assert.Equal(0, g.RightPressedSlot);
        }

        [Fact]
        public void Right_pressed_slot_stays_clear_when_the_press_began_outside()
        {
            var g = Panel();
            var p = new Pointer();

            p.Update(Frame(new Vector2(300, 300)));
            p.Update(Frame(new Vector2(300, 300), MouseButton.Right));
            p.Update(Frame(new Vector2(60, 60), MouseButton.Right));
            g.Update(p);

            Assert.Equal(-1, g.RightPressedSlot);
        }
    }
}
