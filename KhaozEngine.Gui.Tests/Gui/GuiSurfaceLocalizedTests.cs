using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class GuiSurfaceLocalizedTests
    {
        [Fact]
        public void Button_LocalizedOverload_ClickSemanticsIntact()
        {
            var ui = new GuiSurface(null!);
            var pointer = new Pointer();
            ui.Begin(null, pointer); // headless
            bool clicked = ui.Button(null!, new Rect(0, 0, 50, 20), new StringId("Any"));
            Assert.False(clicked); // no tap this frame
        }
    }
}
