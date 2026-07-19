using KhaozEngine.App;
using KhaozEngine.Gui;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for <see cref="Button.LabelScale"/>: the default keeps every existing caller
    /// byte-identical (<c>1f</c>). The positioning math itself is already locked by <see cref="GuiTextScaleTests"/>
    /// (<see cref="GuiDraw.AlignedTextPos"/> at scale 1 reproduces the unscaled layout, non-1 scales width and
    /// centring). The forward from <see cref="Button.Draw"/> into <see cref="GuiDraw.DrawButton"/> has its own
    /// regression net on-device (<c>ButtonLabelScaleGpuTests</c>), since no pure test can see a dropped argument.
    /// </summary>
    public class ButtonScaleTests
    {
        static readonly Rect Btn = new(100, 100, 120, 40);

        [Fact]
        public void LabelScale_defaults_to_one()
        {
            var btn = new Button(Btn, LocalizedText.Raw("Go"), null!);
            Assert.Equal(1f, btn.LabelScale);
        }
    }
}
