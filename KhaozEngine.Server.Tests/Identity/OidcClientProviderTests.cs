using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Oidc;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class OidcClientProviderTests
{
    /// <summary>Round-trips the provider-generated state from the authorize URL (read by the fake browser at
    /// launch time) to the fake loopback listener (which is created BEFORE the browser launches, per the real
    /// ordering), so the fake listener can echo back the same state without racing construction order.</summary>
    private sealed class StateHolder
    {
        public string? State;
    }

    private sealed class FakeBrowser(StateHolder holder) : IBrowserLauncher
    {
        public Uri? Launched;

        public Task<bool> LaunchAsync(Uri url, CancellationToken ct = default)
        {
            Launched = url;
            var q = HttpUtility.ParseQueryString(url.Query);
            holder.State = q["state"];
            return Task.FromResult(true);
        }
    }

    private sealed class FakeListener(StateHolder holder) : ILoopbackListener
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:12345/");

        public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
            => Task.FromResult(new LoopbackResult(RedirectUri.ToString(),
                new Dictionary<string, string> { ["code"] = "auth-code", ["state"] = holder.State ?? "" }));

        public void Dispose() { }
    }

    private sealed class FakeTokenHandler : HttpMessageHandler
    {
        public string? SeenVerifier;
        public string? SeenGrantType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("well-known"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"issuer\":\"https://issuer.test\",\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}",
                    Encoding.UTF8, "application/json") };
            string form = await req.Content!.ReadAsStringAsync(ct);
            var q = HttpUtility.ParseQueryString(form);
            SeenVerifier = q["code_verifier"];
            SeenGrantType = q["grant_type"];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"id_token\":\"the-id-token\",\"refresh_token\":\"the-refresh\",\"expires_in\":3600}",
                Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task SignIn_runs_pkce_flow_and_returns_credential()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        FakeTokenHandler handler = new();
        HttpClient http = new(handler);
        Func<int, ILoopbackListener> listenerFactory = _ => new FakeListener(holder);
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, listenerFactory, http);

        ProviderCredential cred = await provider.SignInAsync(CancellationToken.None);

        Assert.Equal("oidc", cred.ProviderId);
        Assert.Equal("the-id-token", cred.CredentialToken);
        Assert.Equal("the-refresh", cred.RefreshToken);
        Assert.NotNull(browser.Launched);
        var authQ = HttpUtility.ParseQueryString(browser.Launched!.Query);
        Assert.False(string.IsNullOrEmpty(authQ["code_challenge"]));
        Assert.Equal("S256", authQ["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(authQ["state"]));
        Assert.False(string.IsNullOrEmpty(handler.SeenVerifier));
        Assert.Equal("authorization_code", handler.SeenGrantType);
    }

    [Fact]
    public async Task SignIn_throws_when_redirect_state_mismatches()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        FakeTokenHandler handler = new();
        HttpClient http = new(handler);
        // The listener returns a state that never matches the provider-generated one.
        Func<int, ILoopbackListener> listenerFactory = _ => new MismatchListener();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, listenerFactory, http);

        await Assert.ThrowsAsync<IdentitySignInException>(() => provider.SignInAsync(CancellationToken.None));
    }

    private sealed class MismatchListener : ILoopbackListener
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:12345/");

        public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
            => Task.FromResult(new LoopbackResult(RedirectUri.ToString(),
                new Dictionary<string, string> { ["code"] = "auth-code", ["state"] = "not-the-real-state" }));

        public void Dispose() { }
    }

    [Fact]
    public async Task RefreshAsync_posts_refresh_grant_and_returns_new_credential()
    {
        FakeTokenHandler handler = new();
        HttpClient http = new(handler);
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(new StateHolder()), _ => new FakeListener(new StateHolder()), http);

        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);
        ProviderCredential? refreshed = await provider.RefreshAsync(expired, CancellationToken.None);

        Assert.NotNull(refreshed);
        Assert.Equal("oidc", refreshed!.Value.ProviderId);
        Assert.Equal("the-id-token", refreshed.Value.CredentialToken);
        Assert.Equal("the-refresh", refreshed.Value.RefreshToken);
        Assert.Equal("refresh_token", handler.SeenGrantType);
    }

    /// <summary>Serves the well-known discovery document, then a fixed status for the token POST, so the
    /// refresh rejection contract is testable the same way as the Discord provider.</summary>
    private sealed class StatusTokenHandler(HttpStatusCode tokenStatus) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("well-known"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"issuer\":\"https://issuer.test\",\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}",
                        Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(tokenStatus)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static OidcClientProvider ProviderWith(HttpMessageHandler handler)
        => new(new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(new StateHolder()), _ => new FakeListener(new StateHolder()), new HttpClient(handler));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RefreshAsync_returns_null_on_dead_chain(HttpStatusCode status)
    {
        OidcClientProvider provider = ProviderWith(new StatusTokenHandler(status));
        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);

        ProviderCredential? refreshed = await provider.RefreshAsync(expired, CancellationToken.None);

        Assert.Null(refreshed);
    }

    [Fact]
    public async Task RefreshAsync_throws_on_transient_status()
    {
        OidcClientProvider provider = ProviderWith(new StatusTokenHandler(HttpStatusCode.ServiceUnavailable));
        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.RefreshAsync(expired, CancellationToken.None));
    }

    [Fact]
    public async Task SignIn_still_throws_on_token_endpoint_bad_request()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        HttpClient http = new(new StatusTokenHandler(HttpStatusCode.BadRequest));
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, _ => new FakeListener(holder), http);

        await Assert.ThrowsAsync<IdentitySignInException>(() => provider.SignInAsync(CancellationToken.None));
    }

    /// <summary>Serves the well-known discovery document, then a caller-chosen 200 body for the token POST, so a
    /// malformed-but-successful token response is testable.</summary>
    private sealed class BodyTokenHandler(string tokenBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("well-known"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"issuer\":\"https://issuer.test\",\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}",
                        Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenBody, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>#168: a string-typed or out-of-Int32-range <c>expires_in</c> used to escape as a raw
    /// <see cref="InvalidOperationException"/>/<see cref="FormatException"/> from an unguarded
    /// <c>GetInt32()</c>, past every caller catching the provider's own failure type.</summary>
    [Theory]
    [InlineData("{\"id_token\":\"the-id-token\",\"expires_in\":\"3600\"}")]
    [InlineData("{\"id_token\":\"the-id-token\",\"expires_in\":99999999999}")]
    [InlineData("{\"id_token\":\"the-id-token\",\"expires_in\":null}")]
    public async Task SignIn_throws_sign_in_failure_on_malformed_expires_in(string tokenBody)
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, _ => new FakeListener(holder), new HttpClient(new BodyTokenHandler(tokenBody)));

        await Assert.ThrowsAsync<IdentitySignInException>(() => provider.SignInAsync(CancellationToken.None));
    }

    /// <summary>A captive portal (hotel/airport wifi) or a misconfigured reverse proxy answering 200 with an
    /// HTML interstitial in place of the discovery document.</summary>
    private sealed class HtmlDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>Sign in to the hotel network</body></html>", Encoding.UTF8, "text/html"),
            });
    }

    /// <summary>#175: a non-JSON 200 used to throw a raw JsonException straight out of sign-in, past every
    /// caller catching the provider's own failure type.</summary>
    [Fact]
    public async Task SignIn_throws_sign_in_failure_when_discovery_returns_non_json()
    {
        StateHolder holder = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new FakeListener(holder), new HttpClient(new HtmlDiscoveryHandler()));

        await Assert.ThrowsAsync<IdentitySignInException>(() => provider.SignInAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_throws_sign_in_failure_when_discovery_returns_non_json()
    {
        StateHolder holder = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new FakeListener(holder), new HttpClient(new HtmlDiscoveryHandler()));
        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.RefreshAsync(expired, CancellationToken.None));
    }

    [Fact]
    public async Task SignIn_throws_sign_in_failure_when_token_endpoint_returns_non_json()
    {
        StateHolder holder = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new FakeListener(holder),
            new HttpClient(new BodyTokenHandler("<html><body>Sign in to the hotel network</body></html>")));

        await Assert.ThrowsAsync<IdentitySignInException>(() => provider.SignInAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SignIn_treats_an_absent_expires_in_as_no_declared_lifetime()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, _ => new FakeListener(holder),
            new HttpClient(new BodyTokenHandler("{\"id_token\":\"the-id-token\"}")));

        DateTimeOffset before = DateTimeOffset.UtcNow;
        ProviderCredential cred = await provider.SignInAsync(CancellationToken.None);

        Assert.Equal("the-id-token", cred.CredentialToken);
        Assert.InRange(cred.ExpiresAtUtc, before, DateTimeOffset.UtcNow);
    }
}
