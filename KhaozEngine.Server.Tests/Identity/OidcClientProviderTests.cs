using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

        /// <summary>What the launcher reports. False is a browser that could not open the authorize URL.</summary>
        public bool Opens = true;

        public Task<bool> LaunchAsync(Uri url, CancellationToken ct = default)
        {
            Launched = url;
            var q = HttpUtility.ParseQueryString(url.Query);
            holder.State = q["state"];
            return Task.FromResult(Opens);
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
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                    "{\"issuer\":\"https://issuer.test\",\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}",
                    Encoding.UTF8, "application/json")
                };
            string form = await req.Content!.ReadAsStringAsync(ct);
            var q = HttpUtility.ParseQueryString(form);
            SeenVerifier = q["code_verifier"];
            SeenGrantType = q["grant_type"];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                "{\"id_token\":\"the-id-token\",\"refresh_token\":\"the-refresh\",\"expires_in\":3600}",
                Encoding.UTF8, "application/json")
            };
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

    /// <summary>Echoes the real redirect, then applies one named defect to its query. <c>none</c> is the control.</summary>
    private sealed class DefectiveRedirectListener(StateHolder holder, string defect) : ILoopbackListener
    {
        public Uri RedirectUri { get; } = new("http://127.0.0.1:12345/");

        public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
        {
            Dictionary<string, string> query = new() { ["code"] = "auth-code", ["state"] = holder.State ?? "" };
            switch (defect)
            {
                case "state-mismatch": query["state"] = "not-the-real-state"; break;
                case "state-missing": query.Remove("state"); break;
                case "code-missing": query.Remove("code"); break;
                case "code-empty": query["code"] = ""; break;
            }
            return Task.FromResult(new LoopbackResult(RedirectUri.ToString(), query));
        }

        public void Dispose() { }
    }

    private static OidcClientProvider ProviderWithRedirect(StateHolder holder, string defect)
        => new(new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new DefectiveRedirectListener(holder, defect),
            new HttpClient(new FakeTokenHandler()));

    /// <summary>Neither state value nor the code may reach the refusal: the state is the CSRF guard and the code is
    /// redeemable with the verifier.</summary>
    [Theory]
    [InlineData("state-mismatch", SignInRefusal.RedirectStateMismatch)]
    [InlineData("state-missing", SignInRefusal.RedirectStateMismatch)]
    [InlineData("code-missing", SignInRefusal.AuthorizationCodeMissing)]
    [InlineData("code-empty", SignInRefusal.AuthorizationCodeMissing)]
    public async Task SignIn_refuses_a_redirect_that_breaks_one_rule(string defect, SignInRefusal expected)
    {
        StateHolder holder = new();

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => ProviderWithRedirect(holder, defect).SignInAsync(CancellationToken.None));

        Assert.Equal(expected, ex.Reason);
        Assert.Equal("oidc", ex.ProviderId);
        SignInRefusalSecrets.AssertCarriesNone(ex, holder.State!, "not-the-real-state", "auth-code");

        ProviderCredential control = await ProviderWithRedirect(new StateHolder(), "none").SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    [Fact]
    public async Task SignIn_refuses_when_the_browser_does_not_launch()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder) { Opens = false };
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            browser, _ => new FakeListener(holder), new HttpClient(new FakeTokenHandler()));

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.BrowserLaunchFailed, ex.Reason);
        Assert.Equal("oidc", ex.ProviderId);

        browser.Opens = true;
        Assert.Equal("the-id-token", (await provider.SignInAsync(CancellationToken.None)).CredentialToken);
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

    /// <summary>A provider whose browser and listener share one state, so its interactive sign-in can succeed.</summary>
    private static OidcClientProvider SignInProviderWith(HttpMessageHandler handler)
    {
        StateHolder holder = new();
        return new(new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new FakeListener(holder), new HttpClient(handler));
    }

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

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.RefreshAsync(expired, CancellationToken.None));

        Assert.Equal(SignInRefusal.TokenHttpFailure, ex.Reason);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(SignInEndpointRole.TokenEndpoint, ex.EndpointRole);
        Assert.Equal("oidc", ex.ProviderId);
        SignInRefusalSecrets.AssertCarriesNone(ex, "old-token", "old-refresh");

        Assert.NotNull(await ProviderWith(new FakeTokenHandler()).RefreshAsync(expired, CancellationToken.None));
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

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.TokenHttpFailure, ex.Reason);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(SignInEndpointRole.TokenEndpoint, ex.EndpointRole);
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

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.TokenExpiryMalformed, ex.Reason);
        Assert.Equal("expires_in", ex.MemberName);
        SignInRefusalSecrets.AssertCarriesNone(ex, "the-id-token");

        ProviderCredential control = await SignInProviderWith(
            new BodyTokenHandler("{\"id_token\":\"the-id-token\",\"expires_in\":3600}")).SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    [Theory]
    [InlineData("{\"refresh_token\":\"leaked-refresh\",\"expires_in\":3600}", SignInRefusal.TokenMemberMissing)]
    [InlineData("{\"id_token\":\"\",\"refresh_token\":\"leaked-refresh\"}", SignInRefusal.TokenMemberEmpty)]
    [InlineData("{\"id_token\":null,\"refresh_token\":\"leaked-refresh\"}", SignInRefusal.TokenMemberEmpty)]
    public async Task SignIn_refuses_a_token_response_without_its_id_token(string tokenBody, SignInRefusal expected)
    {
        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => SignInProviderWith(new BodyTokenHandler(tokenBody)).SignInAsync(CancellationToken.None));

        Assert.Equal(expected, ex.Reason);
        Assert.Equal("id_token", ex.MemberName);
        Assert.Equal("oidc", ex.ProviderId);
        SignInRefusalSecrets.AssertCarriesNone(ex, "leaked-refresh");

        ProviderCredential control = await SignInProviderWith(
            new BodyTokenHandler("{\"id_token\":\"the-id-token\",\"refresh_token\":\"leaked-refresh\"}"))
            .SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    /// <summary>Serves a caller-chosen status and body for the discovery document, then a good token response.</summary>
    private sealed class DiscoveryHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(req.RequestUri!.AbsolutePath.Contains("well-known")
                ? new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id_token\":\"the-id-token\"}", Encoding.UTF8, "application/json"),
                });
    }

    private const string GoodDiscovery =
        "{\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":\"https://issuer.test/token\"}";

    [Fact]
    public async Task SignIn_refuses_a_failed_discovery_request()
    {
        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => SignInProviderWith(new DiscoveryHandler(HttpStatusCode.ServiceUnavailable, GoodDiscovery))
                .SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.DiscoveryHttpFailure, ex.Reason);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(SignInEndpointRole.DiscoveryDocument, ex.EndpointRole);
        Assert.Equal("oidc", ex.ProviderId);
        SignInRefusalSecrets.AssertCarriesNone(ex, "issuer.test");

        ProviderCredential control = await SignInProviderWith(new DiscoveryHandler(HttpStatusCode.OK, GoodDiscovery))
            .SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    /// <summary>The refusal names the first member the predicate reaches, so a document lacking both names
    /// <c>authorization_endpoint</c>.</summary>
    [Theory]
    [InlineData("{\"token_endpoint\":\"https://issuer.test/token\"}", SignInRefusal.DiscoveryEndpointMissing, "authorization_endpoint")]
    [InlineData("{\"authorization_endpoint\":\"https://issuer.test/auth\"}", SignInRefusal.DiscoveryEndpointMissing, "token_endpoint")]
    [InlineData("{}", SignInRefusal.DiscoveryEndpointMissing, "authorization_endpoint")]
    [InlineData("{\"authorization_endpoint\":\"\",\"token_endpoint\":\"https://issuer.test/token\"}", SignInRefusal.DiscoveryEndpointEmpty, "authorization_endpoint")]
    [InlineData("{\"authorization_endpoint\":\"https://issuer.test/auth\",\"token_endpoint\":null}", SignInRefusal.DiscoveryEndpointEmpty, "token_endpoint")]
    public async Task SignIn_refuses_a_discovery_document_without_a_usable_endpoint(
        string document, SignInRefusal expected, string member)
    {
        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => SignInProviderWith(new DiscoveryHandler(HttpStatusCode.OK, document)).SignInAsync(CancellationToken.None));

        Assert.Equal(expected, ex.Reason);
        Assert.Equal(member, ex.MemberName);
        Assert.Equal("oidc", ex.ProviderId);
        SignInRefusalSecrets.AssertCarriesNone(ex, "issuer.test");

        ProviderCredential control = await SignInProviderWith(new DiscoveryHandler(HttpStatusCode.OK, GoodDiscovery))
            .SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    /// <summary>The abandoned flow: the player opens the browser and never comes back, so the redirect never
    /// arrives and the wait only ends when its token is cancelled. Records disposal, which is what proves the
    /// loopback port is released rather than held for the life of the process.</summary>
    private sealed class StalledListener : ILoopbackListener
    {
        public bool Disposed;

        public Uri RedirectUri { get; } = new("http://127.0.0.1:12345/");

        public Task<LoopbackResult> WaitForRedirectAsync(CancellationToken ct)
        {
            TaskCompletionSource<LoopbackResult> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>#166: the redirect wait had no bound of its own, so an abandoned flow parked forever, the
    /// using on the listener never ran, and the bound loopback port stayed open for the life of the process.
    /// HttpTimeout never covered this: it only feeds the HttpClient.</summary>
    [Fact]
    public async Task SignIn_times_out_and_releases_the_listener_on_an_abandoned_flow()
    {
        StateHolder holder = new();
        StalledListener listener = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions
            {
                Authority = "https://issuer.test",
                ClientId = "client-1",
                LoopbackPort = 12345,
                SignInTimeout = TimeSpan.FromMilliseconds(50),
            },
            new FakeBrowser(holder), _ => listener, new HttpClient(new FakeTokenHandler()));

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));
        Assert.Equal(SignInRefusal.TimedOut, ex.Reason);
        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.Timeout);
        Assert.Equal("oidc", ex.ProviderId);
        Assert.True(listener.Disposed);
    }

    /// <summary>The caller's own cancellation keeps its own shape: it is not a sign-in failure to retry.</summary>
    [Fact]
    public async Task SignIn_surfaces_caller_cancellation_rather_than_the_timeout()
    {
        StateHolder holder = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new StalledListener(), new HttpClient(new FakeTokenHandler()));

        using CancellationTokenSource cts = new();
        Task<ProviderCredential> signIn = provider.SignInAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
    }

    /// <summary>#170: a plain-http authority put the PKCE verifier and the returned tokens on the wire in
    /// cleartext. Nothing checked the scheme, so the refusal has to come before any request goes out (which is
    /// also before the browser launches).</summary>
    /// <summary>Only the scheme may reach the refusal. Nothing after it does, credentials in the authority
    /// included.</summary>
    [Theory]
    [InlineData("http://issuer.test", SignInRefusal.AuthorityInsecure, "http")]
    [InlineData("http://localhost:8080", SignInRefusal.AuthorityInsecure, "http")]
    [InlineData("ftp://issuer.test", SignInRefusal.AuthorityInsecure, "ftp")]
    [InlineData("http://user:hunter2@issuer.test", SignInRefusal.AuthorityInsecure, "http")]
    [InlineData("not-a-url", SignInRefusal.AuthorityNotAbsolute, null)]
    public async Task SignIn_refuses_a_non_https_authority(string authority, SignInRefusal expected, string? scheme)
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        FakeTokenHandler handler = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = authority, ClientId = "client-1", LoopbackPort = 12345 },
            browser, _ => new FakeListener(holder), new HttpClient(handler));

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));
        Assert.Null(browser.Launched);
        Assert.Null(handler.SeenGrantType);
        Assert.Equal(expected, ex.Reason);
        Assert.Equal(scheme, ex.ActualScheme);
        Assert.Equal("oidc", ex.ProviderId);
        int schemeEnd = authority.IndexOf("://", StringComparison.Ordinal);
        SignInRefusalSecrets.AssertCarriesNone(ex, authority, schemeEnd < 0 ? authority : authority[(schemeEnd + 3)..]);

        ProviderCredential control = await SignInProviderWith(new FakeTokenHandler()).SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
    }

    [Fact]
    public async Task RefreshAsync_refuses_a_non_https_authority()
    {
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "http://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(new StateHolder()), _ => new FakeListener(new StateHolder()),
            new HttpClient(new FakeTokenHandler()));
        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.RefreshAsync(expired, CancellationToken.None));

        Assert.Equal(SignInRefusal.AuthorityInsecure, ex.Reason);
        Assert.Equal("http", ex.ActualScheme);
    }

    [Fact]
    public async Task SignIn_allows_a_loopback_http_authority_under_the_local_dev_opt_out()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        FakeTokenHandler handler = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions
            {
                Authority = "http://127.0.0.1:8080",
                ClientId = "client-1",
                LoopbackPort = 12345,
                AllowInsecureLoopbackAuthority = true,
            },
            browser, _ => new FakeListener(holder), new HttpClient(handler));

        ProviderCredential cred = await provider.SignInAsync(CancellationToken.None);

        Assert.Equal("the-id-token", cred.CredentialToken);
    }

    [Fact]
    public async Task SignIn_opt_out_still_refuses_a_remote_http_authority()
    {
        StateHolder holder = new();
        FakeBrowser browser = new(holder);
        OidcClientProvider provider = new(
            new OidcProviderOptions
            {
                Authority = "http://issuer.test",
                ClientId = "client-1",
                LoopbackPort = 12345,
                AllowInsecureLoopbackAuthority = true,
            },
            browser, _ => new FakeListener(holder), new HttpClient(new FakeTokenHandler()));

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));
        Assert.Null(browser.Launched);
        Assert.Equal(SignInRefusal.AuthorityInsecure, ex.Reason);
        Assert.Equal("http", ex.ActualScheme);
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

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.EndpointJsonMalformed, ex.Reason);
        Assert.Equal(SignInEndpointRole.DiscoveryDocument, ex.EndpointRole);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        SignInRefusalSecrets.AssertCarriesNone(ex, "hotel network");
    }

    [Fact]
    public async Task RefreshAsync_throws_sign_in_failure_when_discovery_returns_non_json()
    {
        StateHolder holder = new();
        OidcClientProvider provider = new(
            new OidcProviderOptions { Authority = "https://issuer.test", ClientId = "client-1", LoopbackPort = 12345 },
            new FakeBrowser(holder), _ => new FakeListener(holder), new HttpClient(new HtmlDiscoveryHandler()));
        ProviderCredential expired = new("oidc", "old-token", "old-refresh", DateTimeOffset.UnixEpoch);

        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => provider.RefreshAsync(expired, CancellationToken.None));

        Assert.Equal(SignInRefusal.EndpointJsonMalformed, ex.Reason);
        Assert.Equal(SignInEndpointRole.DiscoveryDocument, ex.EndpointRole);
    }

    /// <summary>The parser's exception stays the inner exception, so these bodies prove it does not echo a token
    /// from a form-encoded or truncated response.</summary>
    [Theory]
    [InlineData("<html><body>Sign in to the hotel network</body></html>", "hotel network")]
    [InlineData("id_token=leaked-id-token&refresh_token=leaked-refresh", "leaked-id-token")]
    [InlineData("{\"id_token\":\"leaked-id-token", "leaked-id-token")]
    public async Task SignIn_throws_sign_in_failure_when_token_endpoint_returns_non_json(string body, string bodyValue)
    {
        IdentitySignInException ex = await Assert.ThrowsAsync<IdentitySignInException>(
            () => SignInProviderWith(new BodyTokenHandler(body)).SignInAsync(CancellationToken.None));

        Assert.Equal(SignInRefusal.EndpointJsonMalformed, ex.Reason);
        Assert.Equal(SignInEndpointRole.TokenEndpoint, ex.EndpointRole);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        SignInRefusalSecrets.AssertCarriesNone(ex, bodyValue);

        ProviderCredential control = await SignInProviderWith(new BodyTokenHandler("{\"id_token\":\"the-id-token\"}"))
            .SignInAsync(CancellationToken.None);
        Assert.Equal("the-id-token", control.CredentialToken);
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
