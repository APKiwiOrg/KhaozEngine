using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class GuiSurfaceTests
    {
        static readonly Rect Btn = new(100, 100, 120, 40);

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Surface needs no texture for headless interaction; null white is never drawn (batch is null).
        static GuiSurface Surface() => new(null!, null);

        [Fact]
        public void Tap_inside_a_button_returns_true_only_on_release_and_false_while_held()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(150, 120);

            p.Update(Frame(at, false));               // idle
            ui.Begin(null!, p);
            Assert.False(ui.Button(null!, Btn, "Go"));

            p.Update(Frame(at, true));                // press inside
            ui.Begin(null!, p);
            Assert.False(ui.Button(null!, Btn, "Go")); // held -> no click yet

            p.Update(Frame(at, false));               // release inside
            ui.Begin(null!, p);
            Assert.True(ui.Button(null!, Btn, "Go"));  // click fires once on release
        }

        [Fact]
        public void Tap_with_press_origin_outside_the_rect_returns_false()
        {
            var ui = Surface();
            var p = new Pointer();

            p.Update(Frame(new Vector2(10, 10), false));   // idle outside
            p.Update(Frame(new Vector2(10, 10), true));    // press OUTSIDE the button
            p.Update(Frame(new Vector2(150, 120), false)); // release inside the button

            ui.Begin(null!, p);
            Assert.False(ui.Button(null!, Btn, "Go"));     // press-origin invariant: no click
        }

        [Fact]
        public void Disabled_button_never_returns_true_but_still_captures_the_pointer()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(150, 120);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));                      // press inside
            p.Update(Frame(at, false));                     // release inside

            ui.Begin(null!, p);
            bool clicked = ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: false);
            Assert.False(clicked);                          // disabled never clicks
            Assert.True(ui.PointerCaptured);                // but still reserves its rect
        }

        [Fact]
        public void PointerCaptured_is_true_when_press_origin_is_inside_a_drawn_panel()
        {
            var ui = Surface();
            var p = new Pointer();
            var panel = new Rect(0, 0, 300, 200);
            var at = new Vector2(50, 50);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));                      // press-origin inside the panel

            ui.Begin(null!, p);
            ui.Panel(panel, GuiStyle.Default.Fill);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void PointerCaptured_is_false_when_press_origin_is_outside_everything()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(500, 400);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));                      // press-origin far from any widget

            ui.Begin(null!, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.PointerCaptured);
        }

        [Fact]
        public void Begin_clears_the_blocked_set_each_frame()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(50, 50);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));                      // press-origin inside the panel

            ui.Begin(null!, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            Assert.True(ui.PointerCaptured);

            // New frame, draw nothing over the press-origin -> capture resets.
            ui.Begin(null!, p);
            Assert.False(ui.PointerCaptured);
        }

        [Fact]
        public void Swatch_reserves_capture_like_a_panel()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(20, 20);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));

            ui.Begin(null!, p);
            ui.Swatch(new Rect(0, 0, 40, 40), Vector4.One);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void Selected_or_hover_state_does_not_change_the_click_contract()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(150, 120);

            // Hover only (no press): no click regardless of selected flag.
            p.Update(Frame(at, false));
            ui.Begin(null!, p);
            Assert.False(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));

            // Full tap with selected: still clicks once on release.
            p.Update(Frame(at, true));
            ui.Begin(null!, p);
            Assert.False(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));

            p.Update(Frame(at, false));
            ui.Begin(null!, p);
            Assert.True(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));
        }
    }
}
