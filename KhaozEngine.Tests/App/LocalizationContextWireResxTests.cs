using System.Globalization;
using System.Resources;
using KhaozEngine.App;
using Xunit;

namespace KhaozEngine.Tests.App
{
    // WireResx mutates the process-wide LocalizationContext.Catalog: share the ambient-localization serial collection.
    [Collection("AmbientLocalization")]
    public class LocalizationContextWireResxTests
    {
        private const string BaseName = "KhaozEngine.Tests.Localization.Fixtures.CoverageFixtureStrings";

        private static ResourceManager Rm()
            => new ResourceManager(BaseName, typeof(LocalizationContextWireResxTests).Assembly);

        [Fact]
        public void WireResx_InstallsCatalog_AndReturnsSameInstance()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                ResourceStringCatalog installed = LocalizationContext.WireResx(Rm());
                Assert.NotNull(LocalizationContext.Catalog);
                Assert.Same(installed, LocalizationContext.Catalog);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void WireResx_ResolvesCultureLive_NotCapturedOnce()
        {
            IStringCatalog? prevCatalog = LocalizationContext.Catalog;
            CultureInfo prevCulture = CultureInfo.CurrentUICulture;
            try
            {
                IStringCatalog catalog = LocalizationContext.WireResx(Rm());

                CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
                Assert.Equal("Play", catalog.Get("menu.play"));

                // Switch culture AFTER wiring: the same catalog re-reads CurrentUICulture at resolve time.
                CultureInfo.CurrentUICulture = new CultureInfo("fr");
                Assert.Equal("Jouer", catalog.Get("menu.play"));
                Assert.Equal("Score : 7", catalog.Format("hud.score", 7));
            }
            finally
            {
                CultureInfo.CurrentUICulture = prevCulture;
                LocalizationContext.Catalog = prevCatalog;
            }
        }

        [Fact]
        public void WireResx_BaseNameAndAssemblyOverload_Works()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.WireResx(BaseName, typeof(LocalizationContextWireResxTests).Assembly);
                Assert.NotNull(LocalizationContext.Catalog);
                Assert.Equal("Quit", LocalizationContext.Catalog!.Get("menu.quit"));
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void WireResx_NullResourceManager_Throws()
            => Assert.Throws<System.ArgumentNullException>(() => LocalizationContext.WireResx((ResourceManager)null!));
    }
}
