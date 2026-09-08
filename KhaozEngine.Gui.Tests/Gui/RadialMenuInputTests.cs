using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AllocSensitive")]
    public sealed class RadialMenuInputTests
    {
        static readonly Rect Safe = new(0f, 0f, 960f, 540f);
        static readonly Vector2 Anchor = new(480f, 270f);
        static readonly Vector2 Outside = new(850f, 500f);
        static readonly RadialMenuMetrics Metrics = RadialMenuMetrics.Default;

        readonly MouseFrames _mouse = new();

        static RadialMenuEntry[] Entries() =>
        [
            new(LocalizedText.Raw("Stew"), 101, InitialChoiceTag: 10),
            new(LocalizedText.Raw("Pie"), 102, InitialChoiceTag: 11),
            new(LocalizedText.Raw("Cake"), 103, Enabled: false, InitialChoiceTag: 10),
            new(LocalizedText.Raw("Soup"), 104, InitialChoiceTag: 13),
        ];

        static RadialMenuChoice[] Choices() =>
        [
            new(LocalizedText.Raw("One"), 10),
            new(LocalizedText.Raw("Two"), 11),
            new(LocalizedText.Raw("Three"), 12, Enabled: false),
            new(LocalizedText.Raw("Four"), 13),
        ];

        static RadialMenu OpenRecipes()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            menu.Open(LocalizedText.Raw("Recipes"), Entries(), Anchor, Choices());
            return menu;
        }

        InputState MouseFrame(Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = _mouse.Advance(held);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, 960, 540,
                mouseReleased: released);
        }

        static InputState KeyFrame(Key key) => new(
            new HashSet<Key> { key }, new HashSet<Key> { key }, new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 960, 540);

        static InputState KeyFrame(Key key, Vector2 pointerPosition) => new(
            new HashSet<Key> { key }, new HashSet<Key> { key }, new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            pointerPosition, Vector2.Zero, 0f, 960, 540);

        static InputState PadFrame(int playerIndex, GamepadButton button)
        {
            var down = new HashSet<GamepadButton> { button };
            var pads = new GamepadState[playerIndex + 1];
            for (int i = 0; i < playerIndex; i++) pads[i] = GamepadState.Disconnected;
            pads[playerIndex] = new GamepadState(
                playerIndex, down, down, new HashSet<GamepadButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 0f);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 960, 540, pads);
        }

        static Vector2 EntryPoint(int index) =>
            RadialMenu.LabelPoint(Anchor, index, Entries().Length, Metrics);

        static Vector2 ChoicePoint(int index)
        {
            Rect bounds = RadialMenu.ChoiceBounds(Anchor, Choices().Length, index, Metrics);
            return new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
        }

        void Update(RadialMenu menu, Pointer pointer, Vector2 at, bool down)
        {
            pointer.Update(MouseFrame(at, down));
            menu.Update(pointer, 1f / 60f);
        }

        void Hover(RadialMenu menu, Pointer pointer, Vector2 at) => Update(menu, pointer, at, false);

        void Tap(RadialMenu menu, Pointer pointer, Vector2 at)
        {
            Update(menu, pointer, at, false);
            Update(menu, pointer, at, true);
            Update(menu, pointer, at, false);
        }

        void TapChoice(RadialMenu menu, Pointer pointer, long choiceTag)
        {
            int index = Array.FindIndex(Choices(), choice => choice.Tag == choiceTag);
            Assert.True(index >= 0);
            Tap(menu, pointer, ChoicePoint(index));
        }

        static void Send(RadialMenu menu, InputManager input, Key key, bool focused = true)
        {
            input.Update(KeyFrame(key));
            menu.Update(input, 1f / 60f, focused);
        }

        static void Send(
            RadialMenu menu,
            InputManager input,
            int playerIndex,
            GamepadButton button,
            PlayerIndex? player = null)
        {
            input.Update(PadFrame(playerIndex, button));
            menu.Update(input, 1f / 60f, focused: true, player);
        }

        [Fact]
        public void Opening_gesture_cannot_select_or_dismiss()
        {
            var pointer = new Pointer();
            pointer.Update(MouseFrame(EntryPoint(1), true));
            RadialMenu menu = OpenRecipes();
            pointer.Update(MouseFrame(EntryPoint(1), false));

            menu.Update(pointer, 1f / 60f);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasDismissed);

            pointer = new Pointer();
            pointer.Update(MouseFrame(Outside, true));
            menu = OpenRecipes();
            pointer.Update(MouseFrame(Outside, false));

            menu.Update(pointer, 1f / 60f);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasDismissed);
        }

        [Fact]
        public void Fresh_tap_selects_an_enabled_wedge_and_returns_both_tags()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();

            Tap(menu, pointer, EntryPoint(1));

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(102, 11), menu.Selection);
        }

        [Fact]
        public void Disabled_wedge_becomes_active_for_inspection_but_does_not_select()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();

            Hover(menu, pointer, EntryPoint(2));
            Assert.Equal(2, menu.HoverIndex);
            Assert.Equal(2, menu.ActiveIndex);

            Tap(menu, pointer, EntryPoint(2));

            Assert.True(menu.IsOpen);
            Assert.Equal(2, menu.ActiveIndex);
            Assert.False(menu.WasSelected);
        }

        [Fact]
        public void Footer_tap_changes_only_the_active_entry_choice_and_leaves_menu_open()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();
            Hover(menu, pointer, EntryPoint(1));

            TapChoice(menu, pointer, choiceTag: 10);

            Assert.True(menu.IsOpen);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);

            Hover(menu, pointer, EntryPoint(0));
            Tap(menu, pointer, EntryPoint(0));
            Assert.Equal(new RadialMenuSelection(101, 10), menu.Selection);
        }

        [Fact]
        public void Switching_entries_restores_each_entries_own_choice()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();
            Hover(menu, pointer, EntryPoint(1));
            TapChoice(menu, pointer, choiceTag: 10);

            Hover(menu, pointer, EntryPoint(3));
            Hover(menu, pointer, EntryPoint(1));
            Tap(menu, pointer, EntryPoint(1));

            Assert.Equal(new RadialMenuSelection(102, 10), menu.Selection);
        }

        [Fact]
        public void Outside_release_dismisses_and_consumes_the_gesture()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();

            Tap(menu, pointer, Outside);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.True(pointer.IsConsumed);
        }

        [Fact]
        public void Complete_composition_blocks_its_bounds()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();

            Hover(menu, pointer, Outside);

            Assert.True(pointer.IsBlocked(new Vector2(menu.Bounds.X + 1f, menu.Bounds.Y + 1f)));
            Assert.True(pointer.IsBlocked(new Vector2(menu.Bounds.Right - 1f, menu.Bounds.Bottom - 1f)));
        }

        [Fact]
        public void SetEntryChoice_changes_state_without_firing_frame_flag()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();

            Assert.True(menu.SetEntryChoice(102, 13));
            Assert.False(menu.WasChoiceChanged);
            Assert.False(menu.SetEntryChoice(999, 13));
            Assert.False(menu.SetEntryChoice(102, 999));

            Tap(menu, pointer, EntryPoint(1));
            Assert.Equal(new RadialMenuSelection(102, 13), menu.Selection);
        }

        [Fact]
        public void Frame_flags_clear_on_the_next_update()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();
            Hover(menu, pointer, EntryPoint(1));
            TapChoice(menu, pointer, choiceTag: 10);
            Assert.True(menu.WasChoiceChanged);

            Hover(menu, pointer, Outside);
            Assert.False(menu.WasChoiceChanged);
            Assert.Equal(default, menu.ChoiceChange);

            Tap(menu, pointer, Outside);
            Assert.True(menu.WasDismissed);

            Hover(menu, pointer, Outside);
            Assert.False(menu.WasDismissed);

            menu = OpenRecipes();
            pointer = new Pointer();
            Tap(menu, pointer, EntryPoint(0));
            Assert.True(menu.WasSelected);

            Hover(menu, pointer, Outside);
            Assert.False(menu.WasSelected);
            Assert.Equal(default, menu.Selection);
        }

        [Fact]
        public void Open_rejects_duplicate_tags_and_invalid_initial_choices()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            RadialMenuEntry[] duplicateEntries =
            [
                new(LocalizedText.Raw("A"), 1),
                new(LocalizedText.Raw("B"), 1),
            ];
            RadialMenuChoice[] duplicateChoices =
            [
                new(LocalizedText.Raw("A"), 10),
                new(LocalizedText.Raw("B"), 10),
            ];

            Assert.Throws<ArgumentException>(() => menu.Open(
                LocalizedText.Raw("Bad"), duplicateEntries, Anchor));
            Assert.Throws<ArgumentException>(() => menu.Open(
                LocalizedText.Raw("Bad"), Entries(), Anchor, duplicateChoices));
            Assert.Throws<ArgumentException>(() => menu.Open(
                LocalizedText.Raw("Bad"),
                [new RadialMenuEntry(LocalizedText.Raw("A"), 1, InitialChoiceTag: 99)],
                Anchor,
                Choices()));
            Assert.Throws<ArgumentException>(() => menu.Open(
                LocalizedText.Raw("Bad"),
                [new RadialMenuEntry(LocalizedText.Raw("A"), 1, InitialChoiceTag: 12)],
                Anchor,
                Choices()));
        }

        [Fact]
        public void Left_and_right_cycle_enabled_wedges_with_wrap()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();
            Assert.Equal(0, menu.ActiveIndex);

            Send(menu, input, Key.Left);
            Assert.Equal(3, menu.ActiveIndex);
            Send(menu, input, Key.Right);
            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, Key.Right);
            Assert.Equal(1, menu.ActiveIndex);
            Send(menu, input, Key.Right);
            Assert.Equal(3, menu.ActiveIndex);
        }

        [Fact]
        public void Down_enters_footer_and_up_returns_to_wedges()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, Key.Down);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuChoiceChange(101, 11), menu.ChoiceChange);

            Send(menu, input, Key.Up);
            Send(menu, input, Key.Right);
            Assert.Equal(1, menu.ActiveIndex);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Footer_navigation_wraps_and_skips_disabled_choices()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, Key.Down);
            Send(menu, input, Key.Left);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuChoiceChange(101, 13), menu.ChoiceChange);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuChoiceChange(101, 10), menu.ChoiceChange);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuChoiceChange(101, 11), menu.ChoiceChange);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuChoiceChange(101, 13), menu.ChoiceChange);
        }

        [Fact]
        public void Stationary_pointer_over_active_wedge_does_not_steal_footer_focus()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();
            Vector2 stationaryPointer = EntryPoint(0);

            input.Update(KeyFrame(Key.Down, stationaryPointer));
            menu.Update(input, 1f / 60f, focused: true);
            input.Update(KeyFrame(Key.Left, stationaryPointer));
            menu.Update(input, 1f / 60f, focused: true);

            Assert.Equal(0, menu.ActiveIndex);
            input.Update(KeyFrame(Key.Enter, stationaryPointer));
            menu.Update(input, 1f / 60f, focused: true);
            Assert.Equal(new RadialMenuChoiceChange(101, 13), menu.ChoiceChange);
        }

        [Fact]
        public void Pointer_hover_does_not_replace_retained_keyboard_wedge_focus()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();
            Send(menu, input, Key.Right);

            input.Update(KeyFrame(Key.F1, EntryPoint(3)));
            menu.Update(input, 1f / 60f, focused: true);
            Assert.Equal(3, menu.HoverIndex);
            Assert.Equal(3, menu.ActiveIndex);

            input.Update(KeyFrame(Key.Enter, EntryPoint(3)));
            menu.Update(input, 1f / 60f, focused: true);

            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(102, 11), menu.Selection);
        }

        [Fact]
        public void Pointer_hover_does_not_steal_retained_footer_focus()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Down);

            input.Update(KeyFrame(Key.F1, EntryPoint(3)));
            menu.Update(input, 1f / 60f, focused: true);
            input.Update(KeyFrame(Key.Left, EntryPoint(3)));
            menu.Update(input, 1f / 60f, focused: true);
            input.Update(KeyFrame(Key.Enter, EntryPoint(3)));
            menu.Update(input, 1f / 60f, focused: true);

            Assert.True(menu.IsOpen);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);
        }

        [Fact]
        public void All_disabled_entries_keep_first_entry_inspectable_but_never_selectable()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            menu.Open(
                LocalizedText.Raw("Unavailable"),
                [
                    new RadialMenuEntry(LocalizedText.Raw("First"), 201, Enabled: false),
                    new RadialMenuEntry(LocalizedText.Raw("Second"), 202, Enabled: false),
                ],
                Anchor);
            var input = new InputManager();

            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, Key.Right);
            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, Key.Enter);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);

            Send(menu, input, 0, GamepadButton.DpadLeft);
            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, 0, GamepadButton.A);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
        }

        [Fact]
        public void All_disabled_footer_retains_default_zero_and_ignores_pointer_and_keyboard()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            RadialMenuChoice[] disabledChoices =
            [
                new RadialMenuChoice(LocalizedText.Raw("One"), 0, Enabled: false),
                new RadialMenuChoice(LocalizedText.Raw("Two"), 11, Enabled: false),
            ];
            menu.Open(
                LocalizedText.Raw("Unavailable amounts"),
                [new RadialMenuEntry(LocalizedText.Raw("Recipe"), 501)],
                Anchor,
                disabledChoices);
            Assert.Equal(-1, menu.FocusedChoiceIndex);
            var pointer = new Pointer();
            Rect firstChoice = RadialMenu.ChoiceBounds(Anchor, disabledChoices.Length, 0, Metrics);
            Vector2 firstChoicePoint = new(
                firstChoice.X + firstChoice.Width / 2f,
                firstChoice.Y + firstChoice.Height / 2f);

            Tap(menu, pointer, firstChoicePoint);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasChoiceChanged);

            var input = new InputManager();
            Send(menu, input, Key.Down);
            Send(menu, input, Key.Right);
            Assert.Equal(-1, menu.FocusedChoiceIndex);
            Send(menu, input, Key.Enter);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasChoiceChanged);

            Send(menu, input, Key.Up);
            Send(menu, input, Key.Enter);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(501, 0), menu.Selection);
        }

        [Fact]
        public void Menu_select_activates_the_focused_wedge_or_footer_control()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, Key.Right);
            Send(menu, input, Key.Down);
            Send(menu, input, Key.Left);
            Send(menu, input, Key.Enter);
            Assert.True(menu.IsOpen);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);

            Send(menu, input, Key.Up);
            Send(menu, input, Key.Enter);
            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(102, 10), menu.Selection);
        }

        [Fact]
        public void Menu_cancel_dismisses_and_unfocused_navigation_is_ignored()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, Key.Right, focused: false);
            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, Key.Escape);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
        }

        [Fact]
        public void Gamepad_navigation_uses_the_requested_player_and_skips_disabled_entries()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, 1, GamepadButton.DpadRight, PlayerIndex.One);
            Assert.Equal(0, menu.ActiveIndex);
            Send(menu, input, 1, GamepadButton.DpadRight, PlayerIndex.Two);
            Assert.Equal(1, menu.ActiveIndex);
            Send(menu, input, 1, GamepadButton.DpadRight, PlayerIndex.Two);
            Assert.Equal(3, menu.ActiveIndex);
            Send(menu, input, 1, GamepadButton.A, PlayerIndex.Two);

            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(104, 13), menu.Selection);
        }

        [Fact]
        public void Gamepad_cancel_dismisses()
        {
            RadialMenu menu = OpenRecipes();
            var input = new InputManager();

            Send(menu, input, 0, GamepadButton.B);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
        }

        [Fact]
        public void Warm_update_allocates_nothing()
        {
            RadialMenu menu = OpenRecipes();
            var pointer = new Pointer();
            Update(menu, pointer, Anchor, false);
            Update(menu, pointer, Anchor, true);
            Update(menu, pointer, Anchor, false);
            Update(menu, pointer, Anchor, false);
            for (int i = 0; i < 20; i++) menu.Update(pointer, 1f / 60f);

            AllocAssert.NoPerCallAllocation(
                "RadialMenu.Update(Pointer)",
                () => menu.Update(pointer, 1f / 60f));
        }
    }
}
