using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="SlotGrid"/> as a drag source and a drop target over a <see cref="GuiDragContext"/>, including the
    /// press-origin slot that survives the pointer leaving its rect (which <see cref="SlotGrid.PressedSlot"/> cannot).
    /// </summary>
    public class SlotGridDragTests
    {
        // 5x6 inventory: 30 slots, 5 columns, slot 40, spacing 4, origin (100,100). Slot 0 centre is (120,120),
        // slot 1 centre (164,120), slot 6 centre (164,164).
        static SlotGrid Grid() => new(new Rect(100, 100, 0, 0), count: 30, columns: 5) { SlotSize = 40f, Spacing = 4f };

        static readonly Vector2 Slot0 = new(120, 120);
        static readonly Vector2 Slot1 = new(164, 120);
        static readonly Vector2 OffGrid = new(700, 480);

        static InputState Frame(Vector2 pos, bool down) =>
            new(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
                new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);

        static SlotGrid Draggable(string token = "potion")
        {
            SlotGrid g = Grid();
            g.BeginDragPayload = i => new DragPayload(token, sourceId: g, sourceIndex: i);
            return g;
        }

        [Fact]
        public void PressOriginSlot_survives_the_pointer_leaving_the_slot_while_PressedSlot_does_not()
        {
            SlotGrid g = Grid();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            g.Update(p);
            Assert.Equal(0, g.PressedSlot);
            Assert.Equal(0, g.PressOriginSlot);

            p.Update(Frame(OffGrid, true));   // dragged right off the grid, button still held
            g.Update(p);
            Assert.Equal(-1, g.PressedSlot);        // the old signal loses the origin here
            Assert.Equal(0, g.PressOriginSlot);     // the drag's origin is still slot 0

            p.Update(Frame(OffGrid, false));  // released
            g.Update(p);
            Assert.Equal(-1, g.PressOriginSlot);
        }

        [Fact]
        public void A_held_press_past_the_threshold_grabs_the_payload_from_the_press_origin_slot()
        {
            SlotGrid g = Draggable();
            var drag = new GuiDragContext();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();
            Assert.False(drag.IsDragging);           // no travel yet, still a tap candidate

            p.Update(Frame(new Vector2(300, 300), true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.True(drag.IsDragging);
            Assert.Equal("potion", drag.Payload.Token);
            Assert.Equal(0, drag.Payload.SourceIndex);
            Assert.Same(g, drag.Payload.SourceId);
            Assert.Equal(0, g.DraggingSlot);
        }

        [Fact]
        public void A_null_payload_makes_that_slot_non_draggable()
        {
            SlotGrid g = Grid();
            g.BeginDragPayload = _ => null;          // nothing here to pick up
            var drag = new GuiDragContext();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(new Vector2(300, 300), true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.False(drag.IsDragging);
            Assert.Equal(-1, g.DraggingSlot);
        }

        [Fact]
        public void A_grid_with_no_hooks_never_takes_part_in_a_drag()
        {
            SlotGrid g = Grid();                      // no BeginDragPayload, no CanAcceptDrop
            var drag = new GuiDragContext();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(new Vector2(300, 300), true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.False(drag.IsDragging);
        }

        [Fact]
        public void Update_without_a_drag_context_behaves_exactly_as_before()
        {
            SlotGrid g = Draggable();
            var p = new Pointer();
            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(Slot0, false));

            Assert.Equal(0, g.Update(p));             // the plain tap still fires
            Assert.Equal(-1, g.DraggingSlot);
            Assert.Equal(-1, g.DroppedSlot);
        }

        // Carry a drag grabbed out of slot 0 and hover `to`. Returns the grid, the context and the pointer.
        static (SlotGrid Grid, GuiDragContext Drag, Pointer Pointer) Carrying(Vector2 to)
        {
            SlotGrid g = Draggable();
            var drag = new GuiDragContext();
            var p = new Pointer();
            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(to, true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();
            Assert.True(drag.IsDragging);
            return (g, drag, p);
        }

        [Fact]
        public void The_hovered_slot_is_offered_as_a_drop_target_every_frame_with_its_verdict()
        {
            (SlotGrid g, GuiDragContext drag, Pointer p) = Carrying(Slot1);
            g.CanAcceptDrop = (slot, _) => slot != 1;   // slot 1 refuses

            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.Equal(1, g.DropTargetSlot);
            Assert.False(g.DropTargetAccepted);
            Assert.True(drag.ShowRejectOverlay);        // refused, and the player has not let go yet
            Assert.Equal(-1, g.DroppedSlot);
        }

        [Fact]
        public void A_refused_drop_never_commits_and_cancels_instead()
        {
            (SlotGrid g, GuiDragContext drag, Pointer p) = Carrying(Slot1);
            g.CanAcceptDrop = (_, _) => false;

            p.Update(Frame(Slot1, false));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.Equal(-1, g.DroppedSlot);
            Assert.False(drag.WasDropped);
            Assert.True(drag.WasCancelled);
        }

        [Fact]
        public void An_accepted_drop_commits_on_the_release_frame()
        {
            (SlotGrid g, GuiDragContext drag, Pointer p) = Carrying(Slot1);
            var seen = new List<(int Slot, object? Token)>();
            g.OnSlotDropped = (slot, payload) => seen.Add((slot, payload.Token));

            p.Update(Frame(Slot1, false));
            drag.BeginFrame(p, 0.016f);
            int clicked = g.Update(p, drag);
            drag.EndFrame();

            Assert.Equal(1, g.DroppedSlot);
            Assert.Equal("potion", g.DroppedPayload.Token);
            Assert.Equal(0, g.DroppedPayload.SourceIndex);   // came out of slot 0
            Assert.Single(seen);
            Assert.Equal((1, (object?)"potion"), seen[0]);
            Assert.True(drag.WasDropped);
            Assert.Equal(-1, clicked);                       // the drop must not also read as a slot click
        }

        [Fact]
        public void Dropping_on_a_second_grid_carries_the_source_identity_across()
        {
            // The second grid's slot 3 spans (604,304)-(644,344), so (624,324) is its centre and is off the first grid.
            (SlotGrid from, GuiDragContext drag, Pointer p) = Carrying(new Vector2(624, 324));
            SlotGrid to = new(new Rect(560, 260, 0, 0), count: 4, columns: 2) { SlotSize = 40f, Spacing = 4f };

            p.Update(Frame(new Vector2(624, 324), false));   // release over the second grid's slot 3
            drag.BeginFrame(p, 0.016f);
            from.Update(p, drag);
            to.Update(p, drag);
            drag.EndFrame();

            Assert.Equal(-1, from.DroppedSlot);
            Assert.Equal(3, to.DroppedSlot);
            Assert.Same(from, to.DroppedPayload.SourceId);
            Assert.Equal(0, to.DroppedPayload.SourceIndex);
            Assert.Same(to, drag.LastDrop.TargetId);
        }

        [Fact]
        public void DraggingSlot_clears_once_the_drag_ends()
        {
            (SlotGrid g, GuiDragContext drag, Pointer p) = Carrying(OffGrid);
            Assert.Equal(0, g.DraggingSlot);

            p.Update(Frame(OffGrid, false));                 // released over nothing
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();
            Assert.True(drag.WasCancelled);

            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();
            Assert.Equal(-1, g.DraggingSlot);
        }

        [Fact]
        public void A_slot_with_content_gets_its_own_icon_as_the_default_ghost()
        {
            SlotGrid g = Draggable();
            g.SetContent(0, new SlotContent("potion_icon"));
            var drag = new GuiDragContext();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(new Vector2(300, 300), true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.NotNull(drag.Payload.Ghost);              // the grid filled in a ghost the game did not supply
            Assert.Equal(40f, drag.GhostRect.Width, 3);      // sized from the slot it came out of
        }

        [Fact]
        public void A_game_supplied_ghost_is_left_alone()
        {
            SlotGrid g = Grid();
            DragGhostPainter mine = (_, _, _, _) => { };
            g.BeginDragPayload = i => new DragPayload("potion", g, i, mine);
            g.SetContent(0, new SlotContent("potion_icon"));
            var drag = new GuiDragContext();
            var p = new Pointer();

            p.Update(Frame(Slot0, false));
            p.Update(Frame(Slot0, true));
            p.Update(Frame(new Vector2(300, 300), true));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            drag.EndFrame();

            Assert.Same(mine, drag.Payload.Ghost);
        }

        [Fact]
        public void Dragging_out_of_the_panel_onto_a_bare_rect_target_commits_there()
        {
            // The Ruinborne bag-destroy shape: a trash rect that is not a widget at all.
            (SlotGrid g, GuiDragContext drag, Pointer p) = Carrying(new Vector2(700, 480));
            var trash = new Rect(660, 440, 80, 80);
            object? destroyed = null;

            p.Update(Frame(new Vector2(700, 480), false));
            drag.BeginFrame(p, 0.016f);
            g.Update(p, drag);
            if (drag.OfferTargetIn(trash, "trash", accepted: true)) destroyed = drag.LastDrop.Payload.Token;
            drag.EndFrame();

            Assert.Equal("potion", destroyed);
            Assert.False(drag.WasCancelled);
            Assert.Equal(-1, g.DroppedSlot);
        }
    }
}
