using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    [Collection("AmbientLocalization")]
    public class LocalizationContextTests
    {
        [Fact]
        public void Catalog_DefaultsNull_AndIsSettable()
        {
            var prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                Assert.Null(LocalizationContext.Catalog);

                var fake = new DictionaryCatalog();
                LocalizationContext.Catalog = fake;
                Assert.Same(fake, LocalizationContext.Catalog);
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
