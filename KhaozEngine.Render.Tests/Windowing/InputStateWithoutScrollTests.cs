using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    public class InputStateWithoutScrollTests
    {
        [Fact]
        public void WithoutScroll_zeros_only_the_scroll_delta()
        {
            var down = new HashSet<Key> { Key.A };
            var pressed = new HashSet<Key> { Key.B };
            var released = new HashSet<Key> { Key.C };
            var repeated = new HashSet<Key> { Key.D };
            var mouseDown = new HashSet<MouseButton> { MouseButton.Left };
            var mousePressed = new HashSet<MouseButton> { MouseButton.Middle };
            var mouseReleased = new HashSet<MouseButton> { MouseButton.Right };
            var gamepads = new[]
            {
                new GamepadState(0, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
                    new HashSet<GamepadButton>(), new Vector2(0.25f, -0.5f), Vector2.One, 0.2f, 0.8f),
            };
            var touches = new[] { new TouchPoint(7, new Vector2(30, 40), TouchPhase.Moved) };
            var input = new InputState(
                down, pressed, released, mouseDown, mousePressed,
                new Vector2(10, 20), new Vector2(3, 4), 2.5f, 1920, 1080,
                gamepads, touches, windowFocused: false, repeated: repeated, mouseReleased: mouseReleased);

            InputState masked = input.WithoutScroll();

            Assert.NotSame(input, masked);
            Assert.Equal(0f, masked.ScrollDelta);
            Assert.Same(down, masked.KeysDown);
            Assert.Same(pressed, masked.KeysPressed);
            Assert.Same(released, masked.KeysReleased);
            Assert.Same(repeated, masked.KeysRepeated);
            Assert.Same(mouseDown, masked.MouseDown);
            Assert.Same(mousePressed, masked.MousePressed);
            Assert.Same(mouseReleased, masked.MouseReleased);
            Assert.Same(gamepads, masked.Gamepads);
            Assert.Same(touches, masked.Touches);
            Assert.Equal(new Vector2(10, 20), masked.MousePosition);
            Assert.Equal(new Vector2(3, 4), masked.MouseDelta);
            Assert.Equal(1920, masked.Width);
            Assert.Equal(1080, masked.Height);
            Assert.False(masked.WindowFocused);
        }

        [Fact]
        public void WithoutScroll_reuses_a_snapshot_that_is_already_masked()
        {
            var input = new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 800, 600);

            Assert.Same(input, input.WithoutScroll());
        }
    }
}
