using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Each retained widget carries a <see cref="GuiStyle"/> <c>Style</c> field (mirroring the retained
    /// <see cref="Button"/>). Since 10.11.0 the default is the crisp look (<see cref="GuiStyle.Default"/>: a
    /// subtle 3px corner radius, hairline border, no bloom), so the field no longer defaults to the old flat
    /// path. These headless contract tests assert the field defaults to the crisp <see cref="GuiStyle.Default"/>
    /// and that assigning <see cref="GuiStyle.Modern"/> switches to the rounded/shadow/gradient/glow look. The
    /// GPU goldens render <see cref="GuiStyle.Modern"/> and raw primitives (never a default-styled widget), so
    /// they are unaffected by the default-look change.
    /// </summary>
    public class RetainedWidgetStyleTests
    {
        static readonly Rect Box = new(10, 10, 200, 30);

        static void AssertCrispDefault(GuiStyle def)
        {
            Assert.Equal(GuiStyle.Default.CornerRadius, def.CornerRadius); // crisp default (3px)
            Assert.False(def.IsFlat);                                      // off the old flat path
        }

        [Fact]
        public void Slider_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new Slider(Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void Toggle_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new Toggle(Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void Panel_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new Panel(Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void Dropdown_Style_defaults_crisp_and_accepts_modern()
        {
            var opts = new List<DropdownOption> { new(LocalizedText.Raw("A"), 0), new(LocalizedText.Raw("B"), 1) };
            var w = new Dropdown(opts, Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void TextInput_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new TextInput(Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void PopupPanel_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new PopupPanel();
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }

        [Fact]
        public void ScrollablePanel_Style_defaults_crisp_and_accepts_modern()
        {
            var w = new ScrollablePanel(Box);
            AssertCrispDefault(w.Style);
            w.Style = GuiStyle.Modern;
            Assert.Equal(7f, w.Style.CornerRadius);
        }
    }
}
