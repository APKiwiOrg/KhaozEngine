using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

        Assert.Equal(GitHubApiRefusal.HttpFailure, error.Reason);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(expectedSeconds.HasValue, error.IsRateLimited);
        Assert.Equal(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null, error.RetryAfter);
    }

    [Theory]
    [InlineData("Bearer abc", 0, null)]
    [InlineData("bearer abc", 0, null)]
    [InlineData("authorization: abc", 0, null)]
    [InlineData("ghs_abc", 0, null)]
    [InlineData("-----BEGIN PRIVATE KEY-----", 0, null)]
    [InlineData("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.abc", 0, null)]
    [InlineData("plain-configured-value", 0, "plain-configured-value")]
    [InlineData("ghs_abc", 250, null)]
    public void A_detail_holding_a_credential_is_dropped_whole(string credential, int padding, string? secret)
    {
        string[] secrets = secret is null ? Array.Empty<string>() : new[] { secret };
        string filler = new('x', padding);

        GitHubApiException refused = ClassifyMessage(filler + credential, secrets);

        Assert.Equal(GitHubApiRefusal.HttpFailure, refused.Reason);
        Assert.Null(refused.UpstreamDetail);
        Assert.DoesNotContain(credential, refused.Message);
        Assert.DoesNotContain("xxx", refused.Message);

        // Control: the same body without the credential keeps its detail, bounded to 200 characters.
        GitHubApiException control = ClassifyMessage(filler + "clean", secrets);
        Assert.Equal(padding == 0 ? "clean" : new string('x', 200), control.UpstreamDetail);
    }

    [Fact]
    public void A_failure_built_outside_the_engine_carries_no_reason()
    {
        var failure = new GitHubApiException(503, true, TimeSpan.FromSeconds(5), "Consumer client failure");

        Assert.Null(failure.Reason);
        Assert.Null(failure.UpstreamDetail);
        Assert.Null(failure.ResponsePart);
        Assert.Equal(503, failure.StatusCode);
        Assert.True(failure.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(5), failure.RetryAfter);
    }

    static GitHubApiException ClassifyMessage(string message, string[] secrets)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { message })),
        };
        return Assert.IsType<GitHubApiException>(GitHubResponseClassifier.Classify(response,
            new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), secrets));
    }
}
