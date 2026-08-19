using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The right-button bounds helpers (<see cref="Pointer.IsRightTapIn"/> / <see cref="Pointer.IsRightPressingIn"/>)
    /// and their own press-origin + consume latches, which are tracked separately from the left button's.
    /// </summary>
    public class PointerRightButtonTests
    {
        static readonly Rect Box = new(100, 100, 200, 80);
        static readonly Rect Other = new(400, 100, 200, 80);
        static readonly Vector2 Inside = new(150, 140);
        static readonly Vector2 AlsoInside = new(200, 150);
        static readonly Vector2 Outside = new(450, 140);   // inside Other, outside Box

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown = false, bool rightDown = false)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            if (rightDown) down.Add(MouseButton.Right);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        [Fact]
        public void RightPressOrigin_latches_on_the_right_press_and_survives_the_cursor_moving()
        {
            var p = new Pointer();
            p.Update(Frame(Inside));
            p.Update(Frame(Inside, rightDown: true));
            Assert.Equal(Inside, p.RightPressOrigin);

            p.Update(Frame(Outside, rightDown: true));    // dragged out while still held
            Assert.Equal(Inside, p.RightPressOrigin);     // origin unchanged
            Assert.Equal(Outside, p.Position);
        }

        [Fact]
        public void IsRightTapIn_needs_the_origin_AND_the_release_inside()
        {
            var p = new Pointer();
            p.Update(Frame(Inside));
            p.Update(Frame(Inside, rightDown: true));
            Assert.False(p.IsRightTapIn(Box));            // still held, no release yet
            p.Update(Frame(AlsoInside));                  // released inside
            Assert.True(p.IsRightTapIn(Box));
            Assert.False(p.IsRightTapIn(Other));          // wrong rect
        }

        [Fact]
        public void IsRightTapIn_is_false_when_the_press_began_elsewhere()
        {
            var p = new Pointer();
            p.Update(Frame(Outside));
            p.Update(Frame(Outside, rightDown: true));    // press origin in Other
            p.Update(Frame(Inside));                      // released over Box
            Assert.False(p.IsRightTapIn(Box));            // click-through invariant holds for the right button too
            Assert.False(p.IsRightTapIn(Other));          // and it did not release inside Other either
        }

        [Fact]
        public void IsRightPressingIn_tracks_the_held_right_button_and_clears_when_the_cursor_leaves()
        {
            var p = new Pointer();
            p.Update(Frame(Inside));
            p.Update(Frame(Inside, rightDown: true));
            Assert.True(p.IsRightPressingIn(Box));

            p.Update(Frame(Outside, rightDown: true));    // strayed off while held
            Assert.False(p.IsRightPressingIn(Box));

            p.Update(Frame(AlsoInside, rightDown: true)); // back inside, same gesture
            Assert.True(p.IsRightPressingIn(Box));
        }

        [Fact]
        public void ConsumeRightGesture_suppresses_the_tap_until_the_next_fresh_right_press()
        {
            var p = new Pointer();
            p.Update(Frame(Inside));
            p.Update(Frame(Inside, rightDown: true));
            p.ConsumeRightGesture();
            Assert.True(p.IsRightConsumed);

            p.Update(Frame(Inside));                      // release of the consumed gesture
            Assert.False(p.IsRightTapIn(Box));

            p.Update(Frame(Inside, rightDown: true));     // a fresh press clears the latch
            Assert.False(p.IsRightConsumed);
            p.Update(Frame(Inside));
            Assert.True(p.IsRightTapIn(Box));
        }

        [Fact]
        public void The_two_buttons_keep_separate_origins_and_separate_consume_latches()
        {
            var p = new Pointer();
            p.Update(Frame(Inside));
            p.Update(Frame(Inside, rightDown: true));     // right press inside Box
            p.Update(Frame(Outside, rightDown: true, leftDown: true));   // left press elsewhere, right still held

            Assert.Equal(Inside, p.RightPressOrigin);     // right origin untouched by the left press
            Assert.Equal(Outside, p.PressOrigin);

            p.ConsumeGesture();                           // consuming the LEFT gesture
            Assert.True(p.IsConsumed);
            Assert.False(p.IsRightConsumed);              // must not blind the right button

            p.Update(Frame(AlsoInside, leftDown: true));  // right released inside Box, left still held
            Assert.True(p.IsRightTapIn(Box));             // right tap still fires
        }

        [Fact]
        public void InputManager_forwards_the_right_button_bounds_helpers()
        {
            var input = new InputManager();
            input.Update(Frame(Inside));
            input.Update(Frame(Inside, rightDown: true));
            Assert.True(input.IsRightPressingIn(Box));
            Assert.Equal(Inside, input.RightPressOrigin);

            input.Update(Frame(AlsoInside));
            Assert.True(input.IsRightTapIn(Box));
        }
    }
}
