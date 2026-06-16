using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class PannableCanvasTests
    {
        // A 400x300 viewport offset to (200,150) in screen space.
        static readonly Rect Vp = new(200, 150, 400, 300);

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static PannableCanvas Canvas() => new()
        {
            Viewport = Vp,
            ContentBounds = new Rect(0, 0, 2000, 2000),
        };

        [Fact]
        public void WorldToScreen_then_ScreenToWorld_round_trips_with_viewport_offset()
        {
            var c = Canvas();
            c.Camera.Position = new Vector2(500, 500);
            c.Camera.Zoom = 1.5f;

            var world = new Vector2(617, 423);
            var back = c.ScreenToWorld(c.WorldToScreen(world));
            Assert.True(Vector2.Distance(world, back) < 1e-2f, back.ToString());
        }

        [Fact]
        public void World_center_maps_to_the_viewport_center_in_screen_space()
        {
            var c = Canvas();
            c.Camera.Position = new Vector2(900, 700);
            var s = c.WorldToScreen(c.Camera.Position);
            // viewport centre = offset + (W/2,H/2) = (200,150)+(200,150) = (400,300)
            Assert.True(Vector2.Distance(s, new Vector2(400, 300)) < 1e-3f, s.ToString());
        }

        [Fact]
        public void Clamp_keeps_position_inside_padded_bounds()
        {
            var c = Canvas();
            c.Padding = 50f;
            c.Camera.Position = new Vector2(-9999, -9999);
            // CenterOn applies a clamp; with zoom 1 halfW=200 halfH=150, padded bounds (-50,-50,2100,2100)
            c.CenterOn(new Vector2(-9999, -9999));
            Assert.Equal(new Vector2(-50 + 200, -50 + 150), c.Camera.Position); // (150,100)
        }

        [Fact]
        public void TryGetTap_true_when_press_and_release_both_inside_viewport()
        {
            var c = Canvas();
            var p = new Pointer();
            var at = new Vector2(400, 300); // inside Vp

            p.Update(Frame(at, false)); // idle
            p.Update(Frame(at, true));  // press inside
            p.Update(Frame(at, false)); // release inside

            Assert.True(c.TryGetTap(p, out var pressWorld, out var releaseWorld));
            Assert.True(Vector2.Distance(pressWorld, releaseWorld) < 1e-3f);
        }

        [Fact]
        public void TryGetTap_false_when_press_origin_outside_viewport()
        {
            var c = Canvas();
            var p = new Pointer();

            p.Update(Frame(new Vector2(10, 10), false)); // idle outside
            p.Update(Frame(new Vector2(10, 10), true));  // press OUTSIDE the viewport
            p.Update(Frame(new Vector2(400, 300), false)); // release inside

            Assert.False(c.TryGetTap(p, out var pw, out var rw));
            Assert.Equal(default, pw);
            Assert.Equal(default, rw);
        }

        [Fact]
        public void TryGetTap_drag_inside_returns_true_with_different_world_points()
        {
            var c = Canvas();
            var p = new Pointer();

            p.Update(Frame(new Vector2(300, 250), false));
            p.Update(Frame(new Vector2(300, 250), true));  // press inside
            p.Update(Frame(new Vector2(450, 350), false)); // release elsewhere inside

            Assert.True(c.TryGetTap(p, out var pressWorld, out var releaseWorld));
            Assert.True(Vector2.Distance(pressWorld, releaseWorld) > 1f); // caller can reject the drag
        }

        [Fact]
        public void Update_pans_on_a_simulated_drag()
        {
            var c = Canvas();
            c.Camera.Position = new Vector2(1000, 1000);
            c.Camera.Zoom = 1f;
            var p = new Pointer();

            // press inside, then move right+down 30,20 in one frame (drag origin stays inside)
            p.Update(Frame(new Vector2(400, 300), false));
            p.Update(Frame(new Vector2(400, 300), true));
            p.Update(Frame(new Vector2(430, 320), true)); // Delta = (30,20)

            c.Update(p, 0f);
            // grab-and-drag: Position -= delta/zoom = (1000,1000) - (30,20) = (970,980)
            Assert.Equal(new Vector2(970, 980), c.Camera.Position);
        }

        [Fact]
        public void Update_no_pan_when_disabled()
        {
            var c = Canvas();
            c.EnablePan = false;
            c.Camera.Position = new Vector2(1000, 1000);
            var p = new Pointer();

            p.Update(Frame(new Vector2(400, 300), false));
            p.Update(Frame(new Vector2(400, 300), true));
            p.Update(Frame(new Vector2(430, 320), true));

            c.Update(p, 0f);
            Assert.Equal(new Vector2(1000, 1000), c.Camera.Position);
        }

        [Fact]
        public void Update_wheel_pans_vertically_only_when_pointer_inside_viewport()
        {
            var c = Canvas();
            c.Camera.Position = new Vector2(1000, 1000);
            c.Camera.Zoom = 1f;
            c.ScrollPanSpeed = 0.5f;
            var p = new Pointer();

            // pointer outside -> no wheel pan
            p.Update(Frame(new Vector2(10, 10), false));
            c.Update(p, 10f);
            Assert.Equal(new Vector2(1000, 1000), c.Camera.Position);

            // pointer inside -> wheel pans: Position.Y += -wheel * speed / zoom = -10*0.5 = -5
            p.Update(Frame(new Vector2(400, 300), false));
            c.Update(p, 10f);
            Assert.Equal(new Vector2(1000, 995), c.Camera.Position);
        }

        [Fact]
        public void Update_blocks_the_viewport_region_for_lower_screens()
        {
            var c = Canvas();
            var p = new Pointer();
            p.Update(Frame(new Vector2(400, 300), false));

            c.Update(p, 0f);
            Assert.True(p.IsBlocked(new Vector2(400, 300)));  // inside the reserved viewport
            Assert.False(p.IsBlocked(new Vector2(10, 10)));   // outside
        }
    }
}
