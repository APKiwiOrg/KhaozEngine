using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The gamepad/touch additions to <see cref="InputState"/> are non-breaking: the existing 10-arg ctor
    /// still works (the new params are optional) and defaults to empty, and the helpers degrade gracefully
    /// when nothing is connected.
    /// </summary>
    public class InputStateGamepadTouchTests
    {
        static InputState Bare(IReadOnlyList<GamepadState>? pads = null, IReadOnlyList<TouchPoint>? touches = null) =>
            new(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 100, 100, pads, touches);

        [Fact]
        public void Defaults_AreEmpty_AndPrimaryGamepadIsDisconnected()
        {
            var s = Bare();
            Assert.Empty(s.Gamepads);
            Assert.Empty(s.Touches);
            Assert.False(s.PrimaryGamepad.IsConnected);
            Assert.Same(GamepadState.Disconnected, s.Gamepad(5));   // out of range -> disconnected sentinel
        }

        [Fact]
        public void Empty_HasNoGamepadsOrTouches()
        {
            Assert.Empty(InputState.Empty.Gamepads);
            Assert.Empty(InputState.Empty.Touches);
        }

        [Fact]
        public void PopulatedGamepad_IsReachableThroughTheHelper()
        {
            var pad = new GamepadState(0,
                new HashSet<GamepadButton> { GamepadButton.Start }, new HashSet<GamepadButton>(),
                new HashSet<GamepadButton>(), Vector2.Zero, Vector2.Zero, 0, 0);
            var s = Bare(pads: new[] { pad });

            Assert.True(s.PrimaryGamepad.IsConnected);
            Assert.True(s.Gamepad(0).IsDown(GamepadButton.Start));
        }

        [Fact]
        public void Touches_ArePreserved()
        {
            var s = Bare(touches: new[] { new TouchPoint(7, new Vector2(10, 20), TouchPhase.Began) });
            Assert.Single(s.Touches);
            Assert.Equal(7, s.Touches[0].Id);
            Assert.Equal(TouchPhase.Began, s.Touches[0].Phase);
        }
    }
}
