using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    // Headless decoration checks for the two new GuiSurface draws (Image, CooldownOverlay). The batch is null in
    // Begin(null, ...), so nothing draws and neither call reserves a rect (decoration) - the same headless pattern
    // as IconWidgetTests.Icon_WithNoAtlas_IsNoOpAndDoesNotThrow.
    public class GuiSurfaceImageCooldownTests
    {
        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static GuiSurface Surface() => new(null!, null);   // headless: null batch, nothing draws

        static Pointer PressedInside(Vector2 at)
        {
            var p = new Pointer();
            p.Update(Frame(at, false));   // idle inside
            p.Update(Frame(at, true));    // press-origin inside the rect
            return p;
        }

        [Fact]
        public void Image_Headless_IsNoOpAndReservesNothing()
        {
            var ui = Surface();
            Pointer p = PressedInside(new Vector2(16, 16));
            ui.Begin(null, p);
            // Headless: the batch is null so the null texture is never dereferenced.
            ui.Image(new Rect(0, 0, 32, 32), null!, new Vector4(0, 0, 1, 1), Vector4.One);
            Assert.False(ui.PointerCaptured);   // decoration reserves no rect
        }

        [Fact]
        public void CooldownOverlay_Headless_IsNoOpAndReservesNothing()
        {
            var ui = Surface();
            Pointer p = PressedInside(new Vector2(16, 16));
            ui.Begin(null, p);
            ui.CooldownOverlay(new Rect(0, 0, 32, 32), 0.5f);
            Assert.False(ui.PointerCaptured);   // decoration reserves no rect
        }

        [Fact]
        public void DefaultCooldownTint_IsTranslucentBlack()
        {
            Assert.Equal(0f, GuiSurface.DefaultCooldownTint.X, 3);
            Assert.Equal(0f, GuiSurface.DefaultCooldownTint.Y, 3);
            Assert.Equal(0f, GuiSurface.DefaultCooldownTint.Z, 3);
            Assert.True(GuiSurface.DefaultCooldownTint.W > 0f && GuiSurface.DefaultCooldownTint.W < 1f);
        }
    }
}
