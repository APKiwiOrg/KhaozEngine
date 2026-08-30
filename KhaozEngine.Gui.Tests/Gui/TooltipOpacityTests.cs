using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for <see cref="Tooltip.Opacity"/> (#245): the field exists and defaults to <c>1f</c>, so
    /// every existing tooltip draws byte-identically. The fade itself is one <see cref="GuiDraw.WithOpacity"/> call
    /// per colour, already pinned by <see cref="GuiDrawPrimitivesTests"/>. What no pure test can see is a colour the
    /// draw forgot to fade, so the completeness of the forward has its own on-device net
    /// (<c>TooltipOpacityGpuTests</c>: at <c>Opacity = 0</c> the bubble must paint nothing at all).
    /// </summary>
    public class TooltipOpacityTests
    {
        [Fact]
        public void Opacity_defaults_to_one()
        {
            var tip = new Tooltip(null!, null!);
            Assert.Equal(1f, tip.Opacity);
        }
    }
}
