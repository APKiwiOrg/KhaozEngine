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

        static InputState Frame(Vector2 pos, bool down) => Frame(pos, down, true);

        static InputState Frame(Vector2 pos, bool down, bool focused)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540, windowFocused: focused);
        }

        // Surface needs no texture for headless interaction; null white is never drawn (batch is null).
        static GuiSurface Surface() => new(null!, null);

        [Fact]
        public void Unfocused_window_suppresses_button_hover_and_hover_enter()
        {
            var ui = Surface();
            var p = new Pointer();
            var inside = new Vector2(150, 120);

            // Cursor over the button, but the window is NOT focused: no hover, no hover-enter (kills hover SFX).
            p.Update(Frame(inside, false, focused: false));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.IsHovering);
            Assert.False(ui.HoverEntered);
            Assert.Null(ui.HoveredRect);

            // Focus returns: hover + hover-enter resume normally.
            p.Update(Frame(inside, false, focused: true));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.True(ui.IsHovering);
            Assert.True(ui.HoverEntered);
            Assert.Equal(Btn, ui.HoveredRect);
        }

        [Fact]
        public void Unfocused_window_suppresses_pointer_and_hover_capture()
        {
            var ui = Surface();
            var p = new Pointer();
            var panel = new Rect(0, 0, 300, 200);
            var at = new Vector2(50, 50);

            // Press-origin inside the panel, but the window is unfocused: neither capture gate fires.
            p.Update(Frame(at, false, focused: false));
            p.Update(Frame(at, true, focused: false));
            ui.Begin(null, p);
            ui.Panel(panel, GuiStyle.Default.Fill);
            Assert.False(ui.PointerCaptured);
            Assert.False(ui.HoverCaptured);

            // Focus returns with the press still held: capture engages.
            p.Update(Frame(at, true, focused: true));
            ui.Begin(null, p);
            ui.Panel(panel, GuiStyle.Default.Fill);
            Assert.True(ui.PointerCaptured);
            Assert.True(ui.HoverCaptured);
        }

        [Fact]
        public void Tap_inside_a_button_returns_true_only_on_release_and_false_while_held()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(150, 120);

            p.Update(Frame(at, false));               // idle
            ui.Begin(null, p);
            Assert.False(ui.Button(null!, Btn, "Go"));

            p.Update(Frame(at, true));                // press inside
            ui.Begin(null, p);
            Assert.False(ui.Button(null!, Btn, "Go")); // held -> no click yet

            p.Update(Frame(at, false));               // release inside
            ui.Begin(null, p);
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

            ui.Begin(null, p);
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

            ui.Begin(null, p);
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

            ui.Begin(null, p);
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

            ui.Begin(null, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.PointerCaptured);
        }

        [Fact]
        public void PointerCaptured_is_false_for_a_never_pressed_pointer_even_over_a_widget_at_the_origin()
        {
            var ui = Surface();
            var p = new Pointer();   // never pressed: PressOrigin defaults to (0,0)

            ui.Begin(null, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            Assert.False(ui.PointerCaptured);   // no active press -> no false-positive capture at (0,0)
        }

        [Fact]
        public void HoverCaptured_is_true_over_a_panel_with_no_press()
        {
            var ui = Surface();
            var p = new Pointer();   // never pressed

            p.Update(Frame(new Vector2(50, 50), false));    // current position inside the panel, no button down
            ui.Begin(null, p);
            ui.Panel(new Rect(0, 0, 300, 200), GuiStyle.Default.Fill);
            Assert.True(ui.HoverCaptured);                   // hover gate fires with no press...
            Assert.False(ui.PointerCaptured);               // ...while the press gate stays closed
        }

        [Fact]
        public void HoverCaptured_is_false_when_position_outside_everything()
        {
            var ui = Surface();
            var p = new Pointer();

            p.Update(Frame(new Vector2(500, 400), false));  // current position far from any widget
            ui.Begin(null, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.HoverCaptured);
        }

        [Fact]
        public void HoverCaptured_tracks_current_position_not_press_origin()
        {
            var ui = Surface();
            var p = new Pointer();
            var panel = new Rect(0, 0, 300, 200);

            p.Update(Frame(new Vector2(50, 50), false));
            p.Update(Frame(new Vector2(50, 50), true));     // press-origin inside the panel
            p.Update(Frame(new Vector2(500, 400), true));   // drag the live position off the panel

            ui.Begin(null, p);
            ui.Panel(panel, GuiStyle.Default.Fill);
            Assert.True(ui.PointerCaptured);                // press-origin still inside -> captured
            Assert.False(ui.HoverCaptured);                // but the cursor is no longer over UI
        }

        [Fact]
        public void Begin_clears_the_blocked_set_each_frame()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(50, 50);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));                      // press-origin inside the panel

            ui.Begin(null, p);
            ui.Panel(new Rect(0, 0, 100, 100), GuiStyle.Default.Fill);
            Assert.True(ui.PointerCaptured);

            // New frame, draw nothing over the press-origin -> capture resets.
            ui.Begin(null, p);
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

            ui.Begin(null, p);
            ui.Swatch(new Rect(0, 0, 40, 40), Vector4.One);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void HoverEntered_fires_once_when_the_pointer_moves_onto_a_button()
        {
            var ui = Surface();
            var p = new Pointer();
            var outside = new Vector2(10, 10);
            var inside = new Vector2(150, 120);

            // Outside: not hovering, no enter.
            p.Update(Frame(outside, false));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.IsHovering);
            Assert.False(ui.HoverEntered);

            // Move onto the button: hovering + hover-enter this frame.
            p.Update(Frame(inside, false));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.True(ui.IsHovering);
            Assert.True(ui.HoverEntered);
            Assert.Equal(Btn, ui.HoveredRect);

            // Stay on the button: still hovering, but no re-enter.
            p.Update(Frame(new Vector2(160, 130), false));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.True(ui.IsHovering);
            Assert.False(ui.HoverEntered);

            // Move off onto nothing: not hovering, and exit is NOT an enter.
            p.Update(Frame(outside, false));
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go");
            Assert.False(ui.IsHovering);
            Assert.False(ui.HoverEntered);
        }

        [Fact]
        public void HoverEntered_fires_when_sliding_from_one_button_straight_onto_another()
        {
            var a = new Rect(100, 100, 120, 40);
            var b = new Rect(100, 160, 120, 40);
            var ui = Surface();
            var p = new Pointer();

            void FrameAt(Vector2 at)
            {
                p.Update(Frame(at, false));
                ui.Begin(null, p);
                ui.Button(null!, a, "A");
                ui.Button(null!, b, "B");
            }

            FrameAt(new Vector2(150, 120));      // on A -> enter
            Assert.True(ui.HoverEntered);
            Assert.Equal(a, ui.HoveredRect);

            FrameAt(new Vector2(150, 180));      // straight onto B -> a fresh enter (different rect)
            Assert.True(ui.HoverEntered);
            Assert.Equal(b, ui.HoveredRect);
        }

        [Fact]
        public void A_disabled_button_does_not_register_hover()
        {
            var ui = Surface();
            var p = new Pointer();

            p.Update(Frame(new Vector2(150, 120), false)); // over the button
            ui.Begin(null, p);
            ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: false);
            Assert.False(ui.IsHovering);     // disabled => no hover affordance
            Assert.False(ui.HoverEntered);
        }

        [Fact]
        public void Selected_or_hover_state_does_not_change_the_click_contract()
        {
            var ui = Surface();
            var p = new Pointer();
            var at = new Vector2(150, 120);

            // Hover only (no press): no click regardless of selected flag.
            p.Update(Frame(at, false));
            ui.Begin(null, p);
            Assert.False(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));

            // Full tap with selected: still clicks once on release.
            p.Update(Frame(at, true));
            ui.Begin(null, p);
            Assert.False(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));

            p.Update(Frame(at, false));
            ui.Begin(null, p);
            Assert.True(ui.Button(null!, Btn, "Go", GuiStyle.Default, enabled: true, selected: true));
        }
    }
}
