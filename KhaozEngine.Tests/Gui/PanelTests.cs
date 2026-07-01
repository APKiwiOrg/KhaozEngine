using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class PanelTests
    {
        static readonly Rect Box = new(100, 100, 200, 120);

        static InputState Frame(Vector2 pos) => new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);

        [Fact]
        public void Blocking_panel_reserves_its_region_on_the_pointer()
        {
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 150)));
            var panel = new Panel(Box) { BlocksPointer = true };

            panel.Update(p);

            Assert.True(p.IsBlocked(new Vector2(150, 150)));    // inside the panel
            Assert.False(p.IsBlocked(new Vector2(10, 10)));     // outside
        }

        [Fact]
        public void Non_blocking_panel_does_not_reserve_anything()
        {
            var p = new Pointer();
            p.Update(Frame(new Vector2(150, 150)));
            var panel = new Panel(Box);   // BlocksPointer defaults false

            panel.Update(p);

            Assert.False(p.IsBlocked(new Vector2(150, 150)));
        }
    }
}
