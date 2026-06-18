using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Keyboard/gamepad focus cursor over a list of N widgets. Holds the focused index and moves it with
    /// wrap or clamp semantics; <see cref="Update"/> drives it from an <see cref="InputManager"/>'s vertical
    /// menu navigation (<see cref="InputManager.IsMenuUp"/>/<see cref="InputManager.IsMenuDown"/>). Pure index
    /// math, headless-testable: the caller decides what each index means (a Button, Slider, MenuEntry, ...)
    /// and draws the focused one highlighted. Companion to the otherwise pointer-only Gui widgets.
    /// </summary>
    public sealed class FocusNavigator
    {
        /// <summary>Number of focusable items.</summary>
        public int Count { get; private set; }
        /// <summary>The focused index, or -1 when the list is empty.</summary>
        public int Focused { get; private set; }
        /// <summary>When true (default) movement wraps around the ends; when false it clamps.</summary>
        public bool Wrap { get; set; } = true;

        public FocusNavigator(int count = 0, int focused = 0)
        {
            Count = count < 0 ? 0 : count;
            Focused = Clamp(focused);
        }

        /// <summary>Set the item count, re-clamping the focused index (-1 when empty).</summary>
        public void SetCount(int count)
        {
            Count = count < 0 ? 0 : count;
            Focused = Clamp(Focused);
        }

        /// <summary>Focus a specific index (clamped into range; -1 when empty).</summary>
        public void Focus(int index) => Focused = Clamp(index);

        /// <summary>Move focus to the next item (down). Wraps or clamps per <see cref="Wrap"/>.</summary>
        public void MoveNext()
        {
            if (Count == 0) return;
            Focused = Wrap ? (Focused + 1) % Count
                           : (Focused + 1 >= Count ? Count - 1 : Focused + 1);
        }

        /// <summary>Move focus to the previous item (up). Wraps or clamps per <see cref="Wrap"/>.</summary>
        public void MovePrevious()
        {
            if (Count == 0) return;
            Focused = Wrap ? (Focused - 1 + Count) % Count
                           : (Focused - 1 < 0 ? 0 : Focused - 1);
        }

        /// <summary>
        /// Drive focus from this frame's menu navigation: menu-down moves to the next item, menu-up to the
        /// previous. Returns true if focus changed this frame.
        /// </summary>
        public bool Update(InputManager input, PlayerIndex? player = null)
        {
            if (Count == 0) return false;
            if (input.IsMenuDown(player)) { MoveNext(); return true; }
            if (input.IsMenuUp(player)) { MovePrevious(); return true; }
            return false;
        }

        int Clamp(int index)
        {
            if (Count == 0) return -1;
            if (index < 0) return 0;
            return index >= Count ? Count - 1 : index;
        }
    }
}
