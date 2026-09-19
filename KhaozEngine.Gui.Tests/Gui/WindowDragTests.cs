using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="WindowDrag"/> over a synthetic window: the offset it keeps from the caller's own layout, the
    /// grab it latches once on the press frame, the gesture it claims while held, and the clamp it writes back.
    /// </summary>
    /// <remarks>
    /// There is no window type here on purpose. The drag reads a <see cref="Pointer"/> and a grip rect and
    /// nothing else, so the test supplies a natural rect, a title-row grip taken from wherever the window is
    /// currently placed, and a frame loop in the order a real caller runs it: update the drag against the grip
    /// the player can see, then place the window for the next frame. The grip travelling with the window is what
    /// makes the multi-hop grab test mean anything.
    /// </remarks>
    public class WindowDragTests
    {
        const float Width = 1280f;
        const float Height = 720f;

        // An arbitrary window: 320 by 240, placed by its own layout at (400, 300).
        static readonly Rect Natural = new(400f, 300f, 320f, 240f);

        // The grip is the title row of wherever the window currently SITS, which is the whole point.
        const float GripHeight = 26f;

        // Everything underneath, as one rect: a world click handler asking whether the release was a tap.
        static readonly Rect World = new(0f, 0f, Width, Height);

        readonly WindowDrag _drag = new();
        readonly Pointer _pointer = new();

        // One per test-class instance, never a static, so a gesture cannot leak between facts.
        readonly MouseFrames _mouse = new();

        Rect _placed = Natural;

        static Vector2 Viewport => new(Width, Height);

        static Vector2 Centre(in Rect rect) => new(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);

        static Rect GripOf(in Rect placed) => new(placed.X, placed.Y, placed.Width, GripHeight);

        // One frame of a window that owns a drag: feed the pointer, update the drag against the grip the player
        // is looking at, then place the window for the next frame.
        Rect Frame(Vector2 at, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(held);
            _pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, at, Vector2.Zero, 0, (int)Width, (int)Height, mouseReleased: released));
            _drag.Update(_pointer, GripOf(_placed));
            _placed = _drag.Place(Natural, Viewport);
            return _placed;
        }

        [Fact]
        public void The_drag_is_an_offset_from_the_layout_so_a_window_keeps_its_placement_rule()
        {
            Vector2 grip = Centre(GripOf(Natural));
            Frame(grip, down: false);
            Frame(grip, down: true);
            Rect moved = Frame(grip + new Vector2(-120f, 90f), down: true);

            // The window moved by exactly what the cursor moved, and it did not resize.
            Assert.Equal(Natural.X - 120f, moved.X, 3);
            Assert.Equal(Natural.Y + 90f, moved.Y, 3);
            Assert.Equal(Natural.Width, moved.Width, 3);
            Assert.Equal(Natural.Height, moved.Height, 3);
            Assert.Equal(new Vector2(-120f, 90f), _drag.Offset);

            // A resize moves the layout, and the drag rides it: the SAME offset over a new natural rect, rather
            // than an absolute position that would strand the window where the old layout used to put it.
            Rect resized = _drag.Place(new Rect(500f, 100f, 320f, 240f), Viewport);
            Assert.Equal(500f - 120f, resized.X, 3);
            Assert.Equal(100f + 90f, resized.Y, 3);
            Assert.Equal(new Vector2(-120f, 90f), _drag.Offset);

            // And placing again against the original layout is idempotent, so a frame that draws twice does not
            // walk the window across the screen.
            Rect again = _drag.Place(Natural, Viewport);
            Assert.Equal(moved.X, again.X, 3);
            Assert.Equal(moved.Y, again.Y, 3);
        }

        [Fact]
        public void The_grab_latches_once_on_the_press_and_holds_however_far_the_grip_travels()
        {
            Vector2 at = Centre(GripOf(Natural));
            Frame(at, down: false);
            Frame(at, down: true);
            Assert.True(_drag.Dragging);

            // Five hops, each TALLER than the grip, which is the shape a real drag arrives in. A single hop can
            // never show the defect, because the window has not yet moved out from under its own press on the
            // one frame it applies a delta. Re-reading the press origin every frame drops the grab from hop two.
            Vector2 moved = Vector2.Zero;
            for (int hop = 0; hop < 5; hop++)
            {
                var step = new Vector2(-30f, 30f);
                moved += step;
                at += step;
                Rect placed = Frame(at, down: true);

                Assert.True(_drag.Dragging, $"the grab was dropped on hop {hop}");
                Assert.False(GripOf(placed).Contains(_pointer.PressOrigin),
                    $"hop {hop} did not move the grip off the press origin, so it proves nothing");
                Assert.Equal(Natural.X + moved.X, placed.X, 3);
                Assert.Equal(Natural.Y + moved.Y, placed.Y, 3);
            }

            // Well outside the window entirely, which is where a fast drag puts the cursor between two frames.
            at = new Vector2(4f, Height - 4f);
            Frame(at, down: true);
            Assert.True(_drag.Dragging);

            // The button going up is the only thing that ends it, and a move after that moves nothing.
            Rect resting = Frame(at, down: false);
            Assert.False(_drag.Dragging);
            Rect after = Frame(at + new Vector2(80f, -80f), down: false);
            Assert.Equal(resting.X, after.X, 3);
            Assert.Equal(resting.Y, after.Y, 3);
        }

        [Fact]
        public void A_press_that_did_not_begin_in_the_grip_never_picks_the_window_up()
        {
            // Below the grip, in the window's body: a press on content is not a press on the title bar.
            var body = new Vector2(Centre(Natural).X, Natural.Y + GripHeight + 40f);
            Frame(body, down: false);
            Frame(body, down: true);
            Assert.False(_drag.Dragging);

            // And the cursor wandering up over the grip while still held does not grab it either, because the
            // press origin was latched outside and the drag reads that once.
            Rect placed = Frame(Centre(GripOf(Natural)), down: true);
            Assert.False(_drag.Dragging);
            Assert.Equal(Vector2.Zero, _drag.Offset);
            Assert.Equal(Natural.X, placed.X, 3);
            Assert.Equal(Natural.Y, placed.Y, 3);

            // Nothing was claimed, so whatever is under the window still gets its press.
            Assert.False(_pointer.IsConsumed);
        }

        [Fact]
        public void A_held_drag_claims_the_gesture_so_the_release_is_not_a_world_click()
        {
            Vector2 grip = Centre(GripOf(Natural));
            Frame(grip, down: false);
            Frame(grip, down: true);
            Assert.True(_pointer.IsConsumed);

            Frame(grip + new Vector2(-60f, 40f), down: true);
            Assert.True(_pointer.IsConsumed);

            // The release lands inside the world, and so did the press, so without the claim this finishes as a
            // world tap: upstream that was a walk order at the end of every drag.
            Frame(grip + new Vector2(-60f, 40f), down: false);
            Assert.True(_pointer.IsJustReleased);
            Assert.True(_pointer.IsConsumed);
            Assert.False(_pointer.IsTapIn(World));
        }

        [Fact]
        public void An_ordinary_tap_that_never_grabbed_still_reaches_the_world()
        {
            // The contrast that makes the claim above worth asserting: the same three frames on the window's
            // body grab nothing, claim nothing, and the tap goes through.
            var body = new Vector2(Centre(Natural).X, Natural.Y + GripHeight + 40f);
            Frame(body, down: false);
            Frame(body, down: true);
            Frame(body, down: false);

            Assert.False(_drag.Dragging);
            Assert.False(_pointer.IsConsumed);
            Assert.True(_pointer.IsTapIn(World));
        }

        [Fact]
        public void The_clamp_is_written_back_so_a_drag_off_the_edge_banks_no_distance()
        {
            Vector2 grip = Centre(GripOf(Natural));
            Frame(grip, down: false);
            Frame(grip, down: true);

            // Hard into the top right corner, far past what the viewport allows.
            Rect pinned = Frame(new Vector2(Width - 10f, 4f), down: true);
            Assert.Equal(Width - Natural.Width, pinned.X, 3);
            Assert.Equal(0f, pinned.Y, 3);

            // The offset banked is the CLAMPED one, not the overshoot.
            Assert.Equal(pinned.X - Natural.X, _drag.Offset.X, 3);
            Assert.Equal(pinned.Y - Natural.Y, _drag.Offset.Y, 3);

            Frame(new Vector2(Width - 10f, 4f), down: false);

            // So the very next drag back moves the window immediately, rather than spending its first several
            // hundred units paying off distance the window never travelled.
            Vector2 corner = Centre(GripOf(pinned));
            Frame(corner, down: false);
            Frame(corner, down: true);
            Rect nudged = Frame(corner + new Vector2(-40f, 40f), down: true);
            Assert.Equal(pinned.X - 40f, nudged.X, 3);
            Assert.Equal(pinned.Y + 40f, nudged.Y, 3);
        }

        [Fact]
        public void A_viewport_smaller_than_the_window_clamps_to_zero_rather_than_to_a_negative()
        {
            // The window's top left corner stays reachable, so it can still be picked up and closed.
            Rect placed = _drag.Place(Natural, new Vector2(100f, 80f));
            Assert.Equal(0f, placed.X, 3);
            Assert.Equal(0f, placed.Y, 3);
            Assert.Equal(Natural.Width, placed.Width, 3);
        }

        [Fact]
        public void Release_lets_go_without_forgetting_the_position_and_Reset_forgets_it()
        {
            Vector2 grip = Centre(GripOf(Natural));
            Frame(grip, down: false);
            Frame(grip, down: true);
            Rect moved = Frame(grip + new Vector2(-70f, 30f), down: true);
            Assert.True(_drag.Dragging);

            // A window closing under a held button lets go, but remembers where it was put.
            _drag.Release();
            Assert.False(_drag.Dragging);
            Assert.Equal(new Vector2(-70f, 30f), _drag.Offset);

            // And it is not re-grabbed by the button that is still down, because that press is no longer fresh.
            Rect stillThere = Frame(grip + new Vector2(120f, -80f), down: true);
            Assert.False(_drag.Dragging);
            Assert.Equal(moved.X, stillThere.X, 3);
            Assert.Equal(moved.Y, stillThere.Y, 3);

            // Reset is the deliberate one: back to whatever the layout wants.
            _drag.Reset();
            Assert.Equal(Vector2.Zero, _drag.Offset);
            Assert.False(_drag.Dragging);
            Rect home = _drag.Place(Natural, Viewport);
            Assert.Equal(Natural.X, home.X, 3);
            Assert.Equal(Natural.Y, home.Y, 3);
        }
    }
}
