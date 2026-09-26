using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class SlotGridVisibleBoundsTests
    {
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 position, bool leftDown = false, bool rightDown = false)
        {
            var held = new HashSet<MouseButton>();
            if (leftDown) held.Add(MouseButton.Left);
            if (rightDown) held.Add(MouseButton.Right);
            var (pressed, released) = _mouse.Advance(held);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, 960, 540, mouseReleased: released);
        }

        static SlotGrid Grid(int count = 2) => new(new Rect(100, 100, 0, 0), count, columns: 2)
        {
            SlotSize = 40f,
            Spacing = 4f,
        };

        static int[] CandidateIndexes(SlotGrid grid)
        {
            var indexes = new List<int>();
            foreach (var candidate in grid.VisibleSlotCandidates()) indexes.Add(candidate.Index);
            return indexes.ToArray();
        }

        [Fact]
        public void Visible_slot_candidates_cull_a_large_grid_and_keep_partial_edge_slots()
        {
            var grid = new SlotGrid(new Rect(100, 100, 0, 0), count: 1000, columns: 10)
            {
                SlotSize = 40f,
                Spacing = 4f,
                VisibleBounds = new Rect(160, 160, 56, 56),
            };

            Assert.Equal(new[] { 11, 12, 21, 22 }, CandidateIndexes(grid));
        }

        [Fact]
        public void Null_visible_bounds_visits_every_slot_candidate()
        {
            var grid = new SlotGrid(new Rect(100, 100, 0, 0), count: 1000, columns: 10)
            {
                SlotSize = 40f,
                Spacing = 4f,
            };

            Assert.Equal(Enumerable.Range(0, 1000), CandidateIndexes(grid));
        }

        [Fact]
        public void Null_visible_bounds_keeps_full_hit_testing_and_reservation()
        {
            SlotGrid grid = Grid();
            var pointer = new Pointer();
            pointer.Update(Frame(new Vector2(164, 120)));

            grid.Update(pointer);

            Assert.Null(grid.VisibleBounds);
            Assert.Equal(1, grid.SlotAt(new Vector2(164, 120)));
            Assert.Equal(1, grid.HoveredSlot);
            Assert.True(pointer.IsBlocked(new Vector2(164, 120)));
        }

        [Fact]
        public void Visible_bounds_limits_slot_lookup_to_the_visible_part_of_a_cell()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 105, 20, 30);

            Assert.Equal(0, grid.SlotAt(new Vector2(125, 120)));
            Assert.Equal(-1, grid.SlotAt(new Vector2(110, 120)));
            Assert.Equal(-1, grid.SlotAt(new Vector2(125, 102)));
        }

        [Fact]
        public void Visible_bounds_limits_hover_press_and_press_origin_states()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 105, 20, 30);
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(110, 120)));
            grid.Update(pointer);
            Assert.Equal(-1, grid.HoveredSlot);

            pointer.Update(Frame(new Vector2(110, 120), leftDown: true));
            grid.Update(pointer);
            Assert.Equal(-1, grid.PressedSlot);
            Assert.Equal(-1, grid.PressOriginSlot);
        }

        [Fact]
        public void Visible_bounds_preserves_the_press_origin_invariant_for_a_partial_cell()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 105, 20, 30);
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(125, 120)));
            pointer.Update(Frame(new Vector2(125, 120), leftDown: true));
            pointer.Update(Frame(new Vector2(110, 120)));

            Assert.Equal(-1, grid.Update(pointer));
        }

        [Fact]
        public void A_press_hidden_by_visible_bounds_cannot_release_into_a_partial_cell()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 105, 20, 30);
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(110, 120)));
            pointer.Update(Frame(new Vector2(110, 120), leftDown: true));
            pointer.Update(Frame(new Vector2(125, 120)));

            Assert.Equal(-1, grid.Update(pointer));
        }

        [Fact]
        public void Visible_bounds_clips_right_taps()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 105, 20, 30);
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(110, 120)));
            pointer.Update(Frame(new Vector2(110, 120), rightDown: true));
            pointer.Update(Frame(new Vector2(110, 120)));
            grid.Update(pointer);

            Assert.Equal(-1, grid.RightClickedSlot);
        }

        [Fact]
        public void Visible_bounds_reserves_only_the_visible_content_intersection()
        {
            SlotGrid grid = Grid();
            grid.VisibleBounds = new Rect(120, 105, 30, 30);
            var pointer = new Pointer();
            pointer.Update(Frame(Vector2.Zero));

            grid.Update(pointer);

            Assert.True(pointer.IsBlocked(new Vector2(125, 120)));
            Assert.False(pointer.IsBlocked(new Vector2(110, 120)));
            Assert.False(pointer.IsBlocked(new Vector2(155, 120)));
            Assert.False(pointer.IsBlocked(new Vector2(125, 140)));
        }

        [Fact]
        public void A_hidden_press_cannot_start_a_drag()
        {
            SlotGrid grid = Grid(count: 1);
            grid.VisibleBounds = new Rect(120, 100, 20, 40);
            grid.BeginDragPayload = i => new DragPayload("item", grid, i);
            var drag = new GuiDragContext();
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(110, 120)));
            pointer.Update(Frame(new Vector2(110, 120), leftDown: true));
            pointer.Update(Frame(new Vector2(200, 200), leftDown: true));
            drag.BeginFrame(pointer, 0.016f);
            grid.Update(pointer, drag);
            drag.EndFrame();

            Assert.False(drag.IsDragging);
        }

        [Fact]
        public void A_hidden_slot_cannot_be_offered_as_a_drop_target()
        {
            SlotGrid grid = Grid();
            grid.VisibleBounds = new Rect(100, 100, 40, 40);
            grid.BeginDragPayload = i => new DragPayload("item", grid, i);
            var drag = new GuiDragContext();
            var pointer = new Pointer();

            pointer.Update(Frame(new Vector2(120, 120)));
            pointer.Update(Frame(new Vector2(120, 120), leftDown: true));
            pointer.Update(Frame(new Vector2(164, 120), leftDown: true));
            drag.BeginFrame(pointer, 0.016f);
            grid.Update(pointer, drag);
            drag.EndFrame();

            Assert.True(drag.IsDragging);
            Assert.Equal(-1, grid.DropTargetSlot);
        }
    }
}
