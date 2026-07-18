using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class TooltipLocalizedTests
    {
        [Fact]
        public void Of_ResolvesLineTextViaAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Tip.Body", "Body text");
                var line = TooltipLine.Of(new StringId("Tip.Body"), Vector4.One);
                Assert.Equal("Body text", line.Text);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Of_Raw_ResolvesLiterally()
        {
            var line = TooltipLine.Of(LocalizedText.Raw("42%"), Vector4.One);
            Assert.Equal("42%", line.Text);
        }
    }
}
