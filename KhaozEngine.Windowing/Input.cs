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
        // Keypad (numpad) block, appended after the original members so existing Key values stay stable.
        // There is deliberately no KeypadEnter: AppWindow folds the physical keypad Enter into Enter, so a
        // numpad Enter commits/confirms exactly like the main Enter key everywhere.
        Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
        KeypadDecimal, KeypadAdd, KeypadSubtract, KeypadMultiply, KeypadDivide, KeypadEqual,
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
        /// <summary>
        /// Keys that fired an OS auto-repeat tick this frame (a held key past the OS repeat delay, recurring at the
        /// OS repeat rate). Surfaced from GLFW's <c>REPEAT</c> key action by <see cref="AppWindow"/>; empty in
        /// headless frames unless a test marks them. Distinct from <see cref="KeysPressed"/> (the press edge): a held
        /// key produces one entry here per repeat tick but is never added to <see cref="KeysPressed"/> after the
        /// initial press. Read it via <see cref="WasRepeated"/>, or <see cref="WasTyped"/> for the press-or-repeat
        /// signal text entry wants.
        /// </summary>
        public IReadOnlySet<Key> KeysRepeated { get; }
        public IReadOnlySet<MouseButton> MouseDown { get; }
        public IReadOnlySet<MouseButton> MousePressed { get; }
        public Vector2 MousePosition { get; }
        public Vector2 MouseDelta { get; }
        public float ScrollDelta { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Connected gamepads this frame (empty when none). Index via <see cref="Gamepad"/>.</summary>
        public IReadOnlyList<GamepadState> Gamepads { get; }
        /// <summary>Active touch points this frame (empty on desktop).</summary>
        public IReadOnlyList<TouchPoint> Touches { get; }
        /// <summary>
        /// True while the window owning this snapshot has OS input focus (the frontmost window). The render loop
        /// keeps running while unfocused and the cursor position stays live, so consumers that should ignore input
        /// when the window is in the background gate on this (e.g. world clicks / hotkeys). The GUI hover/capture
        /// gates already honour it via <see cref="Pointer.WindowFocused"/>. Defaults to <c>true</c> (windows open
        /// focused) so existing builders keep reporting focused.
        /// </summary>
        public bool WindowFocused { get; }

        static readonly IReadOnlySet<Key> EmptyKeys = new HashSet<Key>();

        public static readonly InputState Empty = new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 0, 0, windowFocused: false);

        public InputState(
            IReadOnlySet<Key> down, IReadOnlySet<Key> pressed, IReadOnlySet<Key> released,
            IReadOnlySet<MouseButton> mouseDown, IReadOnlySet<MouseButton> mousePressed,
            Vector2 mousePosition, Vector2 mouseDelta, float scrollDelta, int width, int height,
            IReadOnlyList<GamepadState>? gamepads = null, IReadOnlyList<TouchPoint>? touches = null,
            bool windowFocused = true, IReadOnlySet<Key>? repeated = null)
        {
            KeysDown = down; KeysPressed = pressed; KeysReleased = released;
            KeysRepeated = repeated ?? EmptyKeys;
            MouseDown = mouseDown; MousePressed = mousePressed;
            MousePosition = mousePosition; MouseDelta = mouseDelta; ScrollDelta = scrollDelta;
            Width = width; Height = height;
            Gamepads = gamepads ?? System.Array.Empty<GamepadState>();
            Touches = touches ?? System.Array.Empty<TouchPoint>();
            WindowFocused = windowFocused;
        }

        /// <summary>The gamepad at <paramref name="index"/>, or <see cref="GamepadState.Disconnected"/> if absent.</summary>
        public GamepadState Gamepad(int index = 0) =>
            index >= 0 && index < Gamepads.Count ? Gamepads[index] : GamepadState.Disconnected;

        /// <summary>The first gamepad, or <see cref="GamepadState.Disconnected"/> if none is connected.</summary>
        public GamepadState PrimaryGamepad => Gamepad(0);

        /// <summary>True while <paramref name="key"/> is held.</summary>
        public bool IsDown(Key key) => KeysDown.Contains(key);
        /// <summary>True only on the frame <paramref name="key"/> went down (excludes auto-repeat).</summary>
        public bool WasPressed(Key key) => KeysPressed.Contains(key);
        /// <summary>True on a frame <paramref name="key"/> fired an OS auto-repeat tick (held past the repeat delay).</summary>
        public bool WasRepeated(Key key) => KeysRepeated.Contains(key);
        /// <summary>
        /// True on the press edge OR an auto-repeat tick: the "a character was typed this frame" signal for
        /// hold-to-repeat text entry. Equivalent to <c>WasPressed(key) || WasRepeated(key)</c>.
        /// </summary>
        public bool WasTyped(Key key) => KeysPressed.Contains(key) || KeysRepeated.Contains(key);
        /// <summary>True only on the frame <paramref name="key"/> went up.</summary>
        public bool WasReleased(Key key) => KeysReleased.Contains(key);
        public bool IsDown(MouseButton button) => MouseDown.Contains(button);
        public bool WasPressed(MouseButton button) => MousePressed.Contains(button);
    }
}
