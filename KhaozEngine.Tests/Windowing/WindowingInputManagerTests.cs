using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless tests for the MonoGame-free menu-navigation input layer
    /// (<see cref="InputManager"/>): keyboard/gamepad/scroll menu actions, per-player resolution,
    /// edge-detected stick deflections, and pointer delegation with the press-origin invariant.
    /// </summary>
    public class WindowingInputManagerTests
    {
        // ---- frame builders -------------------------------------------------

        static InputState Keys(IEnumerable<Key>? down = null, IEnumerable<Key>? pressed = null,
                               float scroll = 0f, params GamepadState[] pads)
        {
            var d = new HashSet<Key>(down ?? System.Array.Empty<Key>());
            var p = new HashSet<Key>(pressed ?? System.Array.Empty<Key>());
            foreach (var k in p) d.Add(k); // a key pressed this frame is also held this frame
            return new InputState(
                d, p, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, scroll, 960, 540, pads);
        }

        static InputState Mouse(Vector2 pos, bool leftDown = false, bool middleDown = false,
                                IEnumerable<MouseButton>? pressed = null)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            if (middleDown) down.Add(MouseButton.Middle);
            var pr = new HashSet<MouseButton>(pressed ?? System.Array.Empty<MouseButton>());
            foreach (var b in pr) down.Add(b);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, pr, pos, Vector2.Zero, 0f, 960, 540);
        }

        // A gamepad with the given buttons newly pressed this frame, on the given slot.
        static GamepadState Pad(int index, IEnumerable<GamepadButton>? pressed = null, Vector2 leftStick = default)
        {
            var pr = new HashSet<GamepadButton>(pressed ?? System.Array.Empty<GamepadButton>());
            var down = new HashSet<GamepadButton>(pr);
            return new GamepadState(index, down, pr, new HashSet<GamepadButton>(),
                leftStick, Vector2.Zero, 0f, 0f);
        }

        // ---- menu select ----------------------------------------------------

        [Fact]
        public void MenuSelect_fires_on_Enter()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Enter }));
            Assert.True(im.IsMenuSelect(null, out _));
        }

        [Fact]
        public void MenuSelect_fires_on_Space()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Space }));
            Assert.True(im.IsMenuSelect(null, out _));
        }

        [Fact]
        public void MenuSelect_fires_on_gamepad_A_and_reports_the_player()
        {
            var im = new InputManager();
            im.Update(Keys(pads: new[] { Pad(0), Pad(1, new[] { GamepadButton.A }) }));
            Assert.True(im.IsMenuSelect(null, out var who));
            Assert.Equal(PlayerIndex.Two, who);
        }

        [Fact]
        public void MenuSelect_fires_on_gamepad_Start()
        {
            var im = new InputManager();
            im.Update(Keys(pads: Pad(0, new[] { GamepadButton.Start })));
            Assert.True(im.IsMenuSelect(null, out _));
        }

        // ---- menu cancel ----------------------------------------------------

        [Theory]
        [InlineData(true, false, false)]   // Escape
        [InlineData(false, true, false)]   // B
        [InlineData(false, false, true)]   // Back
        public void MenuCancel_fires_on_Escape_B_or_Back(bool esc, bool b, bool back)
        {
            var im = new InputManager();
            var pressedKeys = esc ? new[] { Key.Escape } : System.Array.Empty<Key>();
            var btns = new List<GamepadButton>();
            if (b) btns.Add(GamepadButton.B);
            if (back) btns.Add(GamepadButton.Back);
            im.Update(Keys(pressed: pressedKeys, pads: Pad(0, btns)));
            Assert.True(im.IsMenuCancel(null, out _));
        }

        // ---- menu up / down -------------------------------------------------

        [Fact]
        public void MenuUp_fires_on_UpArrow_DpadUp_and_wheelUp()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Up }));
            Assert.True(im.IsMenuUp());

            im.Update(Keys(pads: Pad(0, new[] { GamepadButton.DpadUp })));
            Assert.True(im.IsMenuUp());

            im.Update(Keys(scroll: 1f));
            Assert.True(im.IsMenuUp());
        }

        [Fact]
        public void MenuDown_fires_on_DownArrow_DpadDown_and_wheelDown()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Down }));
            Assert.True(im.IsMenuDown());

            im.Update(Keys(pads: Pad(0, new[] { GamepadButton.DpadDown })));
            Assert.True(im.IsMenuDown());

            im.Update(Keys(scroll: -1f));
            Assert.True(im.IsMenuDown());
        }

        // ---- edge-detected stick deflection ---------------------------------

        [Fact]
        public void MenuUp_fires_once_when_left_stick_pushed_up_then_held()
        {
            var im = new InputManager();
            im.Update(Keys(pads: Pad(0, leftStick: Vector2.Zero)));       // centered
            Assert.False(im.IsMenuUp());

            im.Update(Keys(pads: Pad(0, leftStick: new Vector2(0, 0.9f)))); // +Y = up
            Assert.True(im.IsMenuUp());                                     // edge fires

            im.Update(Keys(pads: Pad(0, leftStick: new Vector2(0, 0.9f)))); // still up
            Assert.False(im.IsMenuUp());                                    // no refire while held
        }

        [Fact]
        public void MenuDown_fires_again_after_stick_recentred_and_repushed()
        {
            var im = new InputManager();
            im.Update(Keys(pads: Pad(0, leftStick: new Vector2(0, -0.9f))));
            Assert.True(im.IsMenuDown());
            im.Update(Keys(pads: Pad(0, leftStick: Vector2.Zero)));   // recentre
            Assert.False(im.IsMenuDown());
            im.Update(Keys(pads: Pad(0, leftStick: new Vector2(0, -0.9f))));
            Assert.True(im.IsMenuDown());                             // edge fires again
        }

        // ---- next / previous ------------------------------------------------

        [Fact]
        public void SelectNext_fires_on_Right_and_DpadRight()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Right }));
            Assert.True(im.IsSelectNext());
            im.Update(Keys(pads: Pad(0, new[] { GamepadButton.DpadRight })));
            Assert.True(im.IsSelectNext());
        }

        [Fact]
        public void SelectPrevious_fires_on_Left_and_DpadLeft()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Left }));
            Assert.True(im.IsSelectPrevious());
            im.Update(Keys(pads: Pad(0, new[] { GamepadButton.DpadLeft })));
            Assert.True(im.IsSelectPrevious());
        }

        // ---- raw key / button edges ----------------------------------------

        [Fact]
        public void IsKeyJustPressed_is_true_only_on_the_press_frame()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.W }));
            Assert.True(im.IsKeyJustPressed(Key.W));
            Assert.True(im.IsKeyDown(Key.W));
            im.Update(Keys(down: new[] { Key.W }));   // held, not newly pressed
            Assert.False(im.IsKeyJustPressed(Key.W));
            Assert.True(im.IsKeyDown(Key.W));
        }

        [Fact]
        public void IsNewKeyPress_echoes_the_controlling_player()
        {
            var im = new InputManager();
            im.Update(Keys(pressed: new[] { Key.Enter }));
            Assert.True(im.IsNewKeyPress(Key.Enter, PlayerIndex.Three, out var who));
            Assert.Equal(PlayerIndex.Three, who);
        }

        [Fact]
        public void IsNewButtonPress_for_a_specific_player_ignores_other_pads()
        {
            var im = new InputManager();
            im.Update(Keys(pads: new[] { Pad(0), Pad(1, new[] { GamepadButton.A }) }));
            Assert.False(im.IsNewButtonPress(GamepadButton.A, PlayerIndex.One, out _));
            Assert.True(im.IsNewButtonPress(GamepadButton.A, PlayerIndex.Two, out var who));
            Assert.Equal(PlayerIndex.Two, who);
        }

        [Fact]
        public void IsNewButtonPress_any_player_scans_and_reports_who()
        {
            var im = new InputManager();
            im.Update(Keys(pads: new[] { Pad(0), Pad(1, new[] { GamepadButton.Start }) }));
            Assert.True(im.IsNewButtonPress(GamepadButton.Start, null, out var who));
            Assert.Equal(PlayerIndex.Two, who);
        }

        // ---- pause ----------------------------------------------------------

        [Theory]
        [InlineData(true, false, false)]   // Escape
        [InlineData(false, true, false)]   // Back
        [InlineData(false, false, true)]   // Start
        public void IsPauseGame_fires_on_Escape_Back_or_Start(bool esc, bool back, bool start)
        {
            var im = new InputManager();
            var pressedKeys = esc ? new[] { Key.Escape } : System.Array.Empty<Key>();
            var btns = new List<GamepadButton>();
            if (back) btns.Add(GamepadButton.Back);
            if (start) btns.Add(GamepadButton.Start);
            im.Update(Keys(pressed: pressedKeys, pads: Pad(0, btns)));
            Assert.True(im.IsPauseGame());
        }

        [Fact]
        public void IsPauseGame_fires_on_a_tap_inside_bounds()
        {
            var bounds = new Rect(100, 100, 80, 40);
            var im = new InputManager();
            im.Update(Mouse(new Vector2(120, 120), leftDown: true));  // press inside
            Assert.False(im.IsPauseGame(null, bounds));
            im.Update(Mouse(new Vector2(120, 120), leftDown: false)); // release inside -> tap
            Assert.True(im.IsPauseGame(null, bounds));
        }

        // ---- pointer delegation + press-origin invariant --------------------

        [Fact]
        public void IsTapIn_requires_press_and_release_inside_bounds()
        {
            var box = new Rect(100, 100, 200, 80);
            var im = new InputManager();
            im.Update(Mouse(new Vector2(150, 140), leftDown: true));   // press inside
            im.Update(Mouse(new Vector2(150, 140), leftDown: false));  // release inside
            Assert.True(im.IsTapIn(box));
        }

        [Fact]
        public void IsTapIn_is_false_when_the_press_began_outside()
        {
            var box = new Rect(100, 100, 200, 80);
            var im = new InputManager();
            im.Update(Mouse(new Vector2(10, 10), leftDown: true));     // press outside
            im.Update(Mouse(new Vector2(150, 140), leftDown: false));  // release inside
            Assert.False(im.IsTapIn(box));                            // click-through prevented
        }

        [Fact]
        public void Pointer_maps_into_design_space_through_the_viewport()
        {
            // 960x540 design Fit into 1920x1200 -> scale 2, 60px letterbox bars.
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);
            Vector2 screen = vp.DesignToScreen(new Vector2(150, 140)); // -> (300, 340)

            var im = new InputManager();
            im.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                screen, Vector2.Zero, 0f, 1920, 1200), vp);
            Assert.Equal(new Vector2(150, 140), im.PointerPosition);
        }

        // ---- scroll + middle button edges -----------------------------------

        [Fact]
        public void Wheel_scroll_sign_drives_up_and_down()
        {
            var im = new InputManager();
            im.Update(Keys(scroll: 1f));
            Assert.True(im.IsMouseWheelScrolledUp);
            Assert.False(im.IsMouseWheelScrolledDown);
            im.Update(Keys(scroll: -1f));
            Assert.True(im.IsMouseWheelScrolledDown);
            Assert.False(im.IsMouseWheelScrolledUp);
        }

        [Fact]
        public void Middle_button_just_pressed_and_released_are_single_frame_edges()
        {
            var im = new InputManager();
            im.Update(Mouse(new Vector2(10, 10), middleDown: true));
            Assert.True(im.IsMiddleJustPressed);
            Assert.True(im.IsMiddleDown);
            im.Update(Mouse(new Vector2(10, 10), middleDown: true));
            Assert.False(im.IsMiddleJustPressed);
            im.Update(Mouse(new Vector2(10, 10), middleDown: false));
            Assert.True(im.IsMiddleJustReleased);
        }
    }
}
