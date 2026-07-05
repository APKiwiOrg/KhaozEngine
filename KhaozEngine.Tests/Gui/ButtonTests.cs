using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.App;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage of the retained <see cref="Button"/>: <see cref="Button.Update"/> reserves its rect on the
    /// pointer (the click-through gate), an <see cref="Button.Enabled"/>=false button never fires, and the
    /// press-origin invariant still holds. No texture/font drawing (Update only computes interaction).
    /// </summary>
    public class ButtonTests
    {
        static readonly Rect Btn = new(100, 100, 120, 40);
        static readonly Vector2 Center = new(160, 120);

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        [Fact]
        public void Update_reserves_bounds_on_the_pointer()
        {
            var p = new Pointer();
            p.Update(Frame(Center, false));
            var btn = new Button(Btn, LocalizedText.Raw("Go"), null!);

            btn.Update(p);

            Assert.True(p.IsBlocked(Center));               // rect reserved for click-through
            Assert.False(p.IsBlocked(new Vector2(10, 10))); // outside the rect: not blocked
        }

        [Fact]
        public void Enabled_button_fires_OnClick_on_a_valid_tap()
        {
            var p = new Pointer();
            int clicks = 0;
            var btn = new Button(Btn, LocalizedText.Raw("Go"), null!, () => clicks++);

            p.Update(Frame(Center, false));     // idle
            Assert.False(btn.Update(p));
            p.Update(Frame(Center, true));      // press inside
            Assert.False(btn.Update(p));
            p.Update(Frame(Center, false));     // release inside
            Assert.True(btn.Update(p));         // tap fires

            Assert.Equal(1, clicks);
        }

        [Fact]
        public void Disabled_button_never_fires_and_returns_false_even_on_a_valid_tap()
        {
            var p = new Pointer();
            int clicks = 0;
            var btn = new Button(Btn, LocalizedText.Raw("Go"), null!, () => clicks++) { Enabled = false };

            p.Update(Frame(Center, false));     // idle
            btn.Update(p);
            p.Update(Frame(Center, true));      // press inside
            btn.Update(p);
            p.Update(Frame(Center, false));     // release inside -> would tap if enabled

            Assert.False(btn.Update(p));        // disabled: no click
            Assert.Equal(0, clicks);
            Assert.True(p.IsBlocked(Center));   // still reserves its rect
        }

        [Fact]
        public void Tap_with_press_origin_outside_the_rect_does_not_fire()
        {
            var p = new Pointer();
            int clicks = 0;
            var btn = new Button(Btn, LocalizedText.Raw("Go"), null!, () => clicks++);

            p.Update(Frame(new Vector2(10, 10), false));   // idle outside
            btn.Update(p);
            p.Update(Frame(new Vector2(10, 10), true));    // press OUTSIDE
            btn.Update(p);
            p.Update(Frame(Center, false));                // release inside

            Assert.False(btn.Update(p));                   // press-origin invariant: no click
            Assert.Equal(0, clicks);
        }
    }
}
