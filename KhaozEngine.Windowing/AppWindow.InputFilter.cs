using System;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The input-filter seam of <see cref="AppWindow"/>: one optional hook applied to the snapshot
    /// <c>BuildInput()</c> just built, before the frame latches it. It is here rather than in <c>AppWindow.cs</c>
    /// because that file is at its size ceiling, and it is a distinct concern from window construction and wiring.
    /// </summary>
    public sealed partial class AppWindow
    {
        Func<InputState, InputState>? _inputFilter;

        /// <summary>
        /// An optional transform applied to each frame's input snapshot, immediately after <c>BuildInput()</c> and
        /// before the frame latches it, so every consumer downstream (both pointers, the GUI, the HUD, the game)
        /// sees ONE coherent frame. Null (the default) is the raw snapshot, unchanged and unallocated.
        /// <para>
        /// This is a composition seam, not an input source. It never reaches the accumulator and never touches a
        /// Silk or GLFW static, so the rule that this class is the only one near those statics holds exactly. What a
        /// filter CAN do is return a different immutable <see cref="InputState"/>: union extra keys or buttons into
        /// the real sets, override the pointer position, or force <see cref="InputState.WindowFocused"/>. That is
        /// what <c>KhaozEngine.Automation</c>'s dev-only endpoint uses it for, and it is the reason the seam exists.
        /// </para>
        /// <para>
        /// A delegate rather than an interface: the contract is one method with one caller and no state the window
        /// has to own, it composes without a new public type (<c>window.InputFilter = s =&gt; second(first(s))</c>),
        /// and it matches the delegate-shaped callbacks the frame loop already takes. Settable any time, and it
        /// takes effect on the next frame. It runs on the window thread once per frame, so a filter that blocks
        /// stalls the loop.
        /// </para>
        /// </summary>
        public Func<InputState, InputState>? InputFilter
        {
            get => _inputFilter;
            set => _inputFilter = value;
        }

        /// <summary>The seam's whole body, pulled out so it is assertable without standing up a window.</summary>
        internal static InputState ApplyInputFilter(Func<InputState, InputState>? filter, InputState built) =>
            filter is null ? built : filter(built);
    }
}
