using System;
using System.Net;
using System.Net.Http;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class GitHubResponseClassifierTests
{
    [Theory]
    [InlineData(403, "invalid", "0", "invalid", 60)]
    [InlineData(403, "18446744073709551616", "0", "9223372036854775808", 60)]
    [InlineData(403, "-1", "0", "-9223372036854775809", 60)]
    [InlineData(403, "NaN", "0", "253402300800", 60)]
    [InlineData(403, "Infinity", "0", "-62135596801", 60)]
    [InlineData(403, "Mon, 99 Oct 2026 99:99:99 GMT", "0", "1e309", 60)]
    [InlineData(429, "invalid", "0", "invalid", 60)]
    [InlineData(429, "18446744073709551616", "0", "9223372036854775808", 60)]
    [InlineData(429, "invalid", null, "253402300800", 60)]
    [InlineData(429, "18446744073709551616", "1", "invalid", 60)]
    [InlineData(403, "invalid", null, "9223372036854775808", null)]
    [InlineData(403, "18446744073709551616", "1", "invalid", null)]
    public void Malformed_or_overflowing_headers_preserve_rate_limit_fallbacks(int status, string retryAfter,
        string? remaining, string reset, int? expectedSeconds)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
        if (remaining is not null) response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);

        GitHubApiException error = Assert.IsType<GitHubApiException>(GitHubResponseClassifier.Classify(response,
            new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expectedSeconds.HasValue, error.IsRateLimited);
        Assert.Equal(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null, error.RetryAfter);
    }
}
