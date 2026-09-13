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
    public sealed class RadialMenuInteractionModeTests
    {
        static readonly Rect Safe = new(0f, 0f, 960f, 540f);
        static readonly Vector2 Anchor = new(480f, 270f);
        static readonly RadialMenuMetrics Metrics = RadialMenuMetrics.Default;

        readonly MouseFrames _mouse = new();

        static RadialMenuEntry[] Entries() =>
        [
            new(LocalizedText.Raw("Arrow shafts"), 101, InitialChoiceTag: 1),
            new(LocalizedText.Raw("Joinery pegs"), 102, InitialChoiceTag: 5),
            new(
                LocalizedText.Raw("Wooden handle"),
                103,
                Enabled: false,
                Detail: LocalizedText.Raw("Requires level 4"),
                InitialChoiceTag: 1),
            new(LocalizedText.Raw("Wooden light bow stave"), 104, InitialChoiceTag: -1),
        ];

        static RadialMenuChoice[] Choices() =>
        [
            new(LocalizedText.Raw("1"), 1),
            new(LocalizedText.Raw("5"), 5),
            new(LocalizedText.Raw("10"), 10),
            new(LocalizedText.Raw("All"), -1),
        ];

        static RadialMenu Open(RadialMenuInteractionMode mode = RadialMenuInteractionMode.EntryThenChoice)
        {
            var menu = new RadialMenu
            {
                SafeBounds = Safe,
                InteractionMode = mode,
            };
            menu.Open(LocalizedText.Raw("What would you like to make?"), Entries(), Anchor, Choices());
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

        static Vector2 EntryPoint(int index) =>
            RadialMenu.LabelPoint(Anchor, index, Entries().Length, Metrics);

        static Vector2 ChoicePoint(long tag)
        {
            int index = Array.FindIndex(Choices(), choice => choice.Tag == tag);
            Assert.True(index >= 0);
            Rect bounds = RadialMenu.ChoiceBounds(Anchor, Choices().Length, index, Metrics);
            return new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
        }

        bool Update(RadialMenu menu, Pointer pointer, Vector2 position, bool down)
        {
            pointer.Update(MouseFrame(position, down));
            return menu.Update(pointer, 1f / 60f);
        }

        bool Tap(RadialMenu menu, Pointer pointer, Vector2 position)
        {
            Update(menu, pointer, position, false);
            Update(menu, pointer, position, true);
            return Update(menu, pointer, position, false);
        }

        static void Send(RadialMenu menu, InputManager input, Key key)
        {
            input.Update(KeyFrame(key));
            menu.Update(input, 1f / 60f, focused: true);
        }

        static bool SendReturning(RadialMenu menu, InputManager input, Key key)
        {
            input.Update(KeyFrame(key));
            return menu.Update(input, 1f / 60f, focused: true);
        }

        static void SendAt(RadialMenu menu, InputManager input, Key key, Vector2 pointerPosition)
        {
            input.Update(KeyFrame(key, pointerPosition));
            menu.Update(input, 1f / 60f, focused: true);
        }

        [Fact]
        public void Locked_entry_survives_crossing_another_wedge_to_choose_amount()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Assert.False(Tap(menu, pointer, EntryPoint(0)));
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);

            Update(menu, pointer, EntryPoint(1), false);
            Assert.True(Tap(menu, pointer, ChoicePoint(10)));

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(101, 10), menu.Selection);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(101, 10), menu.ChoiceChange);
        }

        [Fact]
        public void Clicking_another_enabled_entry_replaces_the_lock()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Tap(menu, pointer, EntryPoint(0));
            Tap(menu, pointer, EntryPoint(3));
            Tap(menu, pointer, ChoicePoint(5));

            Assert.Equal(new RadialMenuSelection(104, 5), menu.Selection);
            Assert.Equal(new RadialMenuChoiceChange(104, 5), menu.ChoiceChange);
        }

        [Fact]
        public void Disabled_entry_cannot_replace_a_locked_entry()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Tap(menu, pointer, EntryPoint(0));
            Tap(menu, pointer, EntryPoint(2));
            Tap(menu, pointer, ChoicePoint(10));

            Assert.Equal(new RadialMenuSelection(101, 10), menu.Selection);
            Assert.Equal(new RadialMenuChoiceChange(101, 10), menu.ChoiceChange);
        }

        [Fact]
        public void Footer_is_non_actionable_until_an_entry_is_locked()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Assert.False(Tap(menu, pointer, ChoicePoint(10)));

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Immediate_mode_remains_the_default_and_commits_the_wedge()
        {
            var menu = new RadialMenu { SafeBounds = Safe };
            menu.Open(LocalizedText.Raw("Recipes"), Entries(), Anchor, Choices());
            var pointer = new Pointer();

            Assert.Equal(RadialMenuInteractionMode.Immediate, menu.InteractionMode);
            Assert.True(Tap(menu, pointer, EntryPoint(1)));

            Assert.False(menu.IsOpen);
            Assert.Equal(new RadialMenuSelection(102, 5), menu.Selection);
        }

        [Fact]
        public void Keyboard_select_locks_entry_then_footer_select_commits_amount()
        {
            RadialMenu menu = Open();
            var input = new InputManager();

            Send(menu, input, Key.Enter);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);

            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);
            Send(menu, input, Key.Down);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(102, 10), menu.Selection);
            Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);
        }

        [Fact]
        public void Keyboard_footer_commit_reports_selection()
        {
            RadialMenu menu = Open();
            var input = new InputManager();

            Send(menu, input, Key.Enter);
            Send(menu, input, Key.Down);

            Assert.True(SendReturning(menu, input, Key.Enter));
            Assert.True(menu.WasSelected);
        }

        [Fact]
        public void Immediate_mode_keyboard_footer_keeps_its_keyboard_entry_after_pointer_hover()
        {
            RadialMenu menu = Open(RadialMenuInteractionMode.Immediate);
            var input = new InputManager();

            Send(menu, input, Key.Right);
            SendAt(menu, input, Key.Down, EntryPoint(3));
            SendAt(menu, input, Key.Right, EntryPoint(3));
            SendAt(menu, input, Key.Enter, EntryPoint(3));

            Assert.True(menu.IsOpen);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(102, 10), menu.ChoiceChange);
        }

        [Fact]
        public void Keyboard_cannot_enter_footer_before_locking_an_entry()
        {
            RadialMenu menu = Open();
            var input = new InputManager();

            Send(menu, input, Key.Down);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);

            Send(menu, input, Key.Down);
            Send(menu, input, Key.Enter);
            Assert.Equal(new RadialMenuSelection(102, 5), menu.Selection);
        }

        [Fact]
        public void Opening_gesture_release_does_not_lock_an_entry()
        {
            var pointer = new Pointer();
            pointer.Update(MouseFrame(EntryPoint(0), true));
            RadialMenu menu = Open();
            pointer.Update(MouseFrame(EntryPoint(0), false));

            menu.Update(pointer, 1f / 60f);
            Tap(menu, pointer, ChoicePoint(10));

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Pressing_a_wedge_and_releasing_over_footer_does_not_commit()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();
            Tap(menu, pointer, EntryPoint(0));

            Update(menu, pointer, EntryPoint(1), true);
            Assert.False(Update(menu, pointer, ChoicePoint(10), false));

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);

            Tap(menu, pointer, ChoicePoint(10));
            Assert.Equal(new RadialMenuSelection(101, 10), menu.Selection);
        }

        [Fact]
        public void Warm_entry_then_choice_update_allocates_nothing()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();
            Tap(menu, pointer, EntryPoint(0));
            Update(menu, pointer, EntryPoint(1), false);
            for (int i = 0; i < 20; i++) menu.Update(pointer, 1f / 60f);

            AllocAssert.NoPerCallAllocation(
                "RadialMenu.Update(Pointer) with a locked entry",
                () => menu.Update(pointer, 1f / 60f));
        }
    }
}
