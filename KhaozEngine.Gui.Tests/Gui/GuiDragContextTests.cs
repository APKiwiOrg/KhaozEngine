using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The cross-widget drag session: the arm threshold, the accept / refuse-before-release verdict, first-offer-wins
    /// z ordering, the drop commit, and the cancel + return animation. All GPU-free: the ghost is asserted through
    /// <see cref="GuiDragContext.GhostRect"/> rather than pixels.
    /// </summary>
    public class GuiDragContextTests
    {
        static readonly Rect Source = new(100, 100, 40, 40);   // the "slot" a drag is grabbed from
        static readonly Vector2 Grab = new(120, 120);          // its centre

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(held);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        // A pointer already holding a press that began at `Grab` and has since moved to `to`.
        Pointer Held(Vector2 to)
        {
            var p = new Pointer();
            p.Update(Frame(Grab, false));
            p.Update(Frame(Grab, true));
            p.Update(Frame(to, true));
            return p;
        }

        static DragPayload Item(string token) => new(token, sourceId: "bag", sourceIndex: 3);

        // Arm a drag that has travelled well past the threshold and is now hovering `at`.
        (GuiDragContext Drag, Pointer Pointer) Dragging(Vector2 at)
        {
            var drag = new GuiDragContext();
            Pointer p = Held(at);
            Assert.True(drag.ShouldBeginDrag(p, Source));
            Assert.True(drag.Begin(p, Item("potion"), Source));
            drag.BeginFrame(p, 0.016f);
            return (drag, p);
        }

        [Fact]
        public void A_press_below_the_threshold_is_not_a_drag()
        {
            var drag = new GuiDragContext();
            Pointer p = Held(new Vector2(Grab.X + 3, Grab.Y));   // 3px, threshold is 6
            Assert.False(drag.ShouldBeginDrag(p, Source));

            p = Held(new Vector2(Grab.X + 9, Grab.Y));           // clear of it
            Assert.True(drag.ShouldBeginDrag(p, Source));
        }

        [Fact]
        public void The_drag_keeps_its_grip_after_the_pointer_leaves_the_source_rect()
        {
            // The whole point: IsPressingIn would be false out here, so a per-frame containment test loses the
            // origin the moment the cursor crosses the slot edge. IsDragStartIn (press-origin) does not.
            var drag = new GuiDragContext();
            Pointer p = Held(new Vector2(600, 400));
            Assert.False(p.IsPressingIn(Source));
            Assert.True(drag.ShouldBeginDrag(p, Source));
        }

        [Fact]
        public void Begin_consumes_the_gesture_so_the_release_cannot_also_tap()
        {
            var drag = new GuiDragContext();
            Pointer p = Held(new Vector2(300, 300));
            drag.Begin(p, Item("sword"), Source);

            Assert.True(p.IsConsumed);
            p.Update(Frame(new Vector2(300, 300), false));       // release over a widget below
            Assert.False(p.IsTapIn(new Rect(200, 200, 200, 200)));
        }

        [Fact]
        public void A_second_Begin_while_dragging_is_refused()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));
            Assert.False(drag.Begin(p, Item("second"), Source));
            Assert.Equal("potion", drag.Payload.Token);
        }

        [Fact]
        public void The_payload_carries_the_opaque_token_and_the_source_identity()
        {
            (GuiDragContext drag, _) = Dragging(new Vector2(300, 300));
            Assert.Equal("potion", drag.Payload.Token);
            Assert.Equal("bag", drag.Payload.SourceId);
            Assert.Equal(3, drag.Payload.SourceIndex);
        }

        [Fact]
        public void A_refusing_target_shows_the_reject_wash_before_the_release_and_never_commits()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));

            Assert.False(drag.OfferTarget("anvil", 0, accepted: false));
            Assert.True(drag.IsOverTarget);
            Assert.False(drag.IsOverAcceptingTarget);
            Assert.True(drag.ShowRejectOverlay);          // the refusal is visible while the button is still down

            p.Update(Frame(new Vector2(300, 300), false));   // let go over it
            drag.BeginFrame(p, 0.016f);
            Assert.False(drag.OfferTarget("anvil", 0, accepted: false));
            drag.EndFrame();

            Assert.False(drag.WasDropped);
            Assert.True(drag.WasCancelled);
            Assert.Equal("potion", drag.CancelledPayload.Token);
        }

        [Fact]
        public void An_accepting_target_commits_on_release_and_reports_the_drop()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));
            DragDropResult? fired = null;
            drag.OnDropped = d => fired = d;

            Assert.False(drag.OfferTarget("anvil", 7, accepted: true));   // hovering, not released yet
            Assert.True(drag.IsOverAcceptingTarget);
            Assert.False(drag.ShowRejectOverlay);

            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);
            Assert.True(drag.OfferTarget("anvil", 7, accepted: true));
            drag.EndFrame();

            Assert.True(drag.WasDropped);
            Assert.False(drag.WasCancelled);
            Assert.False(drag.IsDragging);
            Assert.False(drag.IsReturning);              // a committed drop has no return tail
            Assert.Equal("anvil", drag.LastDrop.TargetId);
            Assert.Equal(7, drag.LastDrop.TargetIndex);
            Assert.Equal("potion", drag.LastDrop.Payload.Token);
            Assert.Equal("anvil", fired!.Value.TargetId);
        }

        [Fact]
        public void The_first_offer_of_the_frame_wins_even_when_it_refuses()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));
            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);

            Assert.False(drag.OfferTarget("overlay", 0, accepted: false));   // topmost widget refuses
            Assert.False(drag.OfferTarget("grid", 1, accepted: true));       // the one underneath must not steal it
            drag.EndFrame();

            Assert.False(drag.WasDropped);
            Assert.True(drag.WasCancelled);
            Assert.Equal("overlay", drag.HoveredTargetId);
            Assert.Equal(0, drag.HoveredTargetIndex);
        }

        [Fact]
        public void Releasing_over_nothing_cancels_and_fires_OnCancelled()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(700, 500));
            DragPayload? cancelled = null;
            drag.OnCancelled = pay => cancelled = pay;

            p.Update(Frame(new Vector2(700, 500), false));
            drag.BeginFrame(p, 0.016f);
            drag.EndFrame();                              // no target offered at all

            Assert.True(drag.WasCancelled);
            Assert.False(drag.IsDragging);
            Assert.Equal("potion", cancelled!.Value.Token);
        }

        [Fact]
        public void Cancel_abandons_a_live_drag_and_is_a_no_op_afterwards()
        {
            (GuiDragContext drag, _) = Dragging(new Vector2(300, 300));
            drag.Cancel();
            Assert.False(drag.IsDragging);
            Assert.True(drag.WasCancelled);

            int fired = 0;
            drag.OnCancelled = _ => fired++;
            drag.Cancel();                                // second call must not throw or re-fire
            Assert.False(drag.IsDragging);
            Assert.Equal(0, fired);
        }

        [Fact]
        public void OfferTargetIn_hit_tests_the_rect_for_you()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));
            var trash = new Rect(280, 280, 60, 60);
            var elsewhere = new Rect(0, 0, 20, 20);

            Assert.False(drag.OfferTargetIn(elsewhere, "trash", accepted: true));   // pointer is not over it
            Assert.False(drag.IsOverTarget);

            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);
            Assert.True(drag.OfferTargetIn(trash, "trash", accepted: true));
            Assert.Equal("trash", drag.LastDrop.TargetId);
            Assert.Equal(-1, drag.LastDrop.TargetIndex);
        }

        [Fact]
        public void The_ghost_is_a_source_sized_rect_centred_on_the_pointer()
        {
            (GuiDragContext drag, _) = Dragging(new Vector2(300, 200));
            Rect g = drag.GhostRect;
            Assert.Equal(280f, g.X, 3);       // 300 - 40/2
            Assert.Equal(180f, g.Y, 3);
            Assert.Equal(40f, g.Width, 3);
            Assert.Equal(40f, g.Height, 3);

            drag.GhostScale = 1.5f;
            g = drag.GhostRect;
            Assert.Equal(60f, g.Width, 3);
            Assert.Equal(270f, g.X, 3);       // still centred
        }

        [Fact]
        public void A_cancelled_drag_flies_the_ghost_back_to_its_source_and_then_stops()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(500, 400));
            drag.ReturnDuration = 0.2f;

            p.Update(Frame(new Vector2(500, 400), false));
            drag.BeginFrame(p, 0.016f);
            drag.EndFrame();

            Assert.True(drag.IsReturning);
            Assert.True(drag.IsActive);
            Assert.False(drag.IsDragging);                // the drag is over; the return is only a visual
            float startX = drag.GhostRect.X;

            drag.BeginFrame(p, 0.1f);                     // halfway
            Assert.True(drag.IsReturning);
            float midX = drag.GhostRect.X;
            Assert.True(midX < startX, "the ghost should be travelling back towards the source rect");

            drag.BeginFrame(p, 0.1f);                     // lands
            Assert.False(drag.IsReturning);
            Assert.False(drag.IsActive);
        }

        [Fact]
        public void ReturnDuration_zero_skips_the_animation_entirely()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(500, 400));
            drag.ReturnDuration = 0f;

            p.Update(Frame(new Vector2(500, 400), false));
            drag.BeginFrame(p, 0.016f);
            drag.EndFrame();

            Assert.True(drag.WasCancelled);
            Assert.False(drag.IsReturning);
            Assert.False(drag.IsActive);
        }

        [Fact]
        public void BeginFrame_clears_the_previous_frames_result_and_target_state()
        {
            (GuiDragContext drag, Pointer p) = Dragging(new Vector2(300, 300));
            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);
            Assert.True(drag.OfferTarget("anvil", 7, accepted: true));
            Assert.True(drag.WasDropped);

            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);
            Assert.False(drag.WasDropped);
            Assert.False(drag.WasCancelled);
            Assert.False(drag.IsOverTarget);
            Assert.Null(drag.HoveredTargetId);
            Assert.Equal(-1, drag.HoveredTargetIndex);
        }

        [Fact]
        public void Offers_are_inert_while_nothing_is_being_dragged()
        {
            var drag = new GuiDragContext();
            var p = new Pointer();
            p.Update(Frame(new Vector2(300, 300), false));
            drag.BeginFrame(p, 0.016f);

            Assert.False(drag.OfferTarget("anvil", 0, accepted: true));
            Assert.False(drag.IsOverTarget);
            Assert.False(drag.ShowRejectOverlay);         // no drag, nothing to refuse
            drag.EndFrame();
            Assert.False(drag.WasCancelled);
        }

        [Fact]
        public void WithGhost_replaces_only_the_painter()
        {
            DragPayload p = Item("potion");
            Assert.Null(p.Ghost);
            DragPayload withGhost = p.WithGhost((_, _, _, _) => { });
            Assert.NotNull(withGhost.Ghost);
            Assert.Equal("potion", withGhost.Token);
            Assert.Equal("bag", withGhost.SourceId);
            Assert.Equal(3, withGhost.SourceIndex);
        }
    }
}
