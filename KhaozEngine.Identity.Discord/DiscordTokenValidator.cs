using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Identity.Discord;

/// <summary>Validates a Discord OAuth2 access token by calling the Discord userinfo endpoint.</summary>
public sealed class DiscordTokenValidator : IIdentityValidator
{
    private readonly HttpClient http;
    public string ProviderId => "discord";
    public DiscordTokenValidator(HttpClient? httpClient = null) => http = httpClient ?? new HttpClient();

    public async Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "https://discord.com/api/users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentialToken);
        using HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("id", out JsonElement idEl)) return null;
        string sub = idEl.GetString() ?? "";
        if (sub.Length == 0) return null;
        string? name = root.TryGetProperty("username", out JsonElement u) ? u.GetString() : null;
        Dictionary<string, string> claims = new(StringComparer.Ordinal);
        if (root.TryGetProperty("email", out JsonElement em) && em.ValueKind == JsonValueKind.String) claims["email"] = em.GetString()!;
        return new VerifiedIdentity(sub, "discord", name, claims);
    }
}
