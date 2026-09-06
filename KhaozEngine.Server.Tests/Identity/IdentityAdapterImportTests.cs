// Compile-time proof that existing OIDC consumer imports remain source-compatible. The old package README
// imported both namespaces and constructed the adapter names unqualified. Adding names with the same simple
// spelling to the core namespace turns these lines into CS0104 ambiguous references.

namespace KhaozEngine.Tests.Identity.AdapterImports
{
    using KhaozEngine.Identity;
    using KhaozEngine.Identity.Oidc;
    using Xunit;

    public class IdentityAdapterImportTests
    {
        [Fact]
        public void Existing_oidc_imports_keep_unqualified_adapter_names()
        {
            IBrowserLauncher browser = new SystemBrowserLauncher();
            using ILoopbackListener listener = new HttpLoopbackListener(0);

            Assert.Equal("KhaozEngine.Identity.Oidc", browser.GetType().Assembly.GetName().Name);
            Assert.Equal("KhaozEngine.Identity.Oidc", listener.GetType().Assembly.GetName().Name);
        }
    }
}
