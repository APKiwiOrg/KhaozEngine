using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using KhaozEngine.Identity;

namespace KhaozEngine.Identity.Oidc;

/// <summary>Client-side OIDC sign-in via the system browser + a loopback redirect: authorization-code flow with
/// PKCE (RFC 7636). Discovers <c>authorization_endpoint</c>/<c>token_endpoint</c> from the authority's
/// well-known document, then drives the interactive flow.
///
/// Ordering (see the loopback-race note on <see cref="ILoopbackListener"/>): the authorize URL (with
/// <c>state</c> + PKCE) is built first, then the loopback listener is created (so it is already listening),
/// THEN the browser is launched. Launching the browser before the listener exists risks a fast redirect racing a
/// not-yet-listening loopback.</summary>
public sealed class OidcClientProvider(
    OidcProviderOptions options, IBrowserLauncher browser, Func<int, ILoopbackListener> listenerFactory,
    HttpClient? httpClient = null) : IIdentityProvider
{
    private readonly HttpClient http = httpClient ?? new HttpClient { Timeout = options.HttpTimeout };

    public string ProviderId => "oidc";

    public async Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
    {
        (string authorizationEndpoint, string tokenEndpoint) = await DiscoverAsync(ct).ConfigureAwait(false);

        string state = GenerateState();
        (string verifier, string challenge) = Pkce.Create();

        using ILoopbackListener listener = listenerFactory(options.LoopbackPort);

        UriBuilder authorizeUri = new(authorizationEndpoint);
        System.Collections.Specialized.NameValueCollection authorizeQuery = HttpUtility.ParseQueryString(string.Empty);
        authorizeQuery["client_id"] = options.ClientId;
        authorizeQuery["redirect_uri"] = listener.RedirectUri.ToString();
        authorizeQuery["response_type"] = "code";
        authorizeQuery["scope"] = options.Scopes;
        authorizeQuery["state"] = state;
        authorizeQuery["code_challenge"] = challenge;
        authorizeQuery["code_challenge_method"] = "S256";
        authorizeUri.Query = authorizeQuery.ToString();

        bool launched = await browser.LaunchAsync(authorizeUri.Uri, ct).ConfigureAwait(false);
        if (!launched)
            throw new IdentitySignInException("failed to launch the system browser for sign-in");

        LoopbackResult redirect = await listener.WaitForRedirectAsync(ct).ConfigureAwait(false);

        if (!redirect.Query.TryGetValue("state", out string? returnedState) || returnedState != state)
            throw new IdentitySignInException("sign-in redirect state mismatch");

        if (!redirect.Query.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code))
            throw new IdentitySignInException("sign-in redirect did not include an authorization code");

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = listener.RedirectUri.ToString(),
            ["client_id"] = options.ClientId,
            ["code_verifier"] = verifier,
        };

        return await PostTokenAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
    }

    public async Task<ProviderCredential?> RefreshAsync(ProviderCredential expired, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expired.RefreshToken))
            return null;

        (_, string tokenEndpoint) = await DiscoverAsync(ct).ConfigureAwait(false);

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = expired.RefreshToken,
            ["client_id"] = options.ClientId,
        };

        return await PostTokenAsync(tokenEndpoint, form, ct).ConfigureAwait(false);
    }

    private async Task<(string authorizationEndpoint, string tokenEndpoint)> DiscoverAsync(CancellationToken ct)
    {
        string metadataUrl = options.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        using HttpResponseMessage response = await http.GetAsync(new Uri(metadataUrl), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IdentitySignInException($"failed to fetch OIDC discovery document ({(int)response.StatusCode})");

        using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("authorization_endpoint", out JsonElement authElement) ||
            !doc.RootElement.TryGetProperty("token_endpoint", out JsonElement tokenElement))
            throw new IdentitySignInException("OIDC discovery document is missing required endpoints");

        string? authorizationEndpoint = authElement.GetString();
        string? tokenEndpoint = tokenElement.GetString();
        if (string.IsNullOrEmpty(authorizationEndpoint) || string.IsNullOrEmpty(tokenEndpoint))
            throw new IdentitySignInException("OIDC discovery document has empty required endpoints");

        return (authorizationEndpoint, tokenEndpoint);
    }

    private async Task<ProviderCredential> PostTokenAsync(
        string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await http.PostAsync(new Uri(tokenEndpoint), content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IdentitySignInException($"token endpoint returned an error ({(int)response.StatusCode})");

        using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("id_token", out JsonElement idTokenElement))
            throw new IdentitySignInException("token endpoint response is missing id_token");

        string? idToken = idTokenElement.GetString();
        if (string.IsNullOrEmpty(idToken))
            throw new IdentitySignInException("token endpoint response has an empty id_token");

        string? refreshToken = doc.RootElement.TryGetProperty("refresh_token", out JsonElement refreshElement)
            ? refreshElement.GetString()
            : null;

        int expiresInSeconds = doc.RootElement.TryGetProperty("expires_in", out JsonElement expiresElement)
            ? expiresElement.GetInt32()
            : 0;

        DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);

        return new ProviderCredential(ProviderId, idToken, refreshToken, expiresAtUtc);
    }

    private static string GenerateState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
