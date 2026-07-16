using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class ToastLocalizedTests
    {
        [Fact]
        public void LocalizedMessage_ResolvesThroughAmbientCatalog()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Toast.Saved", "Saved");
                var stack = new ToastStack();
                Toast toast = stack.Show(new StringId("Toast.Saved"));

                Assert.Equal("Saved", toast.Message.Resolve());
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void LocalizedMessage_ReResolvesAfterCatalogSwap()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Toast.Saved", "Saved");
                var stack = new ToastStack();
                Toast toast = stack.Show(new StringId("Toast.Saved"));
                Assert.Equal("Saved", toast.Message.Resolve());

                LocalizationContext.Catalog = new DictionaryCatalog().Add("Toast.Saved", "Enregistre");
                Assert.Equal("Enregistre", toast.Message.Resolve()); // re-resolves, not cached
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void RawMessage_ResolvesLiterally()
        {
            var stack = new ToastStack();
            Toast toast = stack.Show(LocalizedText.Raw("v1.2"));

            Assert.Equal("v1.2", toast.Message.Resolve());
        }
    }
}
