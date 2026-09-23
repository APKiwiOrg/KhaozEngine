using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Showcase;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>
    /// The 2D and GUI tour's drag-and-drop page, driven headlessly through scripted <see cref="InputState"/>
    /// frames: it builds and lays out, moves an item between the grids, refuses the marked stash slot before the
    /// release, destroys on the bin, and flies a cancelled drop home. No fonts or textures are touched because
    /// nothing here draws.
    /// </summary>
    public class DragDropPageTests
    {
        // The host's page bounds at the 1280x720 design size.
        static readonly Rect Bounds = new(180, 112, 920, 592);
        const float Dt = 0.016f;

        readonly MouseFrames _mouse = new();
        readonly ScreenStack _stack = new();
        readonly DragDropPage _page = new();

        public DragDropPageTests()
        {
            _page.Load(new GuiAssets(null!, null!, null!, null!, null!), _stack);
            _page.Activated();
            Frame(new Vector2(0, 0), down: false);   // resolves the layout
        }

        void Frame(Vector2 pos, bool down, float dt = Dt)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(held);
            _stack.InputManager.Update(new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, pos, Vector2.Zero, 0, 1280, 720, mouseReleased: released));
            _page.Update(dt, receivesInput: true, Bounds, _stack.InputManager);
        }

        // Press at `from`, travel past the arm threshold, hover `to` with the button still held.
        void GrabAndHover(Vector2 from, Vector2 to)
        {
            Frame(from, down: false);
            Frame(from, down: true);
            Frame(from + new Vector2(12, 12), down: true);
            Frame(to, down: true);
        }

        static Vector2 Center(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);
        Vector2 Bag(int slot) => Center(_page.BagGrid.SlotRect(slot));
        Vector2 Stash(int slot) => Center(_page.StashGrid.SlotRect(slot));

        [Fact]
        public void The_page_builds_and_lays_out_two_grids_and_a_bin_inside_its_bounds()
        {
            Rect bag = _page.BagGrid.ContentBounds, stash = _page.StashGrid.ContentBounds, bin = _page.DestroyRect;

            Assert.Equal(DragDropPage.SlotCount, _page.BagGrid.Count);
            Assert.Equal(DragDropPage.SlotCount, _page.StashGrid.Count);
            foreach (Rect r in new[] { bag, stash, bin })
            {
                Assert.True(r.Width > 0 && r.Height > 0);
                Assert.True(r.X >= Bounds.X && r.Right <= Bounds.Right && r.Y >= Bounds.Y && r.Bottom <= Bounds.Bottom);
            }
            Assert.True(bag.Right < stash.X && stash.Right < bin.X);   // three columns, no overlap

            Assert.True(_page.BagAt(0)!.Painted);
            Assert.False(_page.BagAt(5)!.Painted);
            Assert.Null(_page.StashAt(DragDropPage.RefusedSlot));
            Assert.Equal(DragDropPage.DropEvent.None, _page.Last);
        }

        [Fact]
        public void Dragging_an_item_to_an_empty_stash_slot_moves_it()
        {
            DragDropPage.Item item = _page.BagAt(0)!;

            GrabAndHover(Bag(0), Stash(0));
            Assert.True(_page.Drag.IsDragging);
            Assert.True(_page.Drag.IsOverAcceptingTarget);
            Frame(Stash(0), down: false);

            Assert.Null(_page.BagAt(0));
            Assert.Same(item, _page.StashAt(0));
            Assert.Equal(DragDropPage.DropEvent.Moved, _page.Last);
            Assert.False(_page.Drag.IsActive);   // a committed drop has no return tail
        }

        [Fact]
        public void Dropping_on_an_occupied_slot_swaps_the_two_items()
        {
            DragDropPage.Item fromStash = _page.StashAt(2)!, inBag = _page.BagAt(0)!;

            GrabAndHover(Stash(2), Bag(0));
            Frame(Bag(0), down: false);

            Assert.Same(fromStash, _page.BagAt(0));
            Assert.Same(inBag, _page.StashAt(2));
            Assert.Equal(DragDropPage.DropEvent.Swapped, _page.Last);
        }

        [Fact]
        public void The_refused_slot_shows_the_reject_wash_before_release_and_the_item_flies_home()
        {
            DragDropPage.Item item = _page.BagAt(0)!;

            GrabAndHover(Bag(0), Stash(DragDropPage.RefusedSlot));
            Assert.True(_page.Drag.ShowRejectOverlay);
            Assert.Equal(DragDropPage.RefusedSlot, _page.StashGrid.DropTargetSlot);
            Assert.False(_page.StashGrid.DropTargetAccepted);

            Frame(Stash(DragDropPage.RefusedSlot), down: false);
            Assert.Same(item, _page.BagAt(0));
            Assert.Null(_page.StashAt(DragDropPage.RefusedSlot));
            Assert.Equal(DragDropPage.DropEvent.Refused, _page.Last);
            Assert.True(_page.Drag.IsReturning);

            for (int i = 0; i < 10; i++) Frame(Stash(DragDropPage.RefusedSlot), down: false);   // 0.16 s > 0.12 s
            Assert.False(_page.Drag.IsActive);
        }

        [Fact]
        public void A_release_over_nothing_cancels_and_the_ghost_flies_back_to_its_slot()
        {
            var nowhere = new Vector2(Bounds.X + 600, Bounds.Bottom - 40);

            GrabAndHover(Bag(1), nowhere);
            Assert.True(_page.Drag.ShowRejectOverlay);   // over nothing reads as a refusal too
            Frame(nowhere, down: false);

            Assert.Equal(DragDropPage.DropEvent.Cancelled, _page.Last);
            Assert.True(_page.Drag.IsReturning);
            Assert.NotNull(_page.BagAt(1));

            Frame(nowhere, down: false, dt: 0.06f);   // half of the default 0.12 s
            Vector2 mid = Center(_page.Drag.GhostRect);
            Assert.True(Vector2.Distance(mid, Bag(1)) < Vector2.Distance(nowhere, Bag(1)));
            Assert.True(Vector2.Distance(mid, Bag(1)) > 1f);

            Frame(nowhere, down: false, dt: 0.07f);
            Assert.False(_page.Drag.IsActive);
        }

        [Fact]
        public void The_bin_accepts_a_drop_and_removes_the_item()
        {
            GrabAndHover(Bag(1), Center(_page.DestroyRect));
            Assert.True(_page.Drag.IsOverAcceptingTarget);
            Frame(Center(_page.DestroyRect), down: false);

            Assert.Null(_page.BagAt(1));
            Assert.Equal(DragDropPage.DropEvent.Destroyed, _page.Last);
        }

        [Fact]
        public void A_hollow_item_carries_no_ghost_painter_so_the_placeholder_frame_flies()
        {
            GrabAndHover(Bag(5), Stash(0));
            Assert.Null(_page.Drag.Payload.Ghost);
            Frame(Stash(0), down: false);

            GrabAndHover(Bag(0), Stash(1));
            Assert.NotNull(_page.Drag.Payload.Ghost);
        }

        [Fact]
        public void Leaving_the_tab_mid_drag_cancels_it()
        {
            GrabAndHover(Bag(0), Stash(0));
            _page.Deactivated();

            Assert.False(_page.Drag.IsDragging);
            Assert.Equal(DragDropPage.DropEvent.Cancelled, _page.Last);
            Assert.NotNull(_page.BagAt(0));
        }
    }
}
