using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Http;

namespace KhaozEngine.BugReports.Relay;

internal sealed class InstallationTokenCache : IDisposable
{
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    readonly HttpClient http;
    readonly GitHubAppCredentials credentials;
    readonly RSA key;
    readonly TimeProvider time;
    readonly HttpRetryPolicy policy;
    readonly SemaphoreSlim exchange = new(1, 1);
    readonly object gate = new();
    string? token;
    DateTimeOffset expiresAt;
    long generation;

    public InstallationTokenCache(HttpClient http, GitHubAppCredentials credentials, RSA key, TimeProvider time, HttpRetryPolicy policy)
    {
        this.http = http;
        this.credentials = credentials;
        this.key = key;
        this.time = time;
        this.policy = policy;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        await exchange.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long startedGeneration;
            lock (gate)
            {
                if (token is not null && expiresAt - time.GetUtcNow() > RefreshMargin)
                    return token;
                startedGeneration = generation;
            }

            string jwt = GitHubAppJwt.Create(credentials.AppId, key, time.GetUtcNow());
            string path = "app/installations/" + credentials.InstallationId.ToString(CultureInfo.InvariantCulture) + "/access_tokens";
            using HttpResponseMessage response = await HttpRetry.SendAsync(http,
                () => GitHubAppIssueClient.CreateRequest(HttpMethod.Post, path, jwt), policy, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                Invalidate();
            var error = GitHubResponseClassifier.Classify(response, time.GetUtcNow(), jwt, credentials.PrivateKeyPem);
            if (error is not null)
                throw error;

            string receivedToken;
            DateTimeOffset receivedExpiry;
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("expires_at", out var expiryValue) || expiryValue.ValueKind != JsonValueKind.String
                    || !expiryValue.TryGetDateTimeOffset(out receivedExpiry))
                    throw GitHubResponseClassifier.InvalidResponse(response);
                receivedToken = tokenValue.GetString()!;
                if (!IsBearerToken(receivedToken))
                    throw GitHubResponseClassifier.InvalidResponse(response);
            }
            catch (JsonException)
            {
                throw GitHubResponseClassifier.InvalidResponse(response);
            }

            lock (gate)
            {
                // An overlapping 401 must not be undone by this exchange completing later.
                if (generation == startedGeneration)
                {
                    token = receivedToken;
                    expiresAt = receivedExpiry;
                }
            }
            return receivedToken;
        }
        finally
        {
            exchange.Release();
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            token = null;
            generation++;
        }
    }

    static bool IsBearerToken(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (char c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~' or '+' or '/' or '='))
                return false;
        return true;
    }

    public void Dispose() => exchange.Dispose();
}
