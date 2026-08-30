using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Discord;
using KhaozEngine.Identity.Oidc;
using Xunit;
using DiscordSignInException = KhaozEngine.Identity.Discord.IdentitySignInException;
using OidcSignInException = KhaozEngine.Identity.Oidc.IdentitySignInException;

namespace KhaozEngine.Tests.Identity;

/// <summary>Pins the shared <see cref="SignInException"/> base that both provider backends' own
/// <c>IdentitySignInException</c> derives from. This file imports BOTH provider namespaces at once, which makes
/// the bare <c>IdentitySignInException</c> ambiguous between them (hence the two aliases above), and that is
/// exactly why cross-provider consumer code wants one base type to catch instead of one per backend.</summary>
public class SignInExceptionBaseTests
{
    /// <summary>Round-trips the provider-generated state from the authorize URL to the fake listener, the same
    /// shape the per-provider tests use.</summary>
    private sealed class StateHolder
    {
        public string? State;
    }

    private sealed class FakeBrowser(StateHolder holder) : IBrowserLauncher
    {
        public Task<bool> LaunchAsync(Uri url, CancellationToken ct = default)
        {
            var q = HttpUtility.ParseQueryString(url.Query);
            holder.State = q["state"];
            return Task.FromResult(true);
        }
    }

    /// <summary>Echoes back a redirect state that never matches the provider-generated one, which is the
    /// cheapest real sign-in failure both backends share.</summary>
    private sealed class MismatchListener : ILoopbackListener
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:12345/");

        public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
            => Task.FromResult(new LoopbackResult(RedirectUri.ToString(),
                new Dictionary<string, string> { ["code"] = "auth-code", ["state"] = "not-the-real-state" }));

        public void Dispose() { }
    }

    /// <summary>Serves the OIDC discovery document (fetched before the browser launches) and a token response.
    /// The state mismatch fires before the token endpoint is ever reached.</summary>
    private sealed class FakeTokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("well-known"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"issuer\":\"https://issuer.test\",\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}",
                    Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"id_token\":\"the-id-token\",\"access_token\":\"the-access-token\",\"expires_in\":3600}",
                Encoding.UTF8, "application/json") });
        }
    }

    private static OidcClientProvider NewOidcProviderThatFailsSignIn()
    {
        StateHolder holder = new();
        return new OidcClientProvider(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new MismatchListener(), new HttpClient(new FakeTokenHandler()));
    }

    private static DiscordClientProvider NewDiscordProviderThatFailsSignIn()
    {
        StateHolder holder = new();
        return new DiscordClientProvider(
            new DiscordProviderOptions { ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new MismatchListener(), new HttpClient(new FakeTokenHandler()));
    }

    [Fact]
    public void Oidc_sign_in_exception_derives_from_the_shared_base()
    {
        OidcSignInException ex = new("boom");

        Assert.IsAssignableFrom<SignInException>(ex);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Discord_sign_in_exception_derives_from_the_shared_base()
    {
        DiscordSignInException ex = new("boom");

        Assert.IsAssignableFrom<SignInException>(ex);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Both_provider_exceptions_keep_their_inner_exception_constructor()
    {
        InvalidOperationException inner = new("cause");

        Exception oidc = new OidcSignInException("outer", inner);
        Exception discord = new DiscordSignInException("outer", inner);

        Assert.Same(inner, oidc.InnerException);
        Assert.Same(inner, discord.InnerException);
    }

    [Fact]
    public async Task Base_catch_catches_a_real_oidc_sign_in_failure()
    {
        OidcClientProvider provider = NewOidcProviderThatFailsSignIn();

        SignInException caught = await Assert.ThrowsAnyAsync<SignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.IsType<OidcSignInException>(caught);
    }

    [Fact]
    public async Task Base_catch_catches_a_real_discord_sign_in_failure()
    {
        DiscordClientProvider provider = NewDiscordProviderThatFailsSignIn();

        SignInException caught = await Assert.ThrowsAnyAsync<SignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.IsType<DiscordSignInException>(caught);
    }

    /// <summary>The dedup payoff: one catch clause, written against the core package alone, handles a sign-in
    /// failure from either backend. Before the shared base a consumer offering both providers needed one catch
    /// per provider package.</summary>
    [Fact]
    public async Task One_base_catch_handles_a_failure_from_either_backend()
    {
        List<string> failures = [];

        foreach (IIdentityProvider provider in new IIdentityProvider[]
                 { NewOidcProviderThatFailsSignIn(), NewDiscordProviderThatFailsSignIn() })
        {
            try
            {
                await provider.SignInAsync(CancellationToken.None);
            }
            catch (SignInException ex)
            {
                failures.Add($"{provider.ProviderId}: {ex.Message}");
            }
        }

        Assert.Equal(2, failures.Count);
        Assert.All(failures, f => Assert.Contains("state mismatch", f));
    }
}
