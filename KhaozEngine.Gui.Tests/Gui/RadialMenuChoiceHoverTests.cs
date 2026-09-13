using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public sealed class RadialMenuChoiceHoverTests
    {
        const int Width = 960;
        const int Height = 540;
        static readonly Vector2 Center = new(480f, 270f);
        static readonly RadialMenuMetrics Metrics = RadialMenuMetrics.Default;

        readonly MouseFrames _mouse = new();

        [Fact]
        public void Choice_hover_requires_a_locked_entry_and_does_not_change_its_remembered_amount()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Update(menu, pointer, ChoicePoint(2), false);
            Assert.Equal(-1, menu.HoveredChoiceIndex);

            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics));
            Update(menu, pointer, ChoicePoint(2), false);

            Assert.Equal(1, menu.HoveredChoiceIndex);
            Assert.True(menu.IsOpen);
            Assert.False(menu.WasSelected);
            Assert.False(menu.WasChoiceChanged);

            Tap(menu, pointer, ChoicePoint(1));
            Assert.Equal(new RadialMenuSelection(101, 1), menu.Selection);
            Assert.False(menu.WasChoiceChanged);
        }

        [Fact]
        public void Disabled_choice_never_becomes_hovered()
        {
            RadialMenu menu = Open();
            var pointer = new Pointer();

            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 1, Metrics));
            Update(menu, pointer, ChoicePoint(3), false);

            Assert.Equal(-1, menu.HoveredChoiceIndex);
        }

        static RadialMenu Open()
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
            };
            menu.Open(
                LocalizedText.Raw("Choose recipe"),
                [new RadialMenuEntry(LocalizedText.Raw("Arrow shafts"), 101, InitialChoiceTag: 1)],
                Center,
                [
                    new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                    new RadialMenuChoice(LocalizedText.Raw("5"), 2),
                    new RadialMenuChoice(LocalizedText.Raw("10"), 3, Enabled: false),
                ],
                LocalizedText.Raw("Choose quantity"));
            return menu;
        }

        static Vector2 ChoicePoint(int tag)
        {
            int index = tag - 1;
            Rect bounds = RadialMenu.ChoiceBounds(Center, 3, index, Metrics);
            return new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
        }

        void Tap(RadialMenu menu, Pointer pointer, Vector2 position)
        {
            Update(menu, pointer, position, false);
            Update(menu, pointer, position, true);
            Update(menu, pointer, position, false);
        }

        void Update(RadialMenu menu, Pointer pointer, Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = _mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, Width, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }
    }
}
