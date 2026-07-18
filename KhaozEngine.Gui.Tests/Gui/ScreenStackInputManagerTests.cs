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
    /// The retained <see cref="ScreenStack"/> owns and exposes an <see cref="InputManager"/> so screens can drive
    /// <see cref="FocusNavigator"/> and the keyboard/gamepad widget overloads (`Update(InputManager, focused)`)
    /// from the same input it already routes. Its pointer is the manager's pointer, so click-through blocking
    /// still composes across a screen's widgets.
    /// </summary>
    public class ScreenStackInputManagerTests
    {
        static InputState KeyFrame(params Key[] pressed)
        {
            var p = new HashSet<Key>(pressed);
            var d = new HashSet<Key>(p);
            return new InputState(d, p, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 960, 540);
        }

        [Fact]
        public void InputManager_is_exposed()
        {
            var stack = new ScreenStack();
            Assert.NotNull(stack.InputManager);
        }

        [Fact]
        public void Pointer_is_the_input_managers_pointer()
        {
            var stack = new ScreenStack();
            Assert.Same(stack.InputManager.Pointer, stack.Pointer);
        }

        [Fact]
        public void InputManager_reflects_menu_nav_after_update()
        {
            var stack = new ScreenStack();
            stack.Update(0.016f, KeyFrame(Key.Down));
            Assert.True(stack.InputManager.IsMenuDown());
        }

        sealed class ToggleScreen : Screen
        {
            public readonly Toggle Toggle = new(new Rect(10, 10, 40, 20));
            public override bool Update(float dt, bool receivesInput)
            {
                if (receivesInput) Toggle.Update(Manager.InputManager, focused: true);
                return true;
            }
            public override void Draw(SpriteBatch batch) { }
        }

        [Fact]
        public void Screen_can_drive_a_widget_overload_via_manager_inputmanager()
        {
            var stack = new ScreenStack();
            var screen = new ToggleScreen();
            stack.Add(screen);

            stack.Update(0.016f, KeyFrame(Key.Enter));   // menu-select flips the focused toggle

            Assert.True(screen.Toggle.IsOn);
        }
    }
}
