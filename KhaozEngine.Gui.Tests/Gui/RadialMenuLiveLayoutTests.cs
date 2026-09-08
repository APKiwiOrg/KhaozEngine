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
    public sealed class RadialMenuLiveLayoutTests
    {
        static readonly Rect Safe = new(0f, 0f, 960f, 540f);
        static readonly Vector2 Center = new(480f, 270f);

        readonly MouseFrames _mouse = new();

        [Fact]
        public void Outer_radius_change_immediately_reflows_bounds_blocking_and_selection()
        {
            RadialMenu menu = OpenMenu(Safe, Center);
            RadialMenuMetrics resized = menu.Metrics with { OuterRadius = 200f };

            menu.Metrics = resized;

            Assert.Equal(new Rect(280f, 70f, 400f, 400f), menu.Bounds);
            Vector2 north = new(480f, 90f);
            var pointer = new Pointer();
            Hover(menu, pointer, north);
            Assert.Equal(0, menu.HoverIndex);
            Assert.True(pointer.IsBlocked(north));

            Tap(menu, pointer, north);
            Assert.True(menu.WasSelected);
            Assert.Equal(new RadialMenuSelection(101, 0), menu.Selection);
        }

        [Fact]
        public void Footer_size_change_immediately_reflows_bounds_blocking_and_choice_hits()
        {
            RadialMenu menu = OpenMenu(Safe, Center, Choices());
            RadialMenuMetrics resized = menu.Metrics with
            {
                FooterGap = 20f,
                FooterButtonSize = new Vector2(100f, 50f),
            };

            menu.Metrics = resized;

            Assert.Equal(new Rect(250f, 122f, 460f, 366f), menu.Bounds);
            Rect fourth = RadialMenu.ChoiceBounds(menu.Center, 4, 3, resized);
            Vector2 fourthCenter = new(fourth.X + fourth.Width / 2f, fourth.Y + fourth.Height / 2f);
            var pointer = new Pointer();
            Hover(menu, pointer, fourthCenter);
            Assert.True(pointer.IsBlocked(fourthCenter));

            Tap(menu, pointer, fourthCenter);
            Assert.True(menu.IsOpen);
            Assert.True(menu.WasChoiceChanged);
            Assert.Equal(new RadialMenuChoiceChange(101, 13), menu.ChoiceChange);
        }

        [Fact]
        public void SafeBounds_changes_reclamp_from_the_requested_anchor()
        {
            Vector2 requested = new(930f, 510f);
            RadialMenu menu = OpenMenu(Safe, requested, Choices());
            Rect smaller = new(0f, 0f, 640f, 480f);

            menu.SafeBounds = smaller;

            Vector2 smallerCenter = RadialMenu.ComputeCenter(requested, smaller, 4, 4, menu.Metrics);
            Assert.Equal(smallerCenter, menu.Center);
            Assert.Equal(RadialMenu.ComputeBounds(smallerCenter, 4, 4, menu.Metrics), menu.Bounds);

            Rect larger = new(0f, 0f, 1200f, 800f);
            menu.SafeBounds = larger;

            Assert.Equal(requested, menu.Center);
            Assert.Equal(RadialMenu.ComputeBounds(requested, 4, 4, menu.Metrics), menu.Bounds);
        }

        [Fact]
        public void SafeBounds_change_makes_outside_dismissal_use_the_current_composition()
        {
            Vector2 requested = new(800f, 270f);
            RadialMenu menu = OpenMenu(Safe, requested);
            Vector2 oldCenter = menu.Center;

            menu.SafeBounds = new Rect(0f, 0f, 400f, 540f);
            Assert.False(menu.Bounds.Contains(oldCenter));

            var pointer = new Pointer();
            Tap(menu, pointer, oldCenter);

            Assert.False(menu.IsOpen);
            Assert.True(menu.WasDismissed);
            Assert.True(pointer.IsConsumed);
        }

        [Fact]
        public void Live_Metrics_rejection_is_atomic_when_the_new_composition_would_not_fit()
        {
            RadialMenu menu = OpenMenu(Safe, Center, Choices());
            LayoutState before = Snapshot(menu);

            Assert.Throws<ArgumentOutOfRangeException>(() => menu.Metrics =
                menu.Metrics with { OuterRadius = 270f });

            Assert.Equal(before, Snapshot(menu));
        }

        [Fact]
        public void Live_SafeBounds_rejection_is_atomic_when_the_composition_would_not_fit()
        {
            RadialMenu menu = OpenMenu(Safe, Center, Choices());
            LayoutState before = Snapshot(menu);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                menu.SafeBounds = new Rect(0f, 0f, 300f, 340f));

            Assert.Equal(before, Snapshot(menu));
        }

        [Fact]
        public void Layout_change_after_Update_blocks_the_new_geometry_in_the_same_frame()
        {
            RadialMenu menu = OpenMenu(Safe, Center);
            var pointer = new Pointer();
            Hover(menu, pointer, Center);
            Vector2 addedArea = new(480f, 90f);
            Assert.False(pointer.IsBlocked(addedArea));

            menu.Metrics = menu.Metrics with { OuterRadius = 200f };

            Assert.True(menu.Bounds.Contains(addedArea));
            Assert.True(pointer.IsBlocked(addedArea));
        }

        static RadialMenu OpenMenu(Rect safe, Vector2 anchor, RadialMenuChoice[]? choices = null)
        {
            var menu = new RadialMenu { SafeBounds = safe };
            menu.Open(
                LocalizedText.Raw("Actions"),
                [
                    new RadialMenuEntry(LocalizedText.Raw("North"), 101),
                    new RadialMenuEntry(LocalizedText.Raw("East"), 102),
                    new RadialMenuEntry(LocalizedText.Raw("South"), 103),
                    new RadialMenuEntry(LocalizedText.Raw("West"), 104),
                ],
                anchor,
                choices);
            return menu;
        }

        static RadialMenuChoice[] Choices() =>
        [
            new(LocalizedText.Raw("One"), 10),
            new(LocalizedText.Raw("Two"), 11),
            new(LocalizedText.Raw("Three"), 12),
            new(LocalizedText.Raw("Four"), 13),
        ];

        void Hover(RadialMenu menu, Pointer pointer, Vector2 position) =>
            Update(menu, pointer, position, down: false);

        void Tap(RadialMenu menu, Pointer pointer, Vector2 position)
        {
            Update(menu, pointer, position, down: false);
            Update(menu, pointer, position, down: true);
            Update(menu, pointer, position, down: false);
        }

        void Update(RadialMenu menu, Pointer pointer, Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = _mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, 960, 540,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }

        static LayoutState Snapshot(RadialMenu menu) =>
            new(menu.Metrics, menu.SafeBounds, menu.Center, menu.Bounds);

        readonly record struct LayoutState(
            RadialMenuMetrics Metrics,
            Rect SafeBounds,
            Vector2 Center,
            Rect Bounds);
    }
}
