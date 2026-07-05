using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    [Collection("AmbientLocalization")]
    public class LocalizedTextTests
    {
        [Fact]
        public void Raw_ResolvesLiterally_IgnoringCatalog()
        {
            var cat = new DictionaryCatalog().Add("v1.2", "SHOULD-NOT-USE");
            LocalizedText t = LocalizedText.Raw("v1.2");
            Assert.True(t.IsRaw);
            Assert.Equal("v1.2", t.Resolve(cat));
        }

        [Fact]
        public void StringId_ResolvesViaCatalog()
        {
            var cat = new DictionaryCatalog().Add("Menu.Play", "Play");
            LocalizedText t = new StringId("Menu.Play"); // implicit conversion
            Assert.False(t.IsRaw);
            Assert.Equal("Play", t.Resolve(cat));
        }

        [Fact]
        public void Of_WithArgs_UsesCatalogFormat()
        {
            var cat = new DictionaryCatalog().Add("Score.Fmt", "Score: {0}");
            LocalizedText t = LocalizedText.Of(new StringId("Score.Fmt"), 42);
            Assert.Equal("Score: 42", t.Resolve(cat));
        }

        [Fact]
        public void Localizable_NoCatalog_ReturnsKeyPlaceholder()
        {
            LocalizedText t = new StringId("Menu.Play");
            Assert.Equal("Menu.Play", t.Resolve(null));
        }

        [Fact]
        public void Default_ResolvesToEmpty()
        {
            LocalizedText t = default;
            Assert.Equal("", t.Resolve(null));
        }

        [Fact]
        public void Resolve_NoArg_UsesAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Play");
                LocalizedText t = new StringId("Menu.Play");
                Assert.Equal("Play", t.Resolve());
                Assert.Equal("Play", t.ToString());
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void LocaleSwitch_ReResolves()
        {
            // Swap the ambient catalog to model a locale change; the same LocalizedText value re-resolves.
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizedText t = new StringId("Menu.Play");
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Play");
                Assert.Equal("Play", t.Resolve());
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Play", "Jouer");
                Assert.Equal("Jouer", t.Resolve());
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
