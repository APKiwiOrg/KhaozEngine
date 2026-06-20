using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class IconWidgetTests
    {
        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Headless: null white is never drawn because the batch is null in Begin(null, ...).
        static GuiSurface Surface() => new(null!, null);

        [Fact]
        public void Icon_WithNoAtlas_IsNoOpAndDoesNotThrow()
        {
            var ui = Surface();
            var p = new Pointer();
            p.Update(Frame(new Vector2(16, 16), false));   // idle inside the icon rect
            p.Update(Frame(new Vector2(16, 16), true));    // press-origin inside the icon rect

            ui.Begin(null, p);          // no IconAtlas set -> Icon is a no-op
            ui.Icon(new Rect(0, 0, 32, 32), Icons.Coin, Vector4.One);
            // Decoration: reserves nothing, so a press inside its rect is not captured.
            Assert.False(ui.PointerCaptured);
        }

        [Fact]
        public void IconButton_ReturnsTrueOnTapInAndReservesRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(10, 10, 40, 40);
            var at = new Vector2(30, 30);   // inside rect

            p.Update(Frame(at, false));     // idle inside
            p.Update(Frame(at, true));      // press-origin inside
            p.Update(Frame(at, false));     // release inside -> tap fires

            ui.Begin(null, p);
            bool clicked = ui.IconButton(rect, Icons.Play, GuiStyle.Default);
            Assert.True(clicked);
            Assert.True(ui.PointerCaptured);
        }

        [Fact]
        public void IconButton_ReturnsFalseWhenPressOriginOutsideRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(10, 10, 40, 40);
            p.Update(Frame(new Vector2(5, 5), false));     // idle outside
            p.Update(Frame(new Vector2(5, 5), true));      // press-origin OUTSIDE
            p.Update(Frame(new Vector2(30, 30), false));   // release inside
            ui.Begin(null, p);
            Assert.False(ui.IconButton(rect, Icons.Play, GuiStyle.Default));
        }

        [Fact]
        public void StatChip_ReservesItsRect()
        {
            var ui = Surface();
            var p = new Pointer();
            var rect = new Rect(0, 0, 120, 36);
            var at = new Vector2(60, 18);   // inside rect

            p.Update(Frame(at, false));
            p.Update(Frame(at, true));      // pressed, press-origin inside rect

            ui.Begin(null, p);
            ui.StatChip(rect, Icons.Coin, "Gold", "120", font: null!, GuiStyle.Default);
            Assert.True(ui.PointerCaptured);
        }
    }
}
