using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Keyboard/gamepad focus navigation across a list of N widgets (<see cref="FocusNavigator"/>):
    /// wrap/clamp index math and driving focus from an <see cref="InputManager"/>'s vertical menu nav.
    /// </summary>
    public class FocusNavigatorTests
    {
        static InputState KeyFrame(params Key[] pressed)
        {
            var p = new HashSet<Key>(pressed);
            return new InputState(
                new HashSet<Key>(p), p, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 960, 540);
        }

        [Fact]
        public void Empty_list_has_no_focus_and_moves_are_noops()
        {
            var nav = new FocusNavigator(0);
            Assert.Equal(-1, nav.Focused);
            nav.MoveNext();
            Assert.Equal(-1, nav.Focused);
        }

        [Fact]
        public void Constructor_clamps_focus_into_range()
        {
            var nav = new FocusNavigator(3, focused: 9);
            Assert.Equal(2, nav.Focused);
        }

        [Fact]
        public void MoveNext_wraps_from_last_to_first()
        {
            var nav = new FocusNavigator(3, focused: 2);
            nav.MoveNext();
            Assert.Equal(0, nav.Focused);
        }

        [Fact]
        public void MovePrevious_wraps_from_first_to_last()
        {
            var nav = new FocusNavigator(3, focused: 0);
            nav.MovePrevious();
            Assert.Equal(2, nav.Focused);
        }

        [Fact]
        public void No_wrap_clamps_at_the_ends()
        {
            var nav = new FocusNavigator(3, focused: 2) { Wrap = false };
            nav.MoveNext();
            Assert.Equal(2, nav.Focused);
            nav.Focus(0);
            nav.MovePrevious();
            Assert.Equal(0, nav.Focused);
        }

        [Fact]
        public void SetCount_reclamps_focus()
        {
            var nav = new FocusNavigator(5, focused: 4);
            nav.SetCount(2);
            Assert.Equal(1, nav.Focused);
            nav.SetCount(0);
            Assert.Equal(-1, nav.Focused);
        }

        [Fact]
        public void Update_moves_focus_down_on_menu_down_and_reports_the_change()
        {
            var nav = new FocusNavigator(3, focused: 0);
            var im = new InputManager();
            im.Update(KeyFrame(Key.Down));
            Assert.True(nav.Update(im));
            Assert.Equal(1, nav.Focused);
        }

        [Fact]
        public void Update_moves_focus_up_on_menu_up()
        {
            var nav = new FocusNavigator(3, focused: 1);
            var im = new InputManager();
            im.Update(KeyFrame(Key.Up));
            Assert.True(nav.Update(im));
            Assert.Equal(0, nav.Focused);
        }

        [Fact]
        public void Update_returns_false_when_nothing_is_pressed()
        {
            var nav = new FocusNavigator(3, focused: 1);
            var im = new InputManager();
            im.Update(KeyFrame());
            Assert.False(nav.Update(im));
            Assert.Equal(1, nav.Focused);
        }
    }
}
