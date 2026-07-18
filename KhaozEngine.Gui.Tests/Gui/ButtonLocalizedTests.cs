using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public class ButtonLocalizedTests
    {
        static readonly Rect R = new(0, 0, 80, 30);

        [Fact]
        public void LocalizedCtor_ResolvesLabel()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("Menu.Go", "Go");
                var b = new Button(R, new StringId("Menu.Go"), null!);
                Assert.Equal("Go", b.Resolved);
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
