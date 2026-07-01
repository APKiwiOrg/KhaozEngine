using System.Collections.Generic;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Each retained widget now carries a <see cref="GuiStyle"/> <c>Style</c> field (mirroring the retained
    /// <see cref="Button"/>) so it can opt into the modern rounded/shadow/gradient/glow look while staying
    /// byte-identical on the flat default. These headless contract tests assert the field defaults to
    /// <see cref="GuiStyle.Default"/> (which <see cref="GuiStyle.IsFlat"/>, the byte-identical path) and that
    /// assigning <see cref="GuiStyle.Modern"/> flips it off the flat path. The pixel output of both paths is
    /// covered by the gated GPU goldens (the flat path by the existing scene2d grids, the modern primitives by
    /// scene2d_modern); here we only lock the per-widget public surface.
    /// </summary>
    public class RetainedWidgetStyleTests
    {
        static readonly Rect Box = new(10, 10, 200, 30);

        [Fact]
        public void Slider_Style_defaults_flat_and_accepts_modern()
        {
            var w = new Slider(Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void Toggle_Style_defaults_flat_and_accepts_modern()
        {
            var w = new Toggle(Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void Panel_Style_defaults_flat_and_accepts_modern()
        {
            var w = new Panel(Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void Dropdown_Style_defaults_flat_and_accepts_modern()
        {
            var opts = new List<DropdownOption> { new("A", 0), new("B", 1) };
            var w = new Dropdown(opts, Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void TextInput_Style_defaults_flat_and_accepts_modern()
        {
            var w = new TextInput(Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void PopupPanel_Style_defaults_flat_and_accepts_modern()
        {
            var w = new PopupPanel();
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }

        [Fact]
        public void ScrollablePanel_Style_defaults_flat_and_accepts_modern()
        {
            var w = new ScrollablePanel(Box);
            Assert.True(w.Style.IsFlat);
            w.Style = GuiStyle.Modern;
            Assert.False(w.Style.IsFlat);
        }
    }
}
