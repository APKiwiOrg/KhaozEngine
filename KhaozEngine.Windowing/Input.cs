using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>Engine-native keyboard keys (no MonoGame / Veldrid types leak out).</summary>
    public enum Key
    {
        None = 0,
        A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Up, Down, Left, Right,
        Space, Enter, Escape, Tab, Backspace, Delete, Insert, Home, End, PageUp, PageDown,
        LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt, LeftSuper, RightSuper,
        Minus, Equals, LeftBracket, RightBracket, Backslash, Semicolon, Apostrophe, Comma, Period, Slash, Grave,
    }

    /// <summary>Engine-native mouse buttons.</summary>
    public enum MouseButton { Left, Middle, Right, X1, X2 }

    /// <summary>
    /// Immutable per-frame input snapshot: which keys/buttons are held, which went down this frame,
    /// the mouse position/delta, and the scroll delta. Pure engine types.
    /// </summary>
    public sealed class InputState
    {
        public IReadOnlySet<Key> KeysDown { get; }
        public IReadOnlySet<Key> KeysPressed { get; }
        public IReadOnlySet<Key> KeysReleased { get; }
        public IReadOnlySet<MouseButton> MouseDown { get; }
        public IReadOnlySet<MouseButton> MousePressed { get; }
        public Vector2 MousePosition { get; }
        public Vector2 MouseDelta { get; }
        public float ScrollDelta { get; }
        public int Width { get; }
        public int Height { get; }

        public static readonly InputState Empty = new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 0, 0);

        public InputState(
            IReadOnlySet<Key> down, IReadOnlySet<Key> pressed, IReadOnlySet<Key> released,
            IReadOnlySet<MouseButton> mouseDown, IReadOnlySet<MouseButton> mousePressed,
            Vector2 mousePosition, Vector2 mouseDelta, float scrollDelta, int width, int height)
        {
            KeysDown = down; KeysPressed = pressed; KeysReleased = released;
            MouseDown = mouseDown; MousePressed = mousePressed;
            MousePosition = mousePosition; MouseDelta = mouseDelta; ScrollDelta = scrollDelta;
            Width = width; Height = height;
        }

        /// <summary>True while <paramref name="key"/> is held.</summary>
        public bool IsDown(Key key) => KeysDown.Contains(key);
        /// <summary>True only on the frame <paramref name="key"/> went down (excludes auto-repeat).</summary>
        public bool WasPressed(Key key) => KeysPressed.Contains(key);
        /// <summary>True only on the frame <paramref name="key"/> went up.</summary>
        public bool WasReleased(Key key) => KeysReleased.Contains(key);
        public bool IsDown(MouseButton button) => MouseDown.Contains(button);
        public bool WasPressed(MouseButton button) => MousePressed.Contains(button);
    }
}
