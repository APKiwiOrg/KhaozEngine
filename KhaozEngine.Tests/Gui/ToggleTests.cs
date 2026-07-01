using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class ToggleTests
    {
        static readonly Rect Box = new(100, 100, 60, 30);
        static readonly Vector2 Inside = new(130, 115);
        static readonly Vector2 Outside = new(10, 10);

        static InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        [Fact]
        public void Tap_inside_flips_the_state_and_fires_OnChanged()
        {
            bool? notified = null;
            var toggle = new Toggle(Box, isOn: false, v => notified = v);
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));    // press inside
            Assert.False(toggle.Update(p));   // still down, no tap yet
            p.Update(Frame(Inside, false));   // release inside -> tap
            Assert.True(toggle.Update(p));
            Assert.True(toggle.IsOn);
            Assert.Equal(true, notified);
        }

        [Fact]
        public void Press_outside_release_inside_does_not_toggle()  // click-through invariant
        {
            var toggle = new Toggle(Box, isOn: false);
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(Frame(Outside, true));   // press began outside
            p.Update(Frame(Inside, false));   // release inside
            Assert.False(toggle.Update(p));
            Assert.False(toggle.IsOn);
        }

        [Fact]
        public void Second_tap_flips_back_off()
        {
            var toggle = new Toggle(Box, isOn: true);
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            p.Update(Frame(Inside, false));
            toggle.Update(p);
            Assert.False(toggle.IsOn);
        }

        [Fact]
        public void Disabled_toggle_ignores_taps()
        {
            var toggle = new Toggle(Box, isOn: false) { Enabled = false };
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            p.Update(Frame(Inside, false));
            Assert.False(toggle.Update(p));
            Assert.False(toggle.IsOn);
        }
    }
}
