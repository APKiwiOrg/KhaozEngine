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

    /// <summary>Runs the interactive flow under <see cref="DiscordProviderOptions.SignInTimeout"/>. The wait on
    /// the loopback redirect is open-ended by nature (it ends when the player finishes in the browser, or
    /// never), so without a deadline an abandoned flow leaves this task pending for the life of the process and
    /// the bound loopback port with it. On expiry the core method unwinds, its <c>using</c> disposes the
    /// listener and frees the port, and the caller sees the same <see cref="IdentitySignInException"/> as any
    /// other sign-in failure, with <see cref="SignInRefusal.TimedOut"/> and the expired timeout. The caller's own
    /// cancellation still surfaces as <see cref="OperationCanceledException"/>.</summary>
    public async Task<ProviderCredential> SignInAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.SignInTimeout > TimeSpan.Zero)
            deadline.CancelAfter(options.SignInTimeout);

        try
        {
            return await SignInCoreAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IdentitySignInException(
                SignInRefusal.TimedOut, $"sign-in timed out after {options.SignInTimeout}")
            {
                ProviderId = ProviderId,
                Timeout = options.SignInTimeout,
            };
        }
    }

    private async Task<ProviderCredential> SignInCoreAsync(CancellationToken ct)
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
            throw new IdentitySignInException(
                SignInRefusal.BrowserLaunchFailed, "failed to launch the system browser for sign-in")
            {
                ProviderId = ProviderId,
            };

        LoopbackResult redirect = await listener.WaitForRedirectAsync(ct).ConfigureAwait(false);

        if (!redirect.Query.TryGetValue("state", out string? returnedState) || returnedState != state)
            throw new IdentitySignInException(SignInRefusal.RedirectStateMismatch, "sign-in redirect state mismatch")
            {
                ProviderId = ProviderId,
            };

        if (!redirect.Query.TryGetValue("code", out string? code) || string.IsNullOrEmpty(code))
            throw new IdentitySignInException(
                SignInRefusal.AuthorizationCodeMissing, "sign-in redirect did not include an authorization code")
            {
                ProviderId = ProviderId,
            };

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
            throw new IdentitySignInException(
                SignInRefusal.TokenHttpFailure, $"token endpoint returned an error ({(int)response.StatusCode})")
            {
                ProviderId = ProviderId,
                StatusCode = (int)response.StatusCode,
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };
        }

        using JsonDocument doc = await ParseTokenJsonBodyAsync(response, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("access_token", out JsonElement accessTokenElement))
            throw new IdentitySignInException(
                SignInRefusal.TokenMemberMissing, "token endpoint response is missing access_token")
            {
                ProviderId = ProviderId,
                MemberName = "access_token",
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };

        string? accessToken = accessTokenElement.ValueKind == JsonValueKind.String ? accessTokenElement.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new IdentitySignInException(
                SignInRefusal.TokenMemberEmpty, "token endpoint response has an empty access_token")
            {
                ProviderId = ProviderId,
                MemberName = "access_token",
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };

        string? refreshToken = null;
        if (doc.RootElement.TryGetProperty("refresh_token", out JsonElement refreshElement))
        {
            if (refreshElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new IdentitySignInException(
                    SignInRefusal.TokenMemberEmpty, "token endpoint response has an unusable refresh_token")
                {
                    ProviderId = ProviderId,
                    MemberName = "refresh_token",
                    EndpointRole = SignInEndpointRole.TokenEndpoint,
                };

            refreshToken = refreshElement.ValueKind == JsonValueKind.String ? refreshElement.GetString() : null;
        }

        DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(ReadExpiresInSeconds(doc.RootElement));

        return new ProviderCredential(ProviderId, accessToken, refreshToken, expiresAtUtc);
    }

    /// <summary>Reads a successful response body as JSON. A 200 whose body is not JSON at all (an HTML
    /// captive-portal interstitial on hotel or airport wifi, a misconfigured reverse proxy sitting in front of
    /// the token endpoint) becomes this class's own <see cref="IdentitySignInException"/>, instead of a raw
    /// <see cref="JsonException"/> escaping sign-in past every caller catching that type. The token endpoint is
    /// the only endpoint this provider reads. The parser's exception stays the inner exception: it names the
    /// offending character, or the few bytes of a malformed literal, and its position, never a string value from
    /// the body. A valid JSON value that is not an object refuses with the same reason and no inner exception,
    /// before any member or expiry read.</summary>
    private async Task<JsonDocument> ParseTokenJsonBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc;

            doc.Dispose();
            throw new IdentitySignInException(
                SignInRefusal.EndpointJsonMalformed, "the token endpoint did not return a JSON object")
            {
                ProviderId = ProviderId,
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };
        }
        catch (JsonException ex)
        {
            throw new IdentitySignInException(
                SignInRefusal.EndpointJsonMalformed, "the token endpoint did not return a JSON body", ex)
            {
                ProviderId = ProviderId,
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };
        }
    }

    /// <summary>Reads <c>expires_in</c> without letting a hostile or merely non-conforming value escape the
    /// <see cref="IdentitySignInException"/> contract the rest of this class follows. An absent field is 0 (no
    /// declared lifetime, as before). A value that is not a JSON number, or a number outside <see cref="int"/>
    /// range, is a sign-in failure rather than the raw <see cref="InvalidOperationException"/> /
    /// <see cref="FormatException"/> a bare <c>GetInt32()</c> throws straight out of sign-in.</summary>
    private int ReadExpiresInSeconds(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out JsonElement expiresElement))
            return 0;

        if (expiresElement.ValueKind != JsonValueKind.Number || !expiresElement.TryGetInt32(out int seconds))
            throw new IdentitySignInException(
                SignInRefusal.TokenExpiryMalformed, "token endpoint response has a malformed expires_in")
            {
                ProviderId = ProviderId,
                MemberName = "expires_in",
                EndpointRole = SignInEndpointRole.TokenEndpoint,
            };

        return seconds;
    }

    private static string GenerateState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
