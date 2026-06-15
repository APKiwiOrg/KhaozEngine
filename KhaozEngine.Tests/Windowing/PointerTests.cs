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
