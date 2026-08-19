using System.Collections.Generic;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests
{
    /// <summary>
    /// Derives one frame's mouse press and release EDGES from the set of buttons a test says are held, so a
    /// test's local <see cref="InputState"/> builder hands the engine the same
    /// <see cref="InputState.MousePressed"/> / <see cref="InputState.MouseReleased"/> sets a real window
    /// would.
    /// <para>Around thirty test files used to pass an empty set for both while filling
    /// <see cref="InputState.MouseDown"/> from a <c>down</c> bool. That models a HELD button and never a press
    /// EDGE, so no test in those files could exercise press-edge behaviour through the mouse at all, which is
    /// how a <see cref="Pointer"/> defect that only shows up on the edge went unnoticed. See
    /// KhaozEngine#300.</para>
    /// <para>Hold ONE per test-class INSTANCE (xUnit builds a fresh instance per fact), never a static: it
    /// carries the previous frame's held set, so a static would leak one test's gesture into the next and
    /// race across the classes xUnit runs in parallel.</para>
    /// </summary>
    public sealed class MouseFrames
    {
        HashSet<MouseButton> _held = new();

        /// <summary>
        /// Consumes <paramref name="held"/> as this frame's held buttons and returns the edges against the
        /// previous frame: everything newly held is a press, everything no longer held is a release. Both sets
        /// are fresh, so the caller may hand them straight to an <see cref="InputState"/> and keep them.
        /// </summary>
        public (HashSet<MouseButton> Pressed, HashSet<MouseButton> Released) Advance(IReadOnlySet<MouseButton> held)
        {
            var pressed = new HashSet<MouseButton>(held);
            pressed.ExceptWith(_held);
            var released = new HashSet<MouseButton>(_held);
            released.ExceptWith(held);
            _held = new HashSet<MouseButton>(held);
            return (pressed, released);
        }

        /// <summary>
        /// The single-button shorthand for the common test helper shape, a <c>leftDown</c>-style bool per
        /// button: the edges for a frame where <paramref name="button"/> is held or not.
        /// </summary>
        public (HashSet<MouseButton> Pressed, HashSet<MouseButton> Released) Advance(MouseButton button, bool held)
        {
            var set = new HashSet<MouseButton>();
            if (held) set.Add(button);
            return Advance(set);
        }
    }
}
