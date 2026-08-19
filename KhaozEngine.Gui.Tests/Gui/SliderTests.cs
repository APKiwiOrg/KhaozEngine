using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class SliderTests
    {
        // Track spans X 100..300 (width 200), Y 100..120.
        static readonly Rect Track = new(100, 100, 200, 20);

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        Pointer Pressing(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));   // up
            p.Update(Frame(at, true));    // press at `at`
            return p;
        }

        [Fact]
        public void Press_at_track_center_sets_value_to_half()
        {
            var slider = new Slider(Track);
            var p = Pressing(new Vector2(200, 110));   // center X
            bool changed = slider.Update(p);
            Assert.True(changed);
            Assert.Equal(0.5f, slider.Value, 3);
        }

        [Fact]
        public void Dragging_past_the_right_edge_clamps_to_one()
        {
            var slider = new Slider(Track);
            var p = Pressing(new Vector2(200, 110));
            slider.Update(p);
            p.Update(Frame(new Vector2(400, 110), true));   // drag well past the right edge, still down
            slider.Update(p);
            Assert.Equal(1f, slider.Value, 3);
        }

        [Fact]
        public void Dragging_past_the_left_edge_clamps_to_zero()
        {
            var slider = new Slider(Track, 0.5f);
            var p = Pressing(new Vector2(200, 110));
            slider.Update(p);
            p.Update(Frame(new Vector2(10, 110), true));   // drag past the left edge
            slider.Update(p);
            Assert.Equal(0f, slider.Value, 3);
        }

        [Fact]
        public void Press_that_began_outside_the_track_does_not_move_the_value()
        {
            var slider = new Slider(Track, 0.25f);
            var p = Pressing(new Vector2(10, 110));        // press began OUTSIDE the track
            slider.Update(p);
            p.Update(Frame(new Vector2(200, 110), true));  // dragged into the track, still down
            bool changed = slider.Update(p);
            Assert.False(changed);
            Assert.Equal(0.25f, slider.Value, 3);          // unchanged
        }

        [Fact]
        public void Disabled_slider_ignores_input()
        {
            var slider = new Slider(Track, 0.25f) { Enabled = false };
            var p = Pressing(new Vector2(300, 110));
            bool changed = slider.Update(p);
            Assert.False(changed);
            Assert.Equal(0.25f, slider.Value, 3);
        }

        [Fact]
        public void Update_returns_false_when_the_value_does_not_change()
        {
            var slider = new Slider(Track);
            var p = Pressing(new Vector2(200, 110));
            slider.Update(p);                              // value becomes 0.5, changed=true
            bool changed = slider.Update(p);              // same position held, no movement
            Assert.False(changed);
        }
    }
}
