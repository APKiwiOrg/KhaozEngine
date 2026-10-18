using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KhaozEngine.BugReports.Relay;

internal static class GitHubResponseClassifier
{
    public static GitHubApiException? Classify(HttpResponseMessage response, DateTimeOffset now) =>
        Classify(response, now, Array.Empty<string>());

    internal static GitHubApiException? Classify(HttpResponseMessage response, DateTimeOffset now, params string[] secrets)
    {
        if (response.IsSuccessStatusCode)
            return null;

        TimeSpan? delay = null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (RetryConditionHeaderValue.TryParse(Header(response, "Retry-After"), out var retry))
            {
                delay = retry.Delta ?? retry.Date - now;
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;
            }
            else if (Header(response, "x-ratelimit-remaining") == "0")
            {
                delay = TimeSpan.FromSeconds(60);
                if (long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long reset)
                    && reset is >= -62135596800 and <= 253402300799)
                    delay = DateTimeOffset.FromUnixTimeSeconds(reset) - now;
                if (delay < TimeSpan.FromSeconds(1))
                    delay = TimeSpan.FromSeconds(1);
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                delay = TimeSpan.FromSeconds(60);

        }

        string message = ReadMessage(response);
        // Drop an echoed credential before truncating, including one beyond the display limit.
        if (message.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ghs_", StringComparison.Ordinal)
            || message.Contains("PRIVATE KEY", StringComparison.Ordinal)
            || message.Contains("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.", StringComparison.Ordinal)
            || secrets.Any(secret => secret.Length != 0 && message.Contains(secret, StringComparison.Ordinal)))
            message = "";
        if (message.Length > 200)
            message = message[..200];

        string status = $"GitHub API returned HTTP {(int)response.StatusCode}";
        return new GitHubApiException((int)response.StatusCode, delay.HasValue, delay,
            message.Length == 0 ? status : status + ": " + message);
    }

    internal static GitHubApiException InvalidResponse(HttpResponseMessage response) =>
        new((int)response.StatusCode, false, null, $"GitHub API returned HTTP {(int)response.StatusCode} with an invalid response.");

    static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    static string ReadMessage(HttpResponseMessage response)
    {
        try
        {
            // Both send paths buffer content before calling this synchronous classifier.
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String ? message.GetString()! : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
