using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class RadialMenuShortcutTests
    {
        const int Width = 960;
        const int Height = 540;
        static readonly Vector2 Center = new(480f, 270f);
        static readonly RadialMenuMetrics Metrics = RadialMenuMetrics.Default;

        readonly MouseFrames _mouse = new();

        [Fact]
        public void Quick_select_commits_the_pressed_entry_with_its_remembered_choice()
        {
            (RadialMenu menu, _) = Open();
            var pointer = new Pointer();

            Tap(menu, pointer, EntryPoint(1), quickSelect: true);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(102, 5), menu.Selection);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Quick_select_does_not_execute_a_disabled_entry()
        {
            (RadialMenu menu, _) = Open();
            var pointer = new Pointer();

            Tap(menu, pointer, EntryPoint(2), quickSelect: true);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
        }

        [Fact]
        public void Quick_select_hold_previews_hovered_entry_and_remembered_choice_without_mutating_state()
        {
            (RadialMenu menu, _) = Open();
            var pointer = new Pointer();
            Tap(menu, pointer, EntryPoint(0), quickSelect: false);

            Update(menu, pointer, EntryPoint(1), left: false, right: false, quickSelect: true);

            Assert.Equal(1, menu.QuickSelectPreviewIndex);
            Assert.Equal(1, menu.QuickSelectPreviewChoiceIndex);
            Assert.Equal(0, menu.LockedEntryIndex);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Quick_select_preview_follows_hover_and_clears_on_release_leave_close_and_open()
        {
            (RadialMenu menu, _) = Open();
            var pointer = new Pointer();

            Update(menu, pointer, EntryPoint(0), left: false, right: false, quickSelect: true);
            Assert.Equal(0, menu.QuickSelectPreviewIndex);

            Update(menu, pointer, EntryPoint(1), left: false, right: false, quickSelect: true);
            Assert.Equal(1, menu.QuickSelectPreviewIndex);

            Update(menu, pointer, EntryPoint(1), left: false, right: false, quickSelect: false);
            Assert.Equal(-1, menu.QuickSelectPreviewIndex);

            Update(menu, pointer, Center, left: false, right: false, quickSelect: true);
            Assert.Equal(-1, menu.QuickSelectPreviewIndex);

            Update(menu, pointer, EntryPoint(0), left: false, right: false, quickSelect: true);
            menu.Close();
            Assert.Equal(-1, menu.QuickSelectPreviewIndex);

            menu.Open(
                LocalizedText.Raw("Choose recipe"),
                [new RadialMenuEntry(LocalizedText.Raw("New recipe"), 501, InitialChoiceTag: 1)],
                Center,
                [new RadialMenuChoice(LocalizedText.Raw("1"), 1)],
                LocalizedText.Raw("Choose quantity"));
            Assert.Equal(-1, menu.QuickSelectPreviewIndex);
        }

        [Fact]
        public void Disabled_hover_has_no_actionable_quick_select_choice()
        {
            (RadialMenu menu, _) = Open();
            var pointer = new Pointer();

            Update(menu, pointer, EntryPoint(2), left: false, right: false, quickSelect: true);

            Assert.Equal(2, menu.QuickSelectPreviewIndex);
            Assert.Equal(-1, menu.QuickSelectPreviewChoiceIndex);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Context_choice_uses_the_entry_pinned_by_the_opening_right_tap()
        {
            (RadialMenu menu, ContextMenu context) = Open();
            var pointer = new Pointer();

            RightTap(menu, pointer, EntryPoint(0));
            Assert.True(context.IsOpen);

            Update(menu, pointer, EntryPoint(1), left: false, right: false);
            Tap(menu, pointer, CenterOf(context.EntryBounds(2)), quickSelect: false);

            Assert.Equal(new RadialMenuChoiceChange(101, 10), menu.ChoiceChange);
            Assert.Equal(new RadialMenuSelection(101, 10), menu.Selection);
            Assert.False(menu.IsOpen);
        }

        [Fact]
        public void Fresh_right_tap_on_an_exposed_enabled_wedge_retargets_the_open_context_menu()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));

            RightTap(menu, pointer, EntryPoint(3));

            Assert.True(context.IsOpen);
            Assert.Equal(3, menu.LockedEntryIndex);
            Tap(menu, pointer, CenterOf(context.EntryBounds(4)), quickSelect: false);
            Assert.Equal(new RadialMenuSelection(104, 1), menu.Selection);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Right_tap_inside_popup_keeps_popup_priority_over_an_underlying_wedge()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));
            Vector2 overlap = FindContextOverlap(context.Bounds, underlyingEntry: 1);

            RightTap(menu, pointer, overlap);

            Assert.True(context.IsOpen);
            Assert.Equal(0, menu.LockedEntryIndex);
        }

        [Fact]
        public void Right_gesture_from_popup_to_exposed_part_of_same_wedge_does_not_retarget()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));
            Vector2 covered = FindEntryPoint(context.Bounds, underlyingEntry: 1, insideContext: true);
            Vector2 exposed = FindEntryPoint(context.Bounds, underlyingEntry: 1, insideContext: false);

            RightGesture(menu, pointer, covered, exposed);

            Assert.True(context.IsOpen);
            Assert.Equal(0, menu.LockedEntryIndex);
        }

        [Fact]
        public void Right_gesture_from_exposed_wedge_into_popup_does_not_retarget()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));
            Vector2 covered = FindEntryPoint(context.Bounds, underlyingEntry: 1, insideContext: true);
            Vector2 exposed = FindEntryPoint(context.Bounds, underlyingEntry: 1, insideContext: false);

            RightGesture(menu, pointer, exposed, covered);

            Assert.True(context.IsOpen);
            Assert.Equal(0, menu.LockedEntryIndex);
        }

        [Fact]
        public void Right_tap_on_unavailable_wedge_keeps_existing_context_target()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));

            RightTap(menu, pointer, EntryPoint(2));

            Assert.True(context.IsOpen);
            Assert.Equal(0, menu.LockedEntryIndex);
            Tap(menu, pointer, CenterOf(context.EntryBounds(4)), quickSelect: false);
            Assert.Equal(new RadialMenuSelection(101, 1), menu.Selection);
        }

        [Fact]
        public void Context_quick_row_shows_and_commits_the_remembered_choice_without_changing_it()
        {
            (RadialMenu menu, ContextMenu context) = Open(shortLabels: true);
            var pointer = new Pointer();

            RightTap(menu, pointer, EntryPoint(1));

            Assert.Equal(56f, context.Bounds.Width);
            Tap(menu, pointer, CenterOf(context.EntryBounds(4)), quickSelect: false);

            Assert.Equal(new RadialMenuSelection(102, 5), menu.Selection);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Disabled_context_choice_does_not_execute_or_change_the_pinned_entry()
        {
            (RadialMenu menu, ContextMenu context) = Open();
            var pointer = new Pointer();

            RightTap(menu, pointer, EntryPoint(0));
            Tap(menu, pointer, CenterOf(context.EntryBounds(3)), quickSelect: false);

            Assert.True(menu.IsOpen);
            Assert.True(context.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Menu_without_choices_never_opens_shortcut_context_and_keeps_ordinary_selection()
        {
            var font = new FixedFont();
            var context = new ContextMenu(font, font) { Viewport = new Vector2(Width, Height) };
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                EntryContextMenu = context,
                QuickSelectLabel = LocalizedText.Raw("Quick craft last"),
            };
            menu.Open(
                LocalizedText.Raw("Choose source"),
                [new RadialMenuEntry(LocalizedText.Raw("Workbench"), 201)],
                Center);
            var pointer = new Pointer();

            RightTap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics));
            Assert.False(context.IsOpen);

            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics), quickSelect: false);
            Assert.Equal(new RadialMenuSelection(201, 0), menu.Selection);
        }

        [Fact]
        public void Close_and_reopen_clear_the_pinned_context_menu()
        {
            (RadialMenu menu, ContextMenu context) = Open();
            var pointer = new Pointer();

            RightTap(menu, pointer, EntryPoint(0));
            Assert.True(context.IsOpen);

            menu.Close();
            Assert.False(context.IsOpen);

            menu.Open(
                LocalizedText.Raw("Choose recipe"),
                [new RadialMenuEntry(LocalizedText.Raw("New recipe"), 501, InitialChoiceTag: 1)],
                Center,
                [new RadialMenuChoice(LocalizedText.Raw("1"), 1)],
                LocalizedText.Raw("Choose quantity"));
            Assert.False(context.IsOpen);

            RightTap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics));
            Tap(menu, pointer, CenterOf(context.EntryBounds(1)), quickSelect: false);
            Assert.Equal(new RadialMenuSelection(501, 1), menu.Selection);
        }

        [Fact]
        public void Context_menu_suppresses_radial_navigation_and_escape_closes_only_the_popup()
        {
            (RadialMenu menu, ContextMenu context) = Open();
            var pointer = new Pointer();
            RightTap(menu, pointer, EntryPoint(0));
            var input = new InputManager();

            Send(menu, input, Key.Down);
            Send(menu, input, Key.Right);
            Send(menu, input, Key.Enter);

            Assert.True(menu.IsOpen);
            Assert.True(context.IsOpen);
            Assert.False(menu.WasSelected);

            Send(menu, input, Key.Escape);

            Assert.True(menu.IsOpen);
            Assert.False(context.IsOpen);
            Assert.False(menu.WasDismissed);
        }

        [Theory]
        [InlineData(RadialMenuInteractionMode.Immediate)]
        [InlineData(RadialMenuInteractionMode.EntryThenChoice)]
        public void Quick_select_refuses_an_amount_menu_with_no_enabled_choice(
            RadialMenuInteractionMode interactionMode)
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = interactionMode,
            };
            menu.Open(
                LocalizedText.Raw("Choose recipe"),
                [new RadialMenuEntry(LocalizedText.Raw("Unavailable recipe"), 601)],
                Center,
                [
                    new RadialMenuChoice(LocalizedText.Raw("1"), 1, Enabled: false),
                    new RadialMenuChoice(LocalizedText.Raw("All"), -1, Enabled: false),
                ],
                LocalizedText.Raw("Choose quantity"));
            var pointer = new Pointer();

            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics), quickSelect: true);

            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
        }

        [Fact]
        public void Quick_select_allows_entry_then_choice_menu_without_a_footer()
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
            };
            menu.Open(
                LocalizedText.Raw("Choose action"),
                [new RadialMenuEntry(LocalizedText.Raw("Inspect"), 701)],
                Center);
            var pointer = new Pointer();

            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics), quickSelect: true);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(701, 0), menu.Selection);
        }

        [Fact]
        public void Footerless_quick_select_keeps_ordinary_center_state_until_the_tap()
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = RadialMenuInteractionMode.Immediate,
                QuickSelectLabel = LocalizedText.Raw("Quick craft last"),
            };
            menu.Open(
                LocalizedText.Raw("Choose material"),
                [new RadialMenuEntry(LocalizedText.Raw("Logs"), 801)],
                Center);
            var pointer = new Pointer();

            Update(
                menu,
                pointer,
                RadialMenu.LabelPoint(Center, 0, 1, Metrics),
                left: false,
                right: false,
                quickSelect: true);

            Assert.Equal(-1, menu.QuickSelectPreviewIndex);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
        }

        static (RadialMenu Menu, ContextMenu Context) Open(bool shortLabels = false)
        {
            var font = new FixedFont();
            var context = new ContextMenu(font, font) { Viewport = new Vector2(Width, Height) };
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
                EntryContextMenu = context,
                QuickSelectLabel = LocalizedText.Raw(shortLabels ? "Q" : "Quick craft last"),
            };
            menu.Open(
                LocalizedText.Raw("Choose recipe"),
                [
                    new RadialMenuEntry(LocalizedText.Raw(shortLabels ? "A" : "Arrow shafts"), 101, InitialChoiceTag: 1),
                    new RadialMenuEntry(LocalizedText.Raw(shortLabels ? "B" : "Joinery pegs"), 102, InitialChoiceTag: 5),
                    new RadialMenuEntry(LocalizedText.Raw("Wooden handle"), 103, Enabled: false, InitialChoiceTag: 1),
                    new RadialMenuEntry(LocalizedText.Raw("Wooden light bow stave"), 104, InitialChoiceTag: 1),
                ],
                Center,
                [
                    new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                    new RadialMenuChoice(LocalizedText.Raw("5"), 5),
                    new RadialMenuChoice(LocalizedText.Raw("10"), 10),
                    new RadialMenuChoice(LocalizedText.Raw("All"), -1, Enabled: false),
                ],
                LocalizedText.Raw("Choose quantity"));
            return (menu, context);
        }

        static Vector2 EntryPoint(int index) => RadialMenu.LabelPoint(Center, index, 4, Metrics);

        static Vector2 CenterOf(Rect bounds) =>
            new(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);

        static Vector2 FindContextOverlap(Rect contextBounds, int underlyingEntry) =>
            FindEntryPoint(contextBounds, underlyingEntry, insideContext: true);

        static Vector2 FindEntryPoint(Rect contextBounds, int underlyingEntry, bool insideContext)
        {
            for (float y = 0f; y < Height; y += 2f)
            {
                for (float x = 0f; x < Width; x += 2f)
                {
                    var point = new Vector2(x, y);
                    if (contextBounds.Contains(point) == insideContext &&
                        RadialMenu.EntryAt(point, Center, 4, Metrics) == underlyingEntry)
                        return point;
                }
            }
            throw new Xunit.Sdk.XunitException("expected the requested popup and radial wedge overlap shape");
        }

        void Tap(RadialMenu menu, Pointer pointer, Vector2 position, bool quickSelect)
        {
            Update(menu, pointer, position, left: false, right: false, quickSelect: quickSelect);
            Update(menu, pointer, position, left: true, right: false, quickSelect: quickSelect);
            Update(menu, pointer, position, left: false, right: false, quickSelect: quickSelect);
        }

        void RightTap(RadialMenu menu, Pointer pointer, Vector2 position)
        {
            Update(menu, pointer, position, left: false, right: false);
            Update(menu, pointer, position, left: false, right: true);
            Update(menu, pointer, position, left: false, right: false);
        }

        void RightGesture(RadialMenu menu, Pointer pointer, Vector2 press, Vector2 release)
        {
            Update(menu, pointer, press, left: false, right: false);
            Update(menu, pointer, press, left: false, right: true);
            Update(menu, pointer, release, left: false, right: false);
        }

        static void Send(RadialMenu menu, InputManager input, Key key)
        {
            input.Update(KeyFrame(key));
            menu.Update(input, 1f / 60f, focused: true);
            input.Update(KeyFrame(null));
            menu.Update(input, 1f / 60f, focused: true);
        }

        static InputState KeyFrame(Key? key)
        {
            var held = new HashSet<Key>();
            if (key is { } value) held.Add(value);
            return new InputState(
                held,
                key is null ? new HashSet<Key>() : new HashSet<Key> { key.Value },
                new HashSet<Key>(),
                new HashSet<MouseButton>(),
                new HashSet<MouseButton>(),
                Vector2.Zero,
                Vector2.Zero,
                0f,
                Width,
                Height);
        }

        void Update(
            RadialMenu menu,
            Pointer pointer,
            Vector2 position,
            bool left,
            bool right,
            bool quickSelect = false)
        {
            var held = new HashSet<MouseButton>();
            if (left) held.Add(MouseButton.Left);
            if (right) held.Add(MouseButton.Right);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = _mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, Width, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f, quickSelect);
        }

        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);
        }
    }
}
