using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Windowing
{
    public class PointerTests
    {
        static readonly Vector2 Inside = new(150, 140);
        static readonly Vector2 Outside = new(10, 10);
        static readonly Rect Box = new(100, 100, 200, 80);

        // Genuinely outside the 960x540 client area Frame uses below, unlike Inside/Outside above,
        // which both sit inside it (the OS-capture drag bug, KhaozEngine#90, needs a real
        // out-of-client-area coordinate to reproduce). HugeBox is sized to contain Inside,
        // FarBeyondEdge, NegativeBeyondEdge, and Reentry all at once, so the bounds-based queries
        // (IsPressingIn and friends) can be asserted against one widget rect across a drag that
        // strays past the window edge.
        static readonly Vector2 FarBeyondEdge = new(1200, 700);
        static readonly Vector2 NegativeBeyondEdge = new(-50, -50);
        static readonly Vector2 Reentry = new(200, 160);
        static readonly Rect HugeBox = new(-5000, -5000, 10000, 10000);

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown) => Frame(pos, leftDown, true);

        InputState Frame(Vector2 pos, bool leftDown, bool focused)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return Frame(pos, down, focused);
        }

        // A frame whose snapshot reports a press for a button that is ALREADY up again: a tap whose press and
        // release both queued inside one frame, which is what a frame hitch or the background throttle produces.
        // Deliberately hand-built rather than derived, since no sequence of held-button frames can express it.
        static InputState TapFrame(Vector2 pos, MouseButton button = MouseButton.Left, int width = 960) => new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton> { button },
            pos, Vector2.Zero, 0, width, 540,
            mouseReleased: new HashSet<MouseButton> { button });

        InputState Frame(Vector2 pos, IReadOnlySet<MouseButton> held, bool focused = true)
        {
            var (edgePressed, edgeReleased) = _mouse.Advance(held);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, edgePressed, pos, Vector2.Zero, 0, 960, 540, windowFocused: focused,
                mouseReleased: edgeReleased);
        }

        [Fact]
        public void WindowFocused_defaults_true_and_reflects_the_input_snapshot()
        {
            var p = new Pointer();
            Assert.True(p.WindowFocused);                  // fresh pointer assumes focused

            p.Update(Frame(Inside, false, focused: false));
            Assert.False(p.WindowFocused);

            p.Update(Frame(Inside, false, focused: true));
            Assert.True(p.WindowFocused);
        }

        [Fact]
        public void IsHoveringIn_is_suppressed_while_the_window_is_unfocused()
        {
            var p = new Pointer();

            p.Update(Frame(Inside, false, focused: false)); // cursor over the box, window NOT focused
            Assert.False(p.IsHoveringIn(Box));              // no hover while unfocused

            p.Update(Frame(Inside, false, focused: true));  // focus returns
            Assert.True(p.IsHoveringIn(Box));               // hover resumes
        }

        [Fact]
        public void Press_origin_queries_stay_live_while_unfocused()  // focus gates HOVER only, not the click-through invariant
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false, focused: false));
            p.Update(Frame(Inside, true, focused: false));  // press inside while unfocused
            Assert.True(p.IsPressingIn(Box));               // press-origin invariant unaffected by focus
            Assert.True(p.IsDragStartIn(Box));
            p.Update(Frame(Inside, false, focused: false)); // release inside while unfocused
            Assert.True(p.IsTapIn(Box));                    // tap still resolves; focus does not neutralize it
        }

        [Fact]
        public void Tap_in_design_space_under_a_scaled_letterboxed_viewport()
        {
            // 960x540 design Fit into a 1920x1200 window -> scale 2, 60px top/bottom bars.
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);
            // Box is in DESIGN space; the screen click is where that design point lands on the window.
            Vector2 screen = vp.DesignToScreen(new Vector2(150, 140));   // -> (300, 340)
            Assert.Equal(new Vector2(300, 340), screen);

            var mouse = new MouseFrames();
            InputState Win(bool down)
            {
                var b = new HashSet<MouseButton>();
                if (down) b.Add(MouseButton.Left);
                var (edgePressed, edgeReleased) = mouse.Advance(b);
                return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                    b, edgePressed, screen, Vector2.Zero, 0, 1920, 1200, mouseReleased: edgeReleased);
            }

            var p = new Pointer();
            p.Update(Win(false), vp);
            p.Update(Win(true), vp);
            p.Update(Win(false), vp);

            Assert.True(p.IsTapIn(Box));                       // design-space hit-test lines up
            Assert.Equal(new Vector2(150, 140), p.Position);   // pointer reported in design space
        }

        [Fact]
        public void Tap_inside_fires_on_release()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));   // up
            p.Update(Frame(Inside, true));    // press inside -> press-origin = inside
            Assert.False(p.IsTapIn(Box));     // not yet (still down)
            p.Update(Frame(Inside, false));   // release inside
            Assert.True(p.IsTapIn(Box));
        }

        [Fact]
        public void Press_outside_release_inside_is_not_a_tap()  // the click-through invariant
        {
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(Frame(Outside, true));   // press began OUTSIDE the box
            p.Update(Frame(Inside, true));    // dragged inside, still down
            p.Update(Frame(Inside, false));   // released inside
            Assert.False(p.IsTapIn(Box));     // press-origin was outside -> no tap
        }

        [Fact]
        public void Press_inside_release_outside_is_not_a_tap_and_is_released_outside()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));    // press inside
            p.Update(Frame(Outside, false));  // release outside
            Assert.False(p.IsTapIn(Box));
            Assert.True(p.IsReleasedOutside(Box));
        }

        [Fact]
        public void IsPressingIn_while_held_inside()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            Assert.True(p.IsPressingIn(Box));
            Assert.True(p.IsDown);
            Assert.True(p.IsJustPressed);
        }

        [Fact]
        public void IsDragStartIn_true_while_a_press_that_began_inside_is_held()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            Assert.False(p.IsDragStartIn(Box));   // not pressed yet
            p.Update(Frame(Inside, true));        // press began inside -> grabbed
            Assert.True(p.IsDragStartIn(Box));
            p.Update(Frame(Outside, true));       // cursor strays out, still down -> keeps the grab
            Assert.True(p.IsDragStartIn(Box));
            p.Update(Frame(Outside, false));      // released -> grab ends
            Assert.False(p.IsDragStartIn(Box));
        }

        [Fact]
        public void IsDragStartIn_false_when_the_press_began_outside()  // press-origin invariant
        {
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(Frame(Outside, true));       // press began OUTSIDE the box
            p.Update(Frame(Inside, true));        // dragged inside, still down
            Assert.False(p.IsDragStartIn(Box));   // press-origin was outside -> never grabs
        }

        [Fact]
        public void Region_blocking()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.BlockRegion(Box);
            Assert.True(p.IsBlocked(Inside));
            Assert.False(p.IsBlocked(Outside));
            p.Update(Frame(Inside, false));   // cleared each Update
            Assert.False(p.IsBlocked(Inside));
        }

        [Fact]
        public void ConsumeGesture_suppresses_the_tap_for_the_rest_of_the_gesture()
        {
            // The campaign-map bug timing: on the release frame IsTapIn would fire, but a consume triggered
            // earlier that frame (a scene push) must suppress it so a freshly-drawn widget ignores the gesture.
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));    // press inside
            p.Update(Frame(Inside, false));   // release inside -> a complete tap
            Assert.True(p.IsTapIn(Box));      // ...which IsTapIn honours
            p.ConsumeGesture();               // gesture claimed (e.g. it pushed an overlay)
            Assert.True(p.IsConsumed);
            Assert.False(p.IsTapIn(Box));     // suppressed for the rest of this gesture
        }

        [Fact]
        public void A_fresh_press_clears_a_consumed_gesture()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, true));    // press
            p.ConsumeGesture();
            p.Update(Frame(Inside, false));   // release of the consumed gesture
            Assert.True(p.IsConsumed);
            Assert.False(p.IsTapIn(Box));     // still suppressed

            p.Update(Frame(Inside, true));    // a brand-new press starts a fresh, unconsumed gesture
            Assert.False(p.IsConsumed);
            p.Update(Frame(Inside, false));   // release inside
            Assert.True(p.IsTapIn(Box));      // taps normally again
        }

        [Fact]
        public void ConsumeGesture_does_not_block_a_held_drag_grab()
        {
            // Consuming the tap must not kill an in-progress slider/drag grab (press-origin based), only the tap.
            var p = new Pointer();
            p.Update(Frame(Inside, true));    // press began inside -> grabbed
            p.ConsumeGesture();
            Assert.True(p.IsDragStartIn(Box));
            Assert.True(p.IsPressingIn(Box));
        }

        // --- OS-capture drag fixes (KhaozEngine#90): a held button must stay latched once a valid
        // in-window press opens it, regardless of where the cursor strays afterward, and a press
        // that never validly opened the latch must stay ignored no matter where the cursor wanders
        // while still held. ---

        [Fact]
        public void Drag_beyond_the_client_area_stays_down_and_keeps_its_original_press_origin()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));          // press begins in-window
            Assert.True(p.IsJustPressed);
            Assert.Equal(Inside, p.PressOrigin);

            p.Update(Frame(FarBeyondEdge, true));   // OS keeps delivering coords past the client area
            Assert.True(p.IsDown);
            Assert.False(p.IsJustReleased);
            Assert.False(p.IsJustPressed);
            Assert.Equal(Inside, p.PressOrigin);    // origin untouched

            Assert.True(p.IsDraggingIn(HugeBox));
            Assert.True(p.IsDragStartIn(HugeBox));
            Assert.True(p.IsPressingIn(HugeBox));
            Assert.Equal(FarBeyondEdge - Inside, p.GetDragDelta(HugeBox));
        }

        [Fact]
        public void Reentering_the_window_mid_drag_does_not_refire_just_pressed_or_move_the_press_origin()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));          // press begins in-window at Inside
            p.Update(Frame(FarBeyondEdge, true));   // strays outside, still held
            p.Update(Frame(Reentry, true));         // back inside at a DIFFERENT point, still held

            Assert.True(p.IsDown);
            Assert.False(p.IsJustPressed);          // re-entry must not look like a fresh press
            Assert.Equal(Inside, p.PressOrigin);    // origin stays the ORIGINAL point, not Reentry
        }

        [Fact]
        public void Releasing_the_button_while_outside_the_client_area_fires_exactly_one_just_released()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));          // press
            p.Update(Frame(FarBeyondEdge, true));   // strays outside, still held
            Assert.False(p.IsJustReleased);

            p.Update(Frame(FarBeyondEdge, false));  // released while still outside
            Assert.True(p.IsJustReleased);
            Assert.False(p.IsDown);

            p.Update(Frame(FarBeyondEdge, false));  // stays released
            Assert.False(p.IsJustReleased);         // the release is a one-frame pulse, not sustained
        }

        [Fact]
        public void Press_that_begins_outside_the_client_area_is_ignored_while_it_stays_outside()
        {
            var p = new Pointer();
            p.Update(Frame(FarBeyondEdge, false));
            p.Update(Frame(FarBeyondEdge, true));   // press begins OUTSIDE the client area
            Assert.False(p.IsDown);
            Assert.False(p.IsJustPressed);

            p.Update(Frame(FarBeyondEdge, true));   // still held, still outside -> still ignored
            Assert.False(p.IsDown);
            Assert.False(p.IsJustPressed);
        }

        [Fact]
        public void Press_that_begins_outside_latches_at_entry_if_still_held_unchanged_pre_existing_behaviour()
        {
            // Not a regression: this is the pre-fix behaviour too, for a press that begins outside
            // the client area and is dragged in while still held (IsDown && inWindow flips true the
            // instant both are true). KhaozEngine#90 is only about a press that VALIDLY begins
            // in-window and later strays outside, see Drag_beyond_the_client_area_stays_down...
            var p = new Pointer();
            p.Update(Frame(FarBeyondEdge, false));
            p.Update(Frame(FarBeyondEdge, true));   // press begins OUTSIDE the client area
            p.Update(Frame(Inside, true));          // dragged inside, still held -> latches here
            Assert.True(p.IsDown);
            Assert.True(p.IsJustPressed);
            Assert.Equal(Inside, p.PressOrigin);    // origin is the ENTRY point, not the outside start
        }

        [Fact]
        public void Negative_coordinates_dragging_outside_stays_down_and_keeps_the_press_origin()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            p.Update(Frame(NegativeBeyondEdge, true));
            Assert.True(p.IsDown);
            Assert.False(p.IsJustReleased);
            Assert.Equal(Inside, p.PressOrigin);
            Assert.True(p.IsDraggingIn(HugeBox));
        }

        [Fact]
        public void Negative_coordinates_a_press_that_begins_there_is_still_ignored()
        {
            var p = new Pointer();
            p.Update(Frame(NegativeBeyondEdge, false));
            p.Update(Frame(NegativeBeyondEdge, true));
            Assert.False(p.IsDown);
            Assert.False(p.IsJustPressed);
        }

        [Fact]
        public void Middle_and_right_buttons_latch_the_same_way_as_left_when_the_cursor_strays_outside()
        {
            var down = new HashSet<MouseButton> { MouseButton.Middle, MouseButton.Right };
            var none = new HashSet<MouseButton>();

            // The class-level builder, which derives the press/release edges across this sequence.
            InputState WithButtons(Vector2 pos, IReadOnlySet<MouseButton> heldButtons) => Frame(pos, heldButtons);

            var p = new Pointer();
            p.Update(WithButtons(Inside, none));
            p.Update(WithButtons(Inside, down));               // fresh press, in-window
            Assert.True(p.IsMiddleDown);
            Assert.True(p.IsRightDown);

            p.Update(WithButtons(FarBeyondEdge, down));        // strays outside, still held
            Assert.True(p.IsMiddleDown);
            Assert.True(p.IsRightDown);
            Assert.False(p.IsMiddleJustReleased);
            Assert.False(p.IsRightJustReleased);

            p.Update(WithButtons(FarBeyondEdge, none));        // released outside
            Assert.True(p.IsMiddleJustReleased);
            Assert.True(p.IsRightJustReleased);
        }

        // --- same-frame taps (#300) ---

        [Fact]
        public void A_tap_whose_press_and_release_land_in_one_frame_still_registers()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));      // idle over the box, button up
            p.Update(TapFrame(Inside));          // press AND release inside this one frame

            // The button is up in the snapshot, so the IsDown transition sees nothing at all: without reading
            // the press edge the whole gesture is invisible and IsTapIn never fires.
            Assert.False(p.IsDown);
            Assert.False(p.IsJustPressed);
            Assert.True(p.IsJustReleased);
            Assert.Equal(Inside, p.PressOrigin);
            Assert.True(p.IsTapIn(Box));
        }

        [Fact]
        public void A_same_frame_tap_keeps_the_press_origin_invariant()
        {
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(TapFrame(Outside));         // the tap happened outside Box

            Assert.True(p.IsJustReleased);
            Assert.False(p.IsTapIn(Box));        // press origin AND release are both outside it
            Assert.True(p.IsTapFromTo(new Rect(0, 0, 50, 50), new Rect(0, 0, 50, 50)));
        }

        [Fact]
        public void A_same_frame_tap_lasts_exactly_one_frame()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(TapFrame(Inside));
            Assert.True(p.IsTapIn(Box));

            p.Update(Frame(Inside, false));      // the next idle frame
            Assert.False(p.IsJustReleased);
            Assert.False(p.IsTapIn(Box));
        }

        [Fact]
        public void A_same_frame_tap_starts_a_fresh_unconsumed_gesture()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            p.ConsumeGesture();                  // an earlier gesture was claimed
            p.Update(Frame(Inside, false));
            Assert.False(p.IsTapIn(Box));        // consumed, as it should be

            p.Update(TapFrame(Inside));
            Assert.False(p.IsConsumed);          // the tap is its own gesture
            Assert.True(p.IsTapIn(Box));
        }

        [Fact]
        public void The_right_button_gets_the_same_frame_tap_too()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(TapFrame(Inside, MouseButton.Right));

            Assert.True(p.IsRightJustReleased);
            Assert.Equal(Inside, p.RightPressOrigin);
            Assert.True(p.IsRightTapIn(Box));
            Assert.False(p.IsTapIn(Box));        // the left gesture is untouched
        }

        [Fact]
        public void A_same_frame_tap_outside_the_client_area_is_ignored()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(TapFrame(FarBeyondEdge));   // beyond the 960x540 client area

            Assert.False(p.IsJustReleased);
            Assert.False(p.IsTapIn(HugeBox));
        }

        [Fact]
        public void A_producer_that_never_fills_MousePressed_reads_exactly_as_before()
        {
            // The contract Pointer places on InputState is unchanged: a replay or a synthesized headless frame
            // that only ever fills MouseDown still drives every press-origin query the way it always did. All
            // it cannot do is express a same-frame tap, which no sequence of held-button frames can.
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            Assert.False(p.IsJustReleased);

            p.Update(Frame(Inside, true));
            Assert.True(p.IsJustPressed);
            Assert.False(p.IsTapIn(Box));

            p.Update(Frame(Inside, false));
            Assert.True(p.IsJustReleased);
            Assert.True(p.IsTapIn(Box));
        }

        [Fact]
        public void A_same_frame_tap_reaches_IsTapIn_through_InputManager()
        {
            var input = new InputManager();
            input.Update(Frame(Inside, false));
            input.Update(TapFrame(Inside));

            Assert.True(input.IsPointerJustReleased);
            Assert.True(input.Pointer.IsTapIn(Box));
        }
    }
}
