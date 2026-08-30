// Compile-time proof that the shared SignInException base does not shadow either provider's own exception name.
// Each namespace below imports KhaozEngine.Identity, the base's home, alongside ONE provider namespace and then
// names IdentitySignInException unqualified, exactly the import pair a consumer's sign-in code carries. A base
// sharing the providers' simple name would turn both of these into CS0104 ambiguous references and fail the
// build, which is why the base is named SignInException rather than IdentitySignInException.

namespace KhaozEngine.Tests.Identity.OidcImports
{
    using KhaozEngine.Identity;
    using KhaozEngine.Identity.Oidc;
    using Xunit;

    public class OidcUnqualifiedImportTests
    {
        [Fact]
        public void Unqualified_provider_exception_still_resolves_beside_the_core_namespace()
        {
            IdentitySignInException ex = new("boom");

            Assert.IsType<IdentitySignInException>(ex);
            Assert.IsAssignableFrom<SignInException>(ex);
        }
    }
}

namespace KhaozEngine.Tests.Identity.DiscordImports
{
    using KhaozEngine.Identity;
    using KhaozEngine.Identity.Discord;
    using Xunit;

    public class DiscordUnqualifiedImportTests
    {
        [Fact]
        public void Unqualified_provider_exception_still_resolves_beside_the_core_namespace()
        {
            IdentitySignInException ex = new("boom");

            Assert.IsType<IdentitySignInException>(ex);
            Assert.IsAssignableFrom<SignInException>(ex);
        }
    }
}
