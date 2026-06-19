using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    public class PointerTests
    {
        static readonly Vector2 Inside = new(150, 140);
        static readonly Vector2 Outside = new(10, 10);
        static readonly Rect Box = new(100, 100, 200, 80);

        static InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        [Fact]
        public void Tap_in_design_space_under_a_scaled_letterboxed_viewport()
        {
            // 960x540 design Fit into a 1920x1200 window -> scale 2, 60px top/bottom bars.
            var vp = new DesignViewport(960, 540, ScaleMode.Fit);
            vp.Update(1920, 1200);
            // Box is in DESIGN space; the screen click is where that design point lands on the window.
            Vector2 screen = vp.DesignToScreen(new Vector2(150, 140));   // -> (300, 340)
            Assert.Equal(new Vector2(300, 340), screen);

            InputState Win(bool down)
            {
                var b = new HashSet<MouseButton>();
                if (down) b.Add(MouseButton.Left);
                return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                    b, new HashSet<MouseButton>(), screen, Vector2.Zero, 0, 1920, 1200);
            }

            var p = new Pointer();
            p.Update(Win(false), vp);
            p.Update(Win(true), vp);
            p.Update(Win(false), vp);

            Assert.True(p.IsTapIn(Box));                       // design-space hit-test lines up
            Assert.Equal(new Vector2(150, 140), p.Position);   // pointer reported in design space
        }

        [Fact]
        public void Tap_inside_fires_on_release()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));   // up
            p.Update(Frame(Inside, true));    // press inside -> press-origin = inside
            Assert.False(p.IsTapIn(Box));     // not yet (still down)
            p.Update(Frame(Inside, false));   // release inside
            Assert.True(p.IsTapIn(Box));
        }

        [Fact]
        public void Press_outside_release_inside_is_not_a_tap()  // the click-through invariant
        {
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(Frame(Outside, true));   // press began OUTSIDE the box
            p.Update(Frame(Inside, true));    // dragged inside, still down
            p.Update(Frame(Inside, false));   // released inside
            Assert.False(p.IsTapIn(Box));     // press-origin was outside -> no tap
        }

        [Fact]
        public void Press_inside_release_outside_is_not_a_tap_and_is_released_outside()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));    // press inside
            p.Update(Frame(Outside, false));  // release outside
            Assert.False(p.IsTapIn(Box));
            Assert.True(p.IsReleasedOutside(Box));
        }

        [Fact]
        public void IsPressingIn_while_held_inside()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.Update(Frame(Inside, true));
            Assert.True(p.IsPressingIn(Box));
            Assert.True(p.IsDown);
            Assert.True(p.IsJustPressed);
        }

        [Fact]
        public void IsDragStartIn_true_while_a_press_that_began_inside_is_held()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            Assert.False(p.IsDragStartIn(Box));   // not pressed yet
            p.Update(Frame(Inside, true));        // press began inside -> grabbed
            Assert.True(p.IsDragStartIn(Box));
            p.Update(Frame(Outside, true));       // cursor strays out, still down -> keeps the grab
            Assert.True(p.IsDragStartIn(Box));
            p.Update(Frame(Outside, false));      // released -> grab ends
            Assert.False(p.IsDragStartIn(Box));
        }

        [Fact]
        public void IsDragStartIn_false_when_the_press_began_outside()  // press-origin invariant
        {
            var p = new Pointer();
            p.Update(Frame(Outside, false));
            p.Update(Frame(Outside, true));       // press began OUTSIDE the box
            p.Update(Frame(Inside, true));        // dragged inside, still down
            Assert.False(p.IsDragStartIn(Box));   // press-origin was outside -> never grabs
        }

        [Fact]
        public void Region_blocking()
        {
            var p = new Pointer();
            p.Update(Frame(Inside, false));
            p.BlockRegion(Box);
            Assert.True(p.IsBlocked(Inside));
            Assert.False(p.IsBlocked(Outside));
            p.Update(Frame(Inside, false));   // cleared each Update
            Assert.False(p.IsBlocked(Inside));
        }
    }
}
