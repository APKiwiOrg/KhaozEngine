using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace KhaozEngine.Identity.Discord;

/// <summary>Client-side Discord sign-in via the system browser + a loopback redirect: authorization-code flow
/// with PKCE (RFC 7636). Discord's authorize and token endpoints are fixed (no discovery document), unlike the
/// generic OIDC provider.
///
/// Ordering (see the loopback-race note on <see cref="ILoopbackListener"/>): the authorize URL (with
/// <c>state</c> + PKCE) is built first, then the loopback listener is created (so it is already listening),
/// THEN the browser is launched. Launching the browser before the listener exists risks a fast redirect racing a
/// not-yet-listening loopback.</summary>
public sealed class DiscordClientProvider(
    DiscordProviderOptions options, IBrowserLauncher browser, Func<int, ILoopbackListener> listenerFactory,
    HttpClient? httpClient = null) : IIdentityProvider
{
    private const string AuthorizeEndpoint = "https://discord.com/api/oauth2/authorize";
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";

    private readonly HttpClient http = httpClient ?? new HttpClient { Timeout = options.HttpTimeout };

    public string ProviderId => "discord";

    public async Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
    {
        string state = GenerateState();
        (string verifier, string challenge) = Pkce.Create();

        using ILoopbackListener listener = listenerFactory(options.LoopbackPort);

        UriBuilder authorizeUri = new(AuthorizeEndpoint);
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

        return (await PostTokenAsync(form, rejectDeadChain: false, ct).ConfigureAwait(false))!.Value;
    }

    public async Task<ProviderCredential?> RefreshAsync(ProviderCredential expired, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(expired.RefreshToken))
            return null;

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = expired.RefreshToken,
            ["client_id"] = options.ClientId,
        };

        return await PostTokenAsync(form, rejectDeadChain: true, ct).ConfigureAwait(false);
    }

    /// <summary>Posts the token form and parses the credential. With <paramref name="rejectDeadChain"/> set (the
    /// refresh grant), a 400 or 401 is a dead refresh chain and returns null. Every other non-success status, and
    /// any non-success during the interactive sign-in code exchange, throws <see cref="IdentitySignInException"/>.
    /// A success always yields a credential, so the non-null return is safe when <paramref name="rejectDeadChain"/>
    /// is false.</summary>
    private async Task<ProviderCredential?> PostTokenAsync(
        Dictionary<string, string> form, bool rejectDeadChain, CancellationToken ct)
    {
        using FormUrlEncodedContent content = new(form);
        using HttpResponseMessage response = await http.PostAsync(new Uri(TokenEndpoint), content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (rejectDeadChain && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return null;
            throw new IdentitySignInException($"token endpoint returned an error ({(int)response.StatusCode})");
        }

        using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("access_token", out JsonElement accessTokenElement))
            throw new IdentitySignInException("token endpoint response is missing access_token");

        string? accessToken = accessTokenElement.GetString();
        if (string.IsNullOrEmpty(accessToken))
            throw new IdentitySignInException("token endpoint response has an empty access_token");

        string? refreshToken = doc.RootElement.TryGetProperty("refresh_token", out JsonElement refreshElement)
            ? refreshElement.GetString()
            : null;

        DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(ReadExpiresInSeconds(doc.RootElement));

        return new ProviderCredential(ProviderId, accessToken, refreshToken, expiresAtUtc);
    }

    /// <summary>Reads <c>expires_in</c> without letting a hostile or merely non-conforming value escape the
    /// <see cref="IdentitySignInException"/> contract the rest of this class follows. An absent field is 0 (no
    /// declared lifetime, as before). A value that is not a JSON number, or a number outside <see cref="int"/>
    /// range, is a sign-in failure rather than the raw <see cref="InvalidOperationException"/> /
    /// <see cref="FormatException"/> a bare <c>GetInt32()</c> throws straight out of sign-in.</summary>
    private static int ReadExpiresInSeconds(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out JsonElement expiresElement))
            return 0;

        if (expiresElement.ValueKind != JsonValueKind.Number || !expiresElement.TryGetInt32(out int seconds))
            throw new IdentitySignInException("token endpoint response has a malformed expires_in");

        return seconds;
    }

    private static string GenerateState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
