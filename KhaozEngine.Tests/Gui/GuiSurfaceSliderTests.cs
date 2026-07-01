using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class GuiSurfaceSliderTests
    {
        // A wide, thin track so the handle half-width is small relative to the span.
        static readonly Rect Track = new(100, 200, 200, 24);

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static GuiSurface Surface() => new(null!, null);

        // Press-and-hold at a position inside the track, returning the value the slider reports that frame.
        static float DragTo(GuiSurface ui, Pointer p, float x, float y)
        {
            p.Update(Frame(new Vector2(x, y), true));
            ui.Begin(null, p);
            return ui.Slider(Track, 0f);
        }

        [Theory]
        [InlineData(0.25)]
        [InlineData(0.50)]
        [InlineData(0.75)]
        public void Pressing_in_maps_pointer_x_to_value_within_handle_tolerance(double frac)
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;

            p.Update(Frame(new Vector2(Track.X, midY), false));   // idle inside, no press
            float x = Track.X + (float)frac * Track.Width;
            float v = DragTo(ui, p, x, midY);

            Assert.Equal((float)frac, v, 0.08f);   // within handle half-width tolerance
        }

        [Fact]
        public void Ends_clamp_to_exactly_zero_and_one()
        {
            float midY = Track.Y + Track.Height * 0.5f;

            // Press inside near the left edge, then drag PAST the left end while held -> clamps to 0.
            var ui = Surface();
            var p = new Pointer();
            p.Update(Frame(new Vector2(Track.X + 5f, midY), false));
            p.Update(Frame(new Vector2(Track.X + 5f, midY), true));      // press-origin inside
            p.Update(Frame(new Vector2(Track.X - 50f, midY), true));     // drag off the left end
            ui.Begin(null, p);
            Assert.Equal(0f, ui.Slider(Track, 0.5f));

            // Press inside near the right edge, then drag PAST the right end while held -> clamps to 1.
            var ui2 = Surface();
            var p2 = new Pointer();
            p2.Update(Frame(new Vector2(Track.Right - 5f, midY), false));
            p2.Update(Frame(new Vector2(Track.Right - 5f, midY), true)); // press-origin inside
            p2.Update(Frame(new Vector2(Track.Right + 50f, midY), true));// drag off the right end
            ui2.Begin(null, p2);
            Assert.Equal(1f, ui2.Slider(Track, 0.5f));
        }

        [Fact]
        public void A_press_that_began_outside_the_track_does_not_move_the_value()
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;

            p.Update(Frame(new Vector2(10, 10), false));            // idle far outside
            p.Update(Frame(new Vector2(10, 10), true));             // press-origin OUTSIDE the track
            p.Update(Frame(new Vector2(Track.X + Track.Width * 0.5f, midY), true)); // drag onto the track, still held

            ui.Begin(null, p);
            float v = ui.Slider(Track, 0.33f);
            Assert.Equal(0.33f, v);   // press-origin invariant: value unchanged
        }

        [Fact]
        public void Disabled_returns_the_input_value_unchanged_and_does_not_capture()
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;
            var at = new Vector2(Track.X + Track.Width * 0.5f, midY);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));   // press-origin inside the track

            ui.Begin(null, p);
            float v = ui.Slider(Track, 0.42f, GuiStyle.Default, enabled: false);
            Assert.Equal(0.42f, v);              // disabled never moves the value
            Assert.False(ui.PointerCaptured);    // and does not reserve its rect
        }

        [Fact]
        public void Interacting_sets_pointer_captured()
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;
            var at = new Vector2(Track.X + Track.Width * 0.5f, midY);

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));   // press-origin inside the track

            ui.Begin(null, p);
            ui.Slider(Track, 0.5f);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void Hovering_without_pressing_leaves_the_value_unchanged()
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;

            p.Update(Frame(new Vector2(Track.X + Track.Width * 0.8f, midY), false)); // hover, not pressing
            ui.Begin(null, p);
            float v = ui.Slider(Track, 0.1f);
            Assert.Equal(0.1f, v);
        }

        [Fact]
        public void Default_style_overload_behaves_like_the_explicit_one()
        {
            var ui = Surface();
            var p = new Pointer();
            float midY = Track.Y + Track.Height * 0.5f;
            var at = new Vector2(Track.X + Track.Width * 0.5f, midY);

            p.Update(Frame(at, false));
            float v = DragTo(ui, p, at.X, midY);
            Assert.Equal(0.5f, v, 0.08f);
        }
    }
}
