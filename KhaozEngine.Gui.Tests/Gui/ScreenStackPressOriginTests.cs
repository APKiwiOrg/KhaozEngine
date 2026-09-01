using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The press-origin invariant across the UI boundary. The stack drives its OWN composed pointer, so a
    /// reservation it makes is invisible to the game's world-picking pointer, and there is no rect a game can
    /// hand <see cref="Pointer.IsTapIn"/> that means "the whole screen stack". The question a game with a modal
    /// screen over a 3D world has to ask is whether the gesture BEGAN over UI, answered on the release frame by
    /// which point the screen is usually gone.
    /// </summary>
    public class ScreenStackPressOriginTests
    {
        static readonly Rect Panel = new(100, 100, 200, 80);

        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (pressed, released) = _mouse.Advance(down);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, pressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: released);
        }

        // A frame carrying the press edge for a button that is already up again: the tap whose press and release
        // both landed inside one frame, routine at the engine's background-throttle rates.
        InputState SameFrameTap(Vector2 pos)
        {
            _mouse.Advance(new HashSet<MouseButton>());
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton> { MouseButton.Left },
                pos, Vector2.Zero, 0, 960, 540, mouseReleased: new HashSet<MouseButton>());
        }

        // A screen that reserves one rect, the way any widget-bearing screen does through Update.
        sealed class ReservingScreen : Screen
        {
            public override bool Update(float dt, bool receivesInput)
            {
                if (receivesInput) Manager.Pointer.BlockRegion(Panel);
                return true;
            }
            public override void Draw(SpriteBatch batch) { }
        }

        static ScreenStack WithPanel()
        {
            var stack = new ScreenStack();
            stack.Add(new ReservingScreen());
            return stack;
        }

        [Fact]
        public void No_press_leaves_the_latch_clear()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            Assert.False(stack.PressBeganOverUi);
        }

        [Fact]
        public void A_press_that_began_over_a_reserved_region_latches()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            stack.Update(0.016f, Frame(new Vector2(150, 130), true));   // press inside the panel
            Assert.True(stack.PressBeganOverUi);
        }

        [Fact]
        public void The_latch_survives_the_screen_closing_before_the_release()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            stack.Update(0.016f, Frame(new Vector2(150, 130), true));   // press over the modal
            Assert.True(stack.PressBeganOverUi);

            stack.Remove(stack.Screens[0]);                              // the press dismissed it

            stack.Update(0.016f, Frame(new Vector2(150, 130), false));   // release, nothing reserved any more
            Assert.True(stack.PressBeganOverUi);                         // the world must still refuse this tap
        }

        [Fact]
        public void A_press_outside_every_reservation_does_not_latch()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(600, 400), false));
            stack.Update(0.016f, Frame(new Vector2(600, 400), true));
            stack.Update(0.016f, Frame(new Vector2(600, 400), false));
            Assert.False(stack.PressBeganOverUi);
        }

        [Fact]
        public void The_next_fresh_press_re_evaluates_the_latch()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            stack.Update(0.016f, Frame(new Vector2(150, 130), true));    // over the panel
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            Assert.True(stack.PressBeganOverUi);

            stack.Update(0.016f, Frame(new Vector2(600, 400), true));    // a fresh press in the world
            Assert.False(stack.PressBeganOverUi);
        }

        [Fact]
        public void A_same_frame_tap_over_ui_latches_on_that_frame()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            stack.Update(0.016f, SameFrameTap(new Vector2(150, 130)));
            Assert.True(stack.PressBeganOverUi);
        }

        [Fact]
        public void A_held_drag_off_the_panel_keeps_the_latch()
        {
            var stack = WithPanel();
            stack.Update(0.016f, Frame(new Vector2(150, 130), false));
            stack.Update(0.016f, Frame(new Vector2(150, 130), true));    // press over the panel
            stack.Update(0.016f, Frame(new Vector2(600, 400), true));    // dragged out into the world
            stack.Update(0.016f, Frame(new Vector2(600, 400), false));   // released there
            Assert.True(stack.PressBeganOverUi);
        }

        [Fact]
        public void Pointer_reports_a_fresh_press_origin_for_one_frame()
        {
            var p = new Pointer();
            p.Update(Frame(new Vector2(10, 10), false));
            Assert.False(p.IsPressOriginFresh);

            p.Update(Frame(new Vector2(10, 10), true));
            Assert.True(p.IsPressOriginFresh);

            p.Update(Frame(new Vector2(20, 20), true));     // still held: the origin has not moved
            Assert.False(p.IsPressOriginFresh);

            p.Update(Frame(new Vector2(20, 20), false));
            Assert.False(p.IsPressOriginFresh);
        }

        [Fact]
        public void Pointer_reports_a_fresh_press_origin_for_a_same_frame_tap()
        {
            var p = new Pointer();
            p.Update(Frame(new Vector2(10, 10), false));
            p.Update(SameFrameTap(new Vector2(42, 42)));
            Assert.True(p.IsPressOriginFresh);
            Assert.True(p.IsJustReleased);
            Assert.Equal(new Vector2(42, 42), p.PressOrigin);
        }
    }
}
