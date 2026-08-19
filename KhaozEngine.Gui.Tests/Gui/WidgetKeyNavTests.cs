using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Keyboard/gamepad navigation for the retained Toggle, Slider, and Dropdown widgets (additive,
    /// opt-in, layered on top of the pointer path). Frames are built headlessly and fed through an
    /// <see cref="InputManager"/>, mirroring <c>WindowingInputManagerTests</c> and <c>FocusNavigatorTests</c>.
    /// </summary>
    public class WidgetKeyNavTests
    {
        // ---- headless frame helpers ----------------------------------------

        static InputState Keys(IEnumerable<Key>? pressed = null, params GamepadState[] pads)
        {
            var p = new HashSet<Key>(pressed ?? Array.Empty<Key>());
            var d = new HashSet<Key>(p); // a pressed key is also down this frame
            return new InputState(d, p, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 960, 540, pads);
        }

        static GamepadState Pad(int index, params GamepadButton[] pressed)
        {
            var pr = new HashSet<GamepadButton>(pressed);
            return new GamepadState(index, new HashSet<GamepadButton>(pr), pr,
                new HashSet<GamepadButton>(), Vector2.Zero, Vector2.Zero, 0f, 0f);
        }

        static InputManager Im(InputState frame)
        {
            var im = new InputManager();
            im.Update(frame);
            return im;
        }

        static readonly Rect ToggleBox = new(10, 10, 40, 20);
        static readonly Rect SliderBox = new(10, 50, 100, 10);
        static readonly Rect DropTrigger = new(100, 100, 160, 30);

        static List<DropdownOption> Opts() => new()
        {
            new("Low", 1), new("Medium", 2), new("High", 3),
        };

        // ==== Toggle =========================================================

        [Fact]
        public void Flip_toggles_state_and_fires_OnChanged()
        {
            bool? notified = null;
            var t = new Toggle(ToggleBox, isOn: false, v => notified = v);
            Assert.True(t.Flip());
            Assert.True(t.IsOn);
            Assert.Equal(true, notified);
        }

        [Fact]
        public void Flip_is_a_noop_when_disabled()
        {
            var t = new Toggle(ToggleBox, isOn: false) { Enabled = false };
            Assert.False(t.Flip());
            Assert.False(t.IsOn);
        }

        [Fact]
        public void Set_changes_only_when_the_value_differs()
        {
            int calls = 0;
            var t = new Toggle(ToggleBox, isOn: false, _ => calls++);
            Assert.True(t.Set(true));   // false -> true
            Assert.False(t.Set(true));  // already true, no change/notify
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Focused_menu_select_flips_the_toggle()
        {
            var t = new Toggle(ToggleBox, isOn: false);
            Assert.True(t.Update(Im(Keys(new[] { Key.Enter })), focused: true));
            Assert.True(t.IsOn);
        }

        [Fact]
        public void Focused_select_next_turns_on_previous_turns_off()
        {
            var t = new Toggle(ToggleBox, isOn: false);
            Assert.True(t.Update(Im(Keys(new[] { Key.Right })), focused: true));
            Assert.True(t.IsOn);
            Assert.False(t.Update(Im(Keys(new[] { Key.Right })), focused: true)); // already on
            Assert.True(t.Update(Im(Keys(new[] { Key.Left })), focused: true));
            Assert.False(t.IsOn);
        }

        [Fact]
        public void Unfocused_toggle_ignores_the_keyboard()
        {
            var t = new Toggle(ToggleBox, isOn: false);
            Assert.False(t.Update(Im(Keys(new[] { Key.Enter })), focused: false));
            Assert.False(t.IsOn);
        }

        [Fact]
        public void Gamepad_a_flips_the_focused_toggle()
        {
            var t = new Toggle(ToggleBox, isOn: false);
            Assert.True(t.Update(Im(Keys(pads: new[] { Pad(0, GamepadButton.A) })), focused: true));
            Assert.True(t.IsOn);
        }

        // ==== Slider =========================================================

        [Fact]
        public void Focused_select_next_nudges_the_slider_up()
        {
            var s = new Slider(SliderBox, 0.5f) { NudgeStep = 0.1f };
            Assert.True(s.Update(Im(Keys(new[] { Key.Right })), focused: true));
            Assert.Equal(0.6f, s.Value, 3);
        }

        [Fact]
        public void Focused_select_previous_nudges_the_slider_down()
        {
            var s = new Slider(SliderBox, 0.5f) { NudgeStep = 0.1f };
            Assert.True(s.Update(Im(Keys(new[] { Key.Left })), focused: true));
            Assert.Equal(0.4f, s.Value, 3);
        }

        [Fact]
        public void Unfocused_slider_ignores_the_keyboard()
        {
            var s = new Slider(SliderBox, 0.5f);
            Assert.False(s.Update(Im(Keys(new[] { Key.Right })), focused: false));
            Assert.Equal(0.5f, s.Value, 3);
        }

        // ==== Dropdown =======================================================

        [Fact]
        public void Focused_menu_select_opens_the_closed_dropdown()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Update(Im(Keys(new[] { Key.Enter })), focused: true);
            Assert.True(d.IsOpen);
        }

        [Fact]
        public void Open_seeds_the_highlight_to_the_selection()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.SelectByValue(2); // index 1
            d.Open();
            Assert.True(d.IsOpen);
            Assert.Equal(1, d.HighlightedIndex);
        }

        [Fact]
        public void HighlightNext_advances_and_wraps()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Open();                       // highlight 0
            Assert.True(d.HighlightNext());
            Assert.Equal(1, d.HighlightedIndex);
            d.HighlightNext();              // 2
            Assert.True(d.HighlightNext()); // wraps to 0
            Assert.Equal(0, d.HighlightedIndex);
        }

        [Fact]
        public void HighlightPrevious_clamps_at_zero_when_wrap_is_off()
        {
            var d = new Dropdown(Opts(), DropTrigger) { Wrap = false };
            d.Open();                            // highlight 0
            Assert.False(d.HighlightPrevious()); // clamped, no move
            Assert.Equal(0, d.HighlightedIndex);
        }

        [Fact]
        public void Focused_menu_down_moves_the_highlight_when_open()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Open();
            d.Update(Im(Keys(new[] { Key.Down })), focused: true);
            Assert.Equal(1, d.HighlightedIndex);
            Assert.Equal(0, d.SelectedIndex); // selection unchanged until commit
        }

        [Fact]
        public void CommitHighlight_selects_and_closes()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Open();
            d.HighlightNext();                 // highlight index 1
            Assert.True(d.CommitHighlight());
            Assert.False(d.IsOpen);
            Assert.True(d.WasChanged);
            Assert.Equal(2, d.SelectedValue);  // "Medium"
        }

        [Fact]
        public void Focused_select_commits_the_open_highlight()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Open();
            d.Update(Im(Keys(new[] { Key.Down })), focused: true);   // highlight 1
            bool changed = d.Update(Im(Keys(new[] { Key.Enter })), focused: true); // commit
            Assert.True(changed);
            Assert.False(d.IsOpen);
            Assert.Equal(1, d.SelectedIndex);
        }

        [Fact]
        public void Focused_menu_cancel_closes_the_open_list_without_changing()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Open();
            d.HighlightNext();
            d.Update(Im(Keys(new[] { Key.Escape })), focused: true);
            Assert.False(d.IsOpen);
            Assert.Equal(0, d.SelectedIndex);
            Assert.False(d.WasChanged);
        }

        [Fact]
        public void Focused_select_next_steps_selection_inline_while_closed()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            bool changed = d.Update(Im(Keys(new[] { Key.Right })), focused: true);
            Assert.True(changed);
            Assert.False(d.IsOpen);            // stays closed, cycles in place
            Assert.True(d.WasChanged);
            Assert.Equal(1, d.SelectedIndex);
        }

        [Fact]
        public void StepSelection_clamps_at_the_last_option_when_wrap_is_off()
        {
            var d = new Dropdown(Opts(), DropTrigger) { Wrap = false };
            d.SelectByValue(3);                 // index 2, the last
            Assert.False(d.StepSelection(1));   // clamped
            Assert.Equal(2, d.SelectedIndex);
        }

        [Fact]
        public void Unfocused_dropdown_ignores_the_keyboard()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Update(Im(Keys(new[] { Key.Enter })), focused: false);
            Assert.False(d.IsOpen);
        }

        [Fact]
        public void Pointer_opened_list_leaves_the_keyboard_highlight_inactive()
        {
            // Pointer-only callers must not get a stray keyboard highlight (byte-identical overlay draw).
            var d = new Dropdown(Opts(), DropTrigger);
            var p = new Pointer();
            var pt = new Vector2(150, 115); // inside the trigger
            p.Update(Frame(pt, false)); d.Update(p);
            p.Update(Frame(pt, true)); d.Update(p);
            p.Update(Frame(pt, false)); d.Update(p);
            Assert.True(d.IsOpen);
            Assert.Equal(-1, d.HighlightedIndex);
        }

        [Fact]
        public void Gamepad_dpad_navigates_and_commits_the_open_list()
        {
            var d = new Dropdown(Opts(), DropTrigger);
            d.Update(Im(Keys(pads: new[] { Pad(0, GamepadButton.A) })), focused: true);      // open
            Assert.True(d.IsOpen);
            d.Update(Im(Keys(pads: new[] { Pad(0, GamepadButton.DpadDown) })), focused: true); // highlight 1
            d.Update(Im(Keys(pads: new[] { Pad(0, GamepadButton.DpadDown) })), focused: true); // highlight 2
            d.Update(Im(Keys(pads: new[] { Pad(0, GamepadButton.A) })), focused: true);        // commit
            Assert.False(d.IsOpen);
            Assert.Equal(3, d.SelectedValue); // "High"
        }

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }
    }
}
