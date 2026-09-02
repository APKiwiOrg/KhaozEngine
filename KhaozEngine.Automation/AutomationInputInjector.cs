using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// The injected half of the input seam: what automation is currently holding, and how it merges with the real
    /// frame. Owned by <see cref="AutomationHost"/> and touched only on the window thread, so it needs no locking.
    /// <para>
    /// The merge is a UNION, never a replacement. A real key held by the developer stays held while automation
    /// clicks, so an automated run and a hand-driven one can share a window. The two exceptions are deliberate: the
    /// pointer POSITION is overridden while an injected pointer is live (two cursors cannot both be the cursor), and
    /// <see cref="InputState.WindowFocused"/> is forced true, without which
    /// <c>GuiSurface</c> drops every injected press the moment the agent's terminal takes focus.
    /// </para>
    /// </summary>
    public sealed class AutomationInputInjector
    {
        readonly HashSet<Key> _keysDown = new();
        readonly HashSet<Key> _keysPressed = new();
        readonly HashSet<Key> _keysReleased = new();
        readonly HashSet<MouseButton> _buttonsDown = new();
        readonly HashSet<MouseButton> _buttonsPressed = new();
        readonly HashSet<MouseButton> _buttonsReleased = new();
        readonly Dictionary<Key, long> _keyHoldUntil = new();
        readonly Dictionary<MouseButton, long> _buttonHoldUntil = new();
        readonly List<Key> _expiredKeys = new();
        readonly List<MouseButton> _expiredButtons = new();

        Vector2? _pointer;
        Vector2 _lastComposedPosition;

        /// <summary>The live injected pointer position in window pixels, or null while the real cursor owns the frame.</summary>
        public Vector2? Pointer => _pointer;

        /// <summary>Keys automation is currently holding. Read-only view for tests and diagnostics.</summary>
        public IReadOnlyCollection<Key> HeldKeys => _keysDown;

        /// <summary>Mouse buttons automation is currently holding. Read-only view for tests and diagnostics.</summary>
        public IReadOnlyCollection<MouseButton> HeldButtons => _buttonsDown;

        /// <summary>
        /// Take over the pointer at <paramref name="position"/>, in window pixels (the same space as
        /// <see cref="InputState.MousePosition"/>, which <c>AppWindow</c> has already scaled out of Silk's logical
        /// points). Stays live until <see cref="ReleasePointer"/>.
        /// </summary>
        public void SetPointer(Vector2 position) => _pointer = position;

        /// <summary>Hand the pointer back to the real cursor.</summary>
        public void ReleasePointer() => _pointer = null;

        /// <summary>
        /// Press <paramref name="button"/> from this frame on. <paramref name="holdFrames"/> above zero schedules an
        /// automatic release <paramref name="holdFrames"/> frames later, so the press is live on
        /// <paramref name="frame"/> through <c>frame + holdFrames - 1</c> and the release edge lands on
        /// <c>frame + holdFrames</c>. Zero or less holds until an explicit <see cref="ReleaseButton"/>.
        /// </summary>
        public void PressButton(MouseButton button, long frame, int holdFrames)
        {
            if (_buttonsDown.Add(button)) _buttonsPressed.Add(button);
            _buttonsReleased.Remove(button);
            if (holdFrames > 0) _buttonHoldUntil[button] = frame + holdFrames;
            else _buttonHoldUntil.Remove(button);
        }

        /// <summary>
        /// Release <paramref name="button"/> on this frame, cancelling any pending auto-release. A press and a
        /// release applied inside ONE pump keep the press edge, so the frame carries both: that is a click, which is
        /// what a bridge sending press and release in one <c>input</c> batch means, and it is the same shape
        /// <c>Pointer</c> already completes as a same-frame tap (a snapshot whose <c>MousePressed</c> carries a
        /// button that is no longer down). Dropping the press edge instead made that click land as a bare release
        /// nothing acts on.
        /// </summary>
        public void ReleaseButton(MouseButton button)
        {
            _buttonHoldUntil.Remove(button);
            if (_buttonsDown.Remove(button)) _buttonsReleased.Add(button);
        }

        /// <summary>Press <paramref name="key"/>, with the same hold semantics as <see cref="PressButton"/>.</summary>
        public void PressKey(Key key, long frame, int holdFrames)
        {
            if (_keysDown.Add(key)) _keysPressed.Add(key);
            _keysReleased.Remove(key);
            if (holdFrames > 0) _keyHoldUntil[key] = frame + holdFrames;
            else _keyHoldUntil.Remove(key);
        }

        /// <summary>Release <paramref name="key"/> on this frame, cancelling any pending auto-release. Keeps a press
        /// edge applied in the same pump, for the reason on <see cref="ReleaseButton"/>.</summary>
        public void ReleaseKey(Key key)
        {
            _keyHoldUntil.Remove(key);
            if (_keysDown.Remove(key)) _keysReleased.Add(key);
        }

        /// <summary>
        /// Fire every auto-release due on or before <paramref name="frame"/>. Called at the top of the pump, BEFORE
        /// this frame's commands are applied, so a press arriving on the same frame a hold expires wins.
        /// </summary>
        public void ExpireHolds(long frame)
        {
            _expiredKeys.Clear();
            foreach (KeyValuePair<Key, long> entry in _keyHoldUntil)
                if (entry.Value <= frame) _expiredKeys.Add(entry.Key);
            foreach (Key key in _expiredKeys) ReleaseKey(key);

            _expiredButtons.Clear();
            foreach (KeyValuePair<MouseButton, long> entry in _buttonHoldUntil)
                if (entry.Value <= frame) _expiredButtons.Add(entry.Key);
            foreach (MouseButton button in _expiredButtons) ReleaseButton(button);
        }

        /// <summary>
        /// Merge the injected state into <paramref name="real"/> and return the snapshot the frame will actually see.
        /// Allocates one <see cref="InputState"/> per frame (and only the sets automation actually touched), which is
        /// a dev-only cost on a path that never exists in a shipping build.
        /// </summary>
        public InputState Compose(InputState real)
        {
            Vector2 position = _pointer ?? real.MousePosition;
            Vector2 delta = _pointer is null ? real.MouseDelta : position - _lastComposedPosition;
            _lastComposedPosition = position;

            return new InputState(
                Union(real.KeysDown, _keysDown),
                Union(real.KeysPressed, _keysPressed),
                Union(real.KeysReleased, _keysReleased),
                Union(real.MouseDown, _buttonsDown),
                Union(real.MousePressed, _buttonsPressed),
                position,
                delta,
                real.ScrollDelta,
                real.Width,
                real.Height,
                real.Gamepads,
                real.Touches,
                windowFocused: true,
                repeated: real.KeysRepeated,
                mouseReleased: Union(real.MouseReleased, _buttonsReleased));
        }

        /// <summary>Clear this frame's injected edges. Called after <see cref="Compose"/>, once the frame has read them.</summary>
        public void EndFrame()
        {
            _keysPressed.Clear();
            _keysReleased.Clear();
            _buttonsPressed.Clear();
            _buttonsReleased.Clear();
        }

        /// <summary>Union without allocating when automation is holding nothing of that kind, which is the common frame.</summary>
        static IReadOnlySet<T> Union<T>(IReadOnlySet<T> real, HashSet<T> injected)
        {
            if (injected.Count == 0) return real;
            if (real.Count == 0) return new HashSet<T>(injected);
            var merged = new HashSet<T>(real);
            merged.UnionWith(injected);
            return merged;
        }
    }
}
