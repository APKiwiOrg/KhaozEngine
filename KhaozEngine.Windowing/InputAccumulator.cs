using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The raw-event to snapshot state machine behind <see cref="AppWindow"/>. It owns the edge-tracking input
    /// state (which keys and buttons are held, which went down, up, or auto-repeated this frame, the accumulated
    /// wheel delta, the last cursor position, and OS focus) and folds it into an immutable
    /// <see cref="InputState"/> once per frame.
    ///
    /// <para><b>Why it is its own type.</b> <see cref="AppWindow"/> is the only class allowed to touch the
    /// Silk.NET/GLFW input statics, and it keeps that wiring plus the raw-to-engine translation. Everything
    /// downstream of the translation lives here: one method per raw OS event, then <see cref="Snapshot"/> to
    /// publish the frame. Nothing in this type touches an OS API and it needs no window, so the accumulation
    /// model that used to be untestable window internals is now plain headless-testable code.</para>
    ///
    /// <para><b>Threading.</b> The Silk and GLFW callbacks run on the GLFW/main thread during the frame poll, and
    /// <see cref="Snapshot"/> runs on that same thread inside the render callback, so the sets need no locking.
    /// Do not drive one instance from more than one thread.</para>
    /// </summary>
    public sealed class InputAccumulator
    {
        // Edge-tracking input state. The OS fires key/button down, up, repeat and scroll events on its event
        // pump, we accumulate them into per-frame sets, and Snapshot publishes plus clears them once per frame.
        readonly HashSet<Key> _keysDown = new();
        readonly HashSet<Key> _pressed = new();
        readonly HashSet<Key> _released = new();
        // Keys that fired an OS auto-repeat tick this frame (held past the OS repeat delay). Snapshotted and
        // cleared per frame like _pressed, and never folded into it: a held key repeats without re-pressing.
        readonly HashSet<Key> _repeated = new();
        readonly HashSet<MouseButton> _mouseDown = new();
        readonly HashSet<MouseButton> _mousePressed = new();
        readonly HashSet<MouseButton> _mouseReleased = new();
        Vector2 _lastMouse;
        // False until the first snapshot that actually sampled a cursor. Frame 1's delta is reported as zero
        // rather than as (cursor - origin), which would otherwise hand a mouse-look camera a full-screen snap on
        // its very first frame. This is a bool and not a "_lastMouse == position" test on purpose: a cursor
        // genuinely sitting at (0,0) would make that test lie and reintroduce the same spike.
        bool _cursorSampled;
        float _wheelAccum;
        bool _focused = true;   // windows open focused, and OnFocusChanged keeps this in sync.

        /// <summary>
        /// True while the window owning this accumulator has OS input focus. Stamped onto every snapshot as
        /// <see cref="InputState.WindowFocused"/>, and read directly by the frame loop to decide the background
        /// throttle plan. Starts <c>true</c> because windows open focused.
        /// </summary>
        public bool IsFocused => _focused;

        /// <summary>Record a key going down. The press edge fires only on a real transition, so an OS auto-repeat
        /// that arrives as another down (rather than as <see cref="OnKeyRepeat"/>) never re-fires it.</summary>
        public void OnKeyDown(Key key)
        {
            if (key == Key.None) return;
            if (_keysDown.Add(key)) _pressed.Add(key);
        }

        /// <summary>Record a key going up. The release edge fires only if the key was actually held, so a
        /// duplicate or stale up (one arriving after focus loss already released it) is a no-op.</summary>
        public void OnKeyUp(Key key)
        {
            if (_keysDown.Remove(key)) _released.Add(key);
        }

        /// <summary>Record an OS auto-repeat tick for a held key. Surfaced as
        /// <see cref="InputState.KeysRepeated"/> and deliberately kept out of the press set.</summary>
        public void OnKeyRepeat(Key key)
        {
            if (key == Key.None) return;
            _repeated.Add(key);
        }

        /// <summary>Record a mouse button going down (press edge on the transition only).</summary>
        public void OnMouseDown(MouseButton button)
        {
            if (_mouseDown.Add(button)) _mousePressed.Add(button);
        }

        /// <summary>Record a mouse button going up. Mirrors <see cref="OnKeyUp"/>: the release edge fires only if
        /// the button was actually held, and lands in <see cref="InputState.MouseReleased"/>.</summary>
        public void OnMouseUp(MouseButton button)
        {
            if (_mouseDown.Remove(button)) _mouseReleased.Add(button);
        }

        /// <summary>Accumulate a wheel delta. Several ticks in one frame sum into a single
        /// <see cref="InputState.ScrollDelta"/>, which is cleared by <see cref="Snapshot"/>.</summary>
        public void OnScroll(float wheelDelta) => _wheelAccum += wheelDelta;

        /// <summary>
        /// Record an OS focus change. On the transition to unfocused, everything currently held is moved into the
        /// released sets and the down sets are cleared, so a consumer sees one clean release edge for it.
        /// <para>Without that, a key or button whose up event the OS swallows while the window is in the
        /// background (Cmd-Tab mid-keypress, Mission Control, a global hotkey eating the release) reads as held
        /// forever once focus comes back, because no up event is ever delivered to clear it.</para>
        /// <para>Only a real value change does anything. A platform that re-reports the same focus state each
        /// poll cannot re-release a key the player pressed since.</para>
        /// </summary>
        public void OnFocusChanged(bool focused)
        {
            if (focused == _focused) return;
            _focused = focused;
            if (focused) return;

            foreach (Key key in _keysDown) _released.Add(key);
            _keysDown.Clear();
            foreach (MouseButton button in _mouseDown) _mouseReleased.Add(button);
            _mouseDown.Clear();
        }

        /// <summary>
        /// Publish this frame's immutable <see cref="InputState"/> and clear the per-frame edge sets, so each edge
        /// is visible for exactly one frame. The OS-read values come in as parameters, which is the seam: the
        /// caller does the platform reads and this type owns the state machine.
        /// </summary>
        /// <param name="cursorPosition">Cursor position ALREADY scaled into framebuffer pixels (not logical
        /// points). Ignored when <paramref name="hasMouse"/> is false.</param>
        /// <param name="hasMouse">Whether a mouse was present to read. When false the last known position is
        /// held and the delta is zero, rather than snapping the cursor to the origin.</param>
        /// <param name="width">Framebuffer width in pixels.</param>
        /// <param name="height">Framebuffer height in pixels.</param>
        /// <param name="gamepads">Connected gamepads this frame, or null for none.</param>
        public InputState Snapshot(
            Vector2 cursorPosition, bool hasMouse, int width, int height,
            IReadOnlyList<GamepadState>? gamepads = null)
        {
            Vector2 position = hasMouse ? cursorPosition : _lastMouse;
            // First sample reports no movement. See _cursorSampled for why this is not a position comparison.
            Vector2 delta = _cursorSampled ? position - _lastMouse : Vector2.Zero;

            var input = new InputState(
                new HashSet<Key>(_keysDown), new HashSet<Key>(_pressed), new HashSet<Key>(_released),
                new HashSet<MouseButton>(_mouseDown), new HashSet<MouseButton>(_mousePressed),
                position, delta, _wheelAccum, width, height,
                gamepads, windowFocused: _focused,
                repeated: new HashSet<Key>(_repeated),
                mouseReleased: new HashSet<MouseButton>(_mouseReleased));

            _pressed.Clear();
            _released.Clear();
            _repeated.Clear();
            _mousePressed.Clear();
            _mouseReleased.Clear();
            _lastMouse = position;
            // Only a frame that actually read a cursor primes the delta. A window that opens with no mouse
            // attached must still report a zero delta on the first frame one shows up.
            if (hasMouse) _cursorSampled = true;
            _wheelAccum = 0f;
            return input;
        }
    }
}
