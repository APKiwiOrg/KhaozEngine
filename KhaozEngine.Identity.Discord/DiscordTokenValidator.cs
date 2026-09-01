using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity.Discord;

/// <summary>Validates a Discord OAuth2 access token via the token-introspection endpoint
/// (<c>GET https://discord.com/api/oauth2/@me</c>) and confirms the token was minted for this consumer's
/// own Discord application before trusting the identity it names.
///
/// The plain <c>users/@me</c> endpoint answers for ANY application's access token and never names the
/// issuing app, so validating against it accepts a token minted for a <em>different</em> Discord app as
/// though it were this consumer's own. When a server exchanges the resulting identity for a session token
/// that is an account-takeover vector: an attacker who owns any "Login with Discord" app a victim ever
/// authorized holds a victim access token that identifies the victim. <c>oauth2/@me</c> returns the issuing
/// <c>application.id</c>, which is compared against the expected client id passed to the constructor; a
/// token from any other application is rejected (returns null).</summary>
public sealed class DiscordTokenValidator : IIdentityValidator
{
    private const string TokenInfoEndpoint = "https://discord.com/api/oauth2/@me";

    private readonly string expectedClientId;
    private readonly HttpClient http;

    /// <summary>ID of the provider this validator verifies credentials for.</summary>
    public string ProviderId => "discord";

    /// <param name="expectedClientId">This consumer's own Discord application (client) id. A token whose
    /// issuing <c>application.id</c> does not equal this is rejected. Required: there is no unchecked mode,
    /// so a validator cannot be constructed that accepts tokens minted for another app.</param>
    /// <param name="httpClient">HTTP client to use; a fresh instance is created when null.</param>
    /// <exception cref="ArgumentException"><paramref name="expectedClientId"/> is null or empty.</exception>
    public DiscordTokenValidator(string expectedClientId, HttpClient? httpClient = null)
    {
        if (string.IsNullOrEmpty(expectedClientId))
            throw new ArgumentException("an expected Discord client id is required", nameof(expectedClientId));
        this.expectedClientId = expectedClientId;
        http = httpClient ?? new HttpClient();
    }

    /// <summary>Verifies the token against <c>oauth2/@me</c> and maps the nested user object to a
    /// <see cref="VerifiedIdentity"/>. Returns null for a non-success response, a token from a different
    /// application, or a malformed/unparseable body (fail-closed). A request that never completes still
    /// throws, as it always has: <see cref="ValidateDetailedAsync"/> is where that becomes an outcome.</summary>
    public async Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await SendAsync(credentialToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await ReadIdentityAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Same verification, with the outage case split out. Discord answering 500, or rate-limiting with 429, is
    /// a completed round trip that says nothing about the token, and a request that never completed says even
    /// less. Reporting either as a refusal sends the player back through sign-in against a provider that is
    /// already down, so those map to <see cref="IdentityValidationOutcome.ProviderUnavailable"/> and every
    /// other non-success maps to Refused. Cancellation the caller asked for still surfaces as an exception.
    /// </summary>
    public async Task<IdentityValidation> ValidateDetailedAsync(string credentialToken, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(credentialToken, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return IdentityValidation.ProviderUnavailable($"discord request failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation. Discord did not answer in time.
            return IdentityValidation.ProviderUnavailable("discord request timed out");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string detail = $"discord returned {(int)resp.StatusCode}";
                return IsTransient(resp.StatusCode)
                    ? IdentityValidation.ProviderUnavailable(detail)
                    : IdentityValidation.Refused(detail);
            }

            VerifiedIdentity? identity = await ReadIdentityAsync(resp, ct).ConfigureAwait(false);
            return identity is { } verified
                ? IdentityValidation.Verified(verified)
                : IdentityValidation.Refused("discord answered, but the token is not this application's");
        }
    }

    /// <summary>
    /// A status the provider is expected to recover from on its own: any 5xx, a 429 rate limit, or a 408. None
    /// of them is a statement about the credential.
    /// </summary>
    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Issues the introspection call. Awaited rather than returned, so the request message outlives the send
    /// instead of being disposed under it.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string credentialToken, CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, TokenInfoEndpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialToken);
        return await http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a success response to an identity, or null when the body is not this application's or cannot be
    /// read. Every null here is a refusal: the round trip completed and the answer was unusable.
    /// </summary>
    private async Task<VerifiedIdentity?> ReadIdentityAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            // Audience check: the token must have been issued for THIS application, or it is not ours to trust.
            if (!root.TryGetProperty("application", out JsonElement appEl)
                || appEl.ValueKind != JsonValueKind.Object
                || !appEl.TryGetProperty("id", out JsonElement appIdEl)
                || appIdEl.ValueKind != JsonValueKind.String)
                return null;
            if (!string.Equals(appIdEl.GetString(), expectedClientId, StringComparison.Ordinal))
                return null;

            // Identity comes from the nested user object (present only when the token carries the identify scope).
            if (!root.TryGetProperty("user", out JsonElement userEl) || userEl.ValueKind != JsonValueKind.Object)
                return null;
            if (!userEl.TryGetProperty("id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.String)
                return null;
            string sub = idEl.GetString() ?? "";
            if (sub.Length == 0) return null;

            string? name = userEl.TryGetProperty("username", out JsonElement u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            Dictionary<string, string> claims = new(StringComparer.Ordinal);
            if (userEl.TryGetProperty("email", out JsonElement em) && em.ValueKind == JsonValueKind.String)
                claims["email"] = em.GetString()!;
            return new VerifiedIdentity(sub, "discord", name, claims);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
