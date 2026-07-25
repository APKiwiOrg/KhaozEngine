using System.Numerics;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless coverage of the raw-event to snapshot state machine that used to live inside AppWindow.
    /// Nothing here needs a window, which is the point of the seam.
    /// </summary>
    public class InputAccumulatorTests
    {
        const int Width = 1920;
        const int Height = 1080;

        // A far-from-origin cursor, roughly where a 2x Retina window's centre lands in framebuffer pixels.
        static readonly Vector2 Centre = new(960, 540);

        static InputState Snap(InputAccumulator a, Vector2 cursor) => a.Snapshot(cursor, true, Width, Height);
        static InputState Snap(InputAccumulator a) => Snap(a, Centre);

        // ---- #92: the first-frame MouseDelta spike -------------------------------------------------------

        [Fact]
        public void First_snapshot_reports_a_zero_delta_even_at_a_far_from_origin_cursor()
        {
            var a = new InputAccumulator();

            InputState first = Snap(a, Centre);

            // Before the fix this was (960, 540): the raw cursor position, because _lastMouse started at the
            // origin. A mouse-look camera read that as a full-screen flick on its very first frame.
            Assert.Equal(Vector2.Zero, first.MouseDelta);
            Assert.Equal(Centre, first.MousePosition);
        }

        [Fact]
        public void Second_snapshot_reports_the_true_delta()
        {
            var a = new InputAccumulator();
            Snap(a, Centre);

            InputState second = Snap(a, new Vector2(970, 520));

            Assert.Equal(new Vector2(10, -20), second.MouseDelta);
            Assert.Equal(new Vector2(970, 520), second.MousePosition);
        }

        [Fact]
        public void A_stationary_cursor_reports_no_delta()
        {
            var a = new InputAccumulator();
            Snap(a, Centre);

            Assert.Equal(Vector2.Zero, Snap(a, Centre).MouseDelta);
        }

        [Fact]
        public void With_no_mouse_present_the_last_position_is_held_and_the_delta_is_zero()
        {
            var a = new InputAccumulator();
            Snap(a, Centre);

            InputState none = a.Snapshot(new Vector2(5, 5), false, Width, Height);

            Assert.Equal(Centre, none.MousePosition);   // the passed position is ignored when no mouse is present
            Assert.Equal(Vector2.Zero, none.MouseDelta);
        }

        [Fact]
        public void A_mouse_arriving_after_mouseless_frames_still_gets_a_zero_first_delta()
        {
            var a = new InputAccumulator();
            a.Snapshot(Vector2.Zero, false, Width, Height);
            a.Snapshot(Vector2.Zero, false, Width, Height);

            // Only a frame that actually sampled a cursor primes the delta, so the first real sample is the
            // first frame, not the third.
            Assert.Equal(Vector2.Zero, Snap(a, Centre).MouseDelta);
            Assert.Equal(new Vector2(-10, 0), Snap(a, new Vector2(950, 540)).MouseDelta);
        }

        // ---- #91: held input is never cleared on focus loss -----------------------------------------------

        [Fact]
        public void Focus_loss_releases_everything_held_and_clears_the_down_sets()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.W);
            a.OnKeyDown(Key.LeftShift);
            a.OnMouseDown(MouseButton.Left);
            Snap(a);   // consume the press edges

            a.OnFocusChanged(false);
            InputState lost = Snap(a);

            Assert.False(lost.WindowFocused);
            Assert.Empty(lost.KeysDown);
            Assert.Empty(lost.MouseDown);
            Assert.False(lost.IsDown(Key.W));
            Assert.False(lost.IsDown(MouseButton.Left));

            // Consumers see a clean release edge rather than a key that silently stops being held.
            Assert.True(lost.WasReleased(Key.W));
            Assert.True(lost.WasReleased(Key.LeftShift));
            Assert.True(lost.WasReleased(MouseButton.Left));
        }

        [Fact]
        public void The_focus_loss_release_edge_is_gone_on_the_next_frame()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.W);
            a.OnMouseDown(MouseButton.Right);
            Snap(a);

            a.OnFocusChanged(false);
            Snap(a);

            InputState next = Snap(a);
            Assert.Empty(next.KeysReleased);
            Assert.Empty(next.MouseReleased);
        }

        [Fact]
        public void A_key_up_swallowed_by_the_OS_cannot_double_release_after_focus_returns()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.W);
            Snap(a);

            a.OnFocusChanged(false);
            Snap(a);              // the synthetic release edge lands here
            a.OnFocusChanged(true);
            a.OnKeyUp(Key.W);     // a late or stale up for a key we already released

            InputState back = Snap(a);
            Assert.True(back.WindowFocused);
            Assert.Empty(back.KeysReleased);
            Assert.Empty(back.KeysDown);
        }

        [Fact]
        public void Only_a_real_focus_transition_releases_held_input()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.W);
            Snap(a);

            // A platform re-reporting the state it is already in must not release anything.
            a.OnFocusChanged(true);
            a.OnFocusChanged(true);

            InputState still = Snap(a);
            Assert.True(still.IsDown(Key.W));
            Assert.Empty(still.KeysReleased);
        }

        [Fact]
        public void Focus_loss_with_nothing_held_produces_no_edges()
        {
            var a = new InputAccumulator();
            Snap(a);

            a.OnFocusChanged(false);

            InputState lost = Snap(a);
            Assert.False(lost.WindowFocused);
            Assert.Empty(lost.KeysReleased);
            Assert.Empty(lost.MouseReleased);
        }

        [Fact]
        public void IsFocused_starts_true_and_tracks_the_callback()
        {
            var a = new InputAccumulator();
            Assert.True(a.IsFocused);   // windows open focused

            a.OnFocusChanged(false);
            Assert.False(a.IsFocused);

            a.OnFocusChanged(true);
            Assert.True(a.IsFocused);
        }

        [Fact]
        public void Input_still_accumulates_while_unfocused()
        {
            var a = new InputAccumulator();
            a.OnFocusChanged(false);
            Snap(a);

            a.OnKeyDown(Key.Escape);

            InputState s = Snap(a);
            Assert.False(s.WindowFocused);
            Assert.True(s.WasPressed(Key.Escape));   // the focus gate is a consumer decision, not a drop here
        }

        // ---- #93: the missing mouse-released set ----------------------------------------------------------

        [Fact]
        public void Mouse_up_surfaces_a_release_edge_for_exactly_one_frame()
        {
            var a = new InputAccumulator();
            a.OnMouseDown(MouseButton.Left);
            InputState down = Snap(a);
            Assert.Empty(down.MouseReleased);

            a.OnMouseUp(MouseButton.Left);
            InputState up = Snap(a);
            Assert.Contains(MouseButton.Left, up.MouseReleased);
            Assert.True(up.WasReleased(MouseButton.Left));
            Assert.False(up.IsDown(MouseButton.Left));

            InputState after = Snap(a);
            Assert.Empty(after.MouseReleased);
            Assert.False(after.WasReleased(MouseButton.Left));
        }

        [Fact]
        public void A_mouse_up_for_a_button_that_was_never_down_produces_no_release_edge()
        {
            var a = new InputAccumulator();

            a.OnMouseUp(MouseButton.Middle);

            Assert.Empty(Snap(a).MouseReleased);
        }

        [Fact]
        public void Only_the_released_button_gets_the_edge()
        {
            var a = new InputAccumulator();
            a.OnMouseDown(MouseButton.Left);
            a.OnMouseDown(MouseButton.Right);
            Snap(a);

            a.OnMouseUp(MouseButton.Right);

            InputState s = Snap(a);
            Assert.True(s.WasReleased(MouseButton.Right));
            Assert.False(s.WasReleased(MouseButton.Left));
            Assert.True(s.IsDown(MouseButton.Left));
        }

        [Fact]
        public void An_InputState_built_without_a_mouse_released_set_reports_no_release()
        {
            // The parameter is optional and trailing, so every pre-existing builder still compiles and simply
            // reports no mouse release edge.
            Assert.Empty(InputState.Empty.MouseReleased);
            Assert.False(InputState.Empty.WasReleased(MouseButton.Left));
        }

        [Fact]
        public void A_mouse_InputSource_now_evaluates_its_release_edge()
        {
            var a = new InputAccumulator();
            InputSource source = InputSource.FromMouseButton(MouseButton.Left);

            a.OnMouseDown(MouseButton.Left);
            Assert.False(source.EvaluateReleased(Snap(a), 0));

            a.OnMouseUp(MouseButton.Left);
            Assert.True(source.EvaluateReleased(Snap(a), 0));   // returned false unconditionally before the fix

            Assert.False(source.EvaluateReleased(Snap(a), 0));
        }

        [Fact]
        public void A_mouse_InputSource_release_edge_is_button_specific()
        {
            var a = new InputAccumulator();
            a.OnMouseDown(MouseButton.Right);
            Snap(a);
            a.OnMouseUp(MouseButton.Right);

            InputState s = Snap(a);
            Assert.True(InputSource.FromMouseButton(MouseButton.Right).EvaluateReleased(s, 0));
            Assert.False(InputSource.FromMouseButton(MouseButton.Left).EvaluateReleased(s, 0));
        }

        [Fact]
        public void Focus_loss_feeds_the_mouse_release_edge_through_to_an_InputSource()
        {
            var a = new InputAccumulator();
            a.OnMouseDown(MouseButton.Left);
            Snap(a);

            a.OnFocusChanged(false);

            Assert.True(InputSource.FromMouseButton(MouseButton.Left).EvaluateReleased(Snap(a), 0));
        }

        // ---- the ordinary accumulation, unchanged ---------------------------------------------------------

        [Fact]
        public void A_key_press_edge_fires_once_and_the_hold_persists()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.Space);

            InputState first = Snap(a);
            Assert.True(first.WasPressed(Key.Space));
            Assert.True(first.IsDown(Key.Space));

            InputState second = Snap(a);
            Assert.False(second.WasPressed(Key.Space));
            Assert.True(second.IsDown(Key.Space));
        }

        [Fact]
        public void A_repeated_key_down_for_a_held_key_does_not_re_fire_the_press_edge()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.A);
            Snap(a);

            a.OnKeyDown(Key.A);

            Assert.False(Snap(a).WasPressed(Key.A));
        }

        [Fact]
        public void A_key_release_edge_fires_once_and_clears_the_hold()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.A);
            Snap(a);

            a.OnKeyUp(Key.A);
            InputState up = Snap(a);
            Assert.True(up.WasReleased(Key.A));
            Assert.False(up.IsDown(Key.A));

            Assert.False(Snap(a).WasReleased(Key.A));
        }

        [Fact]
        public void A_press_and_release_inside_one_frame_surfaces_both_edges()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.Enter);
            a.OnKeyUp(Key.Enter);

            InputState s = Snap(a);
            Assert.True(s.WasPressed(Key.Enter));
            Assert.True(s.WasReleased(Key.Enter));
            Assert.False(s.IsDown(Key.Enter));
        }

        [Fact]
        public void An_auto_repeat_tick_is_typed_but_not_pressed()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.Backspace);
            Snap(a);

            a.OnKeyRepeat(Key.Backspace);
            InputState s = Snap(a);
            Assert.True(s.WasRepeated(Key.Backspace));
            Assert.True(s.WasTyped(Key.Backspace));
            Assert.False(s.WasPressed(Key.Backspace));

            Assert.False(Snap(a).WasRepeated(Key.Backspace));   // cleared per frame like the other edges
        }

        [Fact]
        public void The_unmapped_key_sentinel_is_ignored()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.None);
            a.OnKeyRepeat(Key.None);

            InputState s = Snap(a);
            Assert.Empty(s.KeysDown);
            Assert.Empty(s.KeysPressed);
            Assert.Empty(s.KeysRepeated);
        }

        [Fact]
        public void Scroll_ticks_accumulate_within_a_frame_and_reset_after_it()
        {
            var a = new InputAccumulator();
            a.OnScroll(1.5f);
            a.OnScroll(-0.5f);

            Assert.Equal(1f, Snap(a).ScrollDelta);
            Assert.Equal(0f, Snap(a).ScrollDelta);
        }

        [Fact]
        public void A_snapshot_carries_the_framebuffer_size_it_was_given()
        {
            var a = new InputAccumulator();

            InputState s = a.Snapshot(Centre, true, 800, 600);

            Assert.Equal(800, s.Width);
            Assert.Equal(600, s.Height);
        }

        [Fact]
        public void Snapshots_are_independent_of_later_accumulation()
        {
            var a = new InputAccumulator();
            a.OnKeyDown(Key.W);
            InputState held = Snap(a);

            a.OnKeyUp(Key.W);
            Snap(a);

            // The earlier snapshot is immutable, so a later event cannot rewrite a frame already handed out.
            Assert.True(held.IsDown(Key.W));
        }
    }
}
