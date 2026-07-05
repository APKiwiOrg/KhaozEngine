using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class LabelLocalizedTests
    {
        static readonly Rect R = new(0, 0, 100, 20);

        [Fact]
        public void LocalizedCtor_ResolvesAtAccess()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Play");
                var label = new Label(R, new StringId("Menu.Play"), null!);
                Assert.Equal("Play", label.Resolved);

                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Jouer");
                Assert.Equal("Jouer", label.Resolved); // re-resolves, not cached
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void RawContent_ResolvesLiterally()
        {
            var label = new Label(R, LocalizedText.Raw("v1.2"), null!);
            Assert.Equal("v1.2", label.Resolved);
        }
    }
}
