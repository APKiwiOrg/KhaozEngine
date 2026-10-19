using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Http;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class GitHubAppIssueClientTests : IDisposable
{
    const string Token = "ghs_installation_secret";
    static readonly DateTimeOffset Start = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    static readonly GitHubIssueDraft Draft = new("A title", "A body", new[] { "kind/bug", "game/test" });
    readonly RSA rsa = RSA.Create(2048);
    readonly ScriptedHttpHandler handler = new();
    readonly ManualClock clock = new(Start);
    readonly HttpClient http;

    public GitHubAppIssueClientTests() => http = new HttpClient(handler);

    GitHubAppCredentials Credentials => new(123456, 789, rsa.ExportPkcs8PrivateKeyPem());

    GitHubAppIssueClient Client() => new(http, Credentials, "example", "reports", clock,
        new HttpRetryPolicy { Backoff = _ => TimeSpan.Zero });

    void TokenResponse(string token = Token, string expires = "2026-10-17T01:00:00Z") =>
        handler.Enqueue(HttpStatusCode.Created, JsonSerializer.Serialize(new
        {
            token,
            expires_at = expires,
            permissions = new { issues = "write" },
            repository_selection = "selected",
        }));

    void IssueResponse() => handler.Enqueue(HttpStatusCode.Created, "{\"number\":42,\"id\":123,\"state\":\"open\"}");

    [Fact]
    public async Task UpdateIssue_SendsPatchWithTitleBodyAndNotPlannedClose()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.OK, "{\"html_url\":\"https://github.com/o/r/issues/12\"}");
        using var client = new GitHubAppIssueClient(http, Credentials, "o", "r", clock);
        var update = new GitHubIssueUpdate("Revoked bug report ABC123",
            "This report was revoked at the reporter's request. Its files and private record are being deleted.");

        var result = await client.UpdateIssueAsync(12, update, CancellationToken.None);

        Assert.Equal(GitHubIssueUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(new Uri("https://github.com/o/r/issues/12"), result.IssueUrl);
        Assert.Equal(2, handler.Requests.Length);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal(new Uri("https://api.github.com/repos/o/r/issues/12"), request.Uri);
        Assert.Equal("Bearer " + Token, request.Authorization);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Equal("KhaozEngine-BugReports", request.Headers["User-Agent"]);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        Assert.Equal(update.Title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal(update.Body, json.RootElement.GetProperty("body").GetString());
        Assert.Equal("closed", json.RootElement.GetProperty("state").GetString());
        Assert.Equal("not_planned", json.RootElement.GetProperty("state_reason").GetString());
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task UpdateIssue_404Or410_AnswersGone(int status)
    {
        TokenResponse();
        handler.Enqueue((HttpStatusCode)status, "not json");
        using var client = Client();

        var result = await client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None);

        Assert.Equal(GitHubIssueUpdateOutcome.Gone, result.Outcome);
        Assert.Null(result.IssueUrl);
        Assert.Equal(2, handler.Requests.Length);
    }

    [Fact]
    public async Task UpdateIssue_301_AnswersMovedToTheNewIssuePage()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.MovedPermanently, "not json", ("Location", "https://api.github.com/repos/o/other/issues/3"));
        using var client = Client();

        var result = await client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None);

        Assert.Equal(GitHubIssueUpdateOutcome.Moved, result.Outcome);
        Assert.Equal(new Uri("https://github.com/o/other/issues/3"), result.IssueUrl);
        Assert.Equal(2, handler.Requests.Length);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(500)]
    public async Task UpdateIssue_RedirectedFinalRequest_AnswersMoved(int status)
    {
        TokenResponse();
        using var finalRequest = new HttpRequestMessage(HttpMethod.Patch, "https://api.github.com/repos/o/other/issues/3");
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            RequestMessage = finalRequest,
            Content = new StringContent("not json"),
        }));
        using var client = Client();

        var result = await client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None);

        Assert.Equal(GitHubIssueUpdateOutcome.Moved, result.Outcome);
        Assert.Equal(new Uri("https://github.com/o/other/issues/3"), result.IssueUrl);
        Assert.Equal(2, handler.Requests.Length);
    }

    [Fact]
    public async Task UpdateIssue_500_ThrowsGitHubApiException()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.InternalServerError, "{\"message\":\"Upstream failed\"}");
        using var client = Client();

        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None));

        Assert.Equal(500, error.StatusCode);
        Assert.False(error.IsRateLimited);
        Assert.Null(error.RetryAfter);
        Assert.Contains("Upstream failed", error.Message);
        Assert.Equal(2, handler.Requests.Length);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"html_url\":null}")]
    [InlineData("{\"html_url\":12}")]
    [InlineData("{\"html_url\":\"not a url\"}")]
    [InlineData("[]")]
    [InlineData("not json ghs_installation_secret PRIVATE KEY")]
    public async Task UpdateIssue_UnreadableIssueUrl_ThrowsSafeApiException(string json)
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.OK, json);
        using var client = Client();

        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None));

        Assert.Equal(200, error.StatusCode);
        Assert.Contains("invalid response", error.Message);
        AssertNoSecrets(error);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("/repos/o/other/issues/3")]
    [InlineData("https://api.github.com/not-an-issue")]
    public async Task UpdateIssue_UnreadableLocation_ThrowsSafeApiException(string? location)
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.MovedPermanently, "not json ghs_installation_secret PRIVATE KEY",
            location is null ? Array.Empty<(string, string)>() : new[] { ("Location", location) });
        using var client = Client();

        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None));

        Assert.Equal(301, error.StatusCode);
        Assert.Contains("invalid response", error.Message);
        AssertNoSecrets(error);
    }

    [Fact]
    public async Task IssueClientWithoutUpdate_ThrowsNotSupportedNamingTheMember()
    {
        IGitHubIssueClient client = new IssueClientWithoutUpdate();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None));

        Assert.Contains("IGitHubIssueClient.UpdateIssueAsync", error.Message);
    }

    sealed class IssueClientWithoutUpdate : IGitHubIssueClient
    {
        public Task<int> CreateIssueAsync(GitHubIssueDraft issue, CancellationToken cancellationToken) => Task.FromResult(1);
        public Task AddCommentAsync(int issueNumber, string body, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task CreateIssue_ExchangesTokenThenPostsIssue_ReturnsNumber()
    {
        TokenResponse();
        IssueResponse();
        using var client = Client();

        Assert.Equal(42, await client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(new[] { "https://api.github.com/app/installations/789/access_tokens", "https://api.github.com/repos/example/reports/issues" },
            handler.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
    }

    [Fact]
    public async Task CreateIssue_SendsTitleBodyLabelsAndHeaders()
    {
        TokenResponse();
        IssueResponse();
        using var client = Client();
        await client.CreateIssueAsync(Draft, CancellationToken.None);

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
            Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
            Assert.Equal("KhaozEngine-BugReports", request.Headers["User-Agent"]);
        });
        Assert.StartsWith("Bearer eyJ", handler.Requests[0].Authorization);
        Assert.Equal("Bearer " + Token, handler.Requests[1].Authorization);
        using var json = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("A title", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("A body", json.RootElement.GetProperty("body").GetString());
        Assert.Equal(new[] { "kind/bug", "game/test" }, json.RootElement.GetProperty("labels").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Token_ReusedUntilFiveMinutesBeforeExpiry()
    {
        TokenResponse();
        IssueResponse();
        IssueResponse();
        TokenResponse("ghs_refreshed");
        IssueResponse();
        using var client = Client();

        await client.CreateIssueAsync(Draft, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(54));
        await client.CreateIssueAsync(Draft, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Length);
        clock.Advance(TimeSpan.FromMinutes(2));
        await client.CreateIssueAsync(Draft, CancellationToken.None);

        Assert.Equal(5, handler.Requests.Length);
        Assert.Equal("/app/installations/789/access_tokens", handler.Requests[3].Uri.AbsolutePath);
        Assert.Equal("Bearer ghs_refreshed", handler.Requests[4].Authorization);
    }

    [Fact]
    public async Task Token_RefreshesAtExactlyFiveMinuteMargin()
    {
        TokenResponse();
        IssueResponse();
        TokenResponse("ghs_refreshed");
        IssueResponse();
        using var client = Client();
        await client.CreateIssueAsync(Draft, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(55));
        await client.CreateIssueAsync(Draft, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Token_ConcurrentCallersShareOneExchange()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Enqueue(async (_, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"token\":\"ghs_installation_secret\",\"expires_at\":\"2026-10-17T01:00:00Z\"}"),
            };
        });
        IssueResponse();
        IssueResponse();
        using var client = Client();
        Task<int> first = client.CreateIssueAsync(Draft, CancellationToken.None);
        await entered.Task;
        Task<int> second = client.CreateIssueAsync(Draft, CancellationToken.None);
        release.SetResult();

        Assert.Equal(new[] { 42, 42 }, await Task.WhenAll(first, second));
        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task CreateIssue_403WithRetryAfter_ThrowsRateLimited(int status)
    {
        TokenResponse();
        handler.Enqueue((HttpStatusCode)status, "{}", ("Retry-After", "30"));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.True(error.IsRateLimited);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
    }

    [Theory]
    [InlineData(403, "1792195245", 45)]
    [InlineData(429, "1792195190", 1)]
    public async Task CreateIssue_403RemainingZero_ThrowsRateLimitedUntilReset(int status, string reset, int seconds)
    {
        TokenResponse();
        handler.Enqueue((HttpStatusCode)status, "{}", ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.True(error.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(seconds), error.RetryAfter);
    }

    [Fact]
    public async Task CreateIssue_429Alone_ThrowsRateLimitedSixtySeconds()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.TooManyRequests);
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.True(error.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
    }

    [Fact]
    public async Task CreateIssue_RetryAfterZero_PreservesRequestedDelay()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Forbidden, "{}", ("Retry-After", "0"));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.True(error.IsRateLimited);
        Assert.Equal(TimeSpan.Zero, error.RetryAfter);
    }

    [Fact]
    public async Task CreateIssue_RetryAfterDate_TakesPrecedenceOverReset()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Forbidden, "{}", ("Retry-After", "Sat, 17 Oct 2026 00:00:30 GMT"),
            ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1792195245"));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.True(error.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
    }

    [Fact]
    public async Task CreateIssue_422_ThrowsNotRateLimitedWithGitHubMessage()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Validation failed\",\"errors\":[{\"code\":\"invalid\"}]}");
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.False(error.IsRateLimited);
        Assert.Null(error.RetryAfter);
        Assert.Contains("422", error.Message);
        Assert.Contains("Validation failed", error.Message);
        Assert.DoesNotContain("invalid", error.Message);
    }

    [Fact]
    public async Task CreateIssue_LongGitHubMessage_IsBounded()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { message = new string('x', 250) }));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(200, error.Message.Count(c => c == 'x'));
    }

    [Fact]
    public async Task CreateIssue_401_InvalidatesTokenAndThrows()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        TokenResponse("ghs_new_token");
        IssueResponse();
        using var client = Client();

        await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Length);
        Assert.Equal(42, await client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal("Bearer ghs_new_token", handler.Requests[3].Authorization);
    }

    [Fact]
    public async Task CreateIssue_TransportFault_SentOnceAndPropagates()
    {
        TokenResponse();
        handler.Enqueue((_, _) => throw new HttpRequestException("Network unavailable"));
        using var client = Client();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_502_IsSentExactlyOnce(bool comment)
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.BadGateway, "{\"message\":\"Upstream failed\"}");
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => comment
            ? client.AddCommentAsync(42, "A comment", CancellationToken.None)
            : client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.False(error.IsRateLimited);
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(2, handler.Requests.Length);
        Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_ExchangeRetriesTransientStatus()
    {
        handler.Enqueue(HttpStatusCode.ServiceUnavailable);
        TokenResponse();
        IssueResponse();
        using var client = Client();
        Assert.Equal(42, await client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Length);
        Assert.Equal(handler.Requests[0].Uri, handler.Requests[1].Uri);
    }

    [Fact]
    public async Task AddComment_PostsToCommentsRoute()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Created);
        using var client = Client();
        await client.AddCommentAsync(42, "A comment\nwith a quote \"", CancellationToken.None);
        Assert.Equal("https://api.github.com/repos/example/reports/issues/42/comments", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("Bearer " + Token, handler.Requests[1].Authorization);
        using var json = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("A comment\nwith a quote \"", json.RootElement.GetProperty("body").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task AddComment_401_InvalidatesToken()
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        TokenResponse("ghs_new_token");
        handler.Enqueue(HttpStatusCode.Created);
        using var client = Client();
        await Assert.ThrowsAsync<GitHubApiException>(() => client.AddCommentAsync(42, "A comment", CancellationToken.None));
        await client.AddCommentAsync(42, "A comment", CancellationToken.None);
        Assert.Equal(4, handler.Requests.Length);
        Assert.Equal("Bearer ghs_new_token", handler.Requests[3].Authorization);
    }

    [Fact]
    public void Constructor_NonRsaPem_Throws()
    {
        using var ec = ECDsa.Create();
        string pem = ec.ExportPkcs8PrivateKeyPem();
        var error = Assert.Throws<ArgumentException>(() => new GitHubAppIssueClient(http,
            new GitHubAppCredentials(123456, 789, pem), "example", "reports", clock));
        Assert.DoesNotContain(pem, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(422, true)]
    [InlineData(429, true)]
    [InlineData(502, true)]
    public async Task Exceptions_NeverContainTokenOrJwt(int status, bool update = false)
    {
        TokenResponse();
        handler.Enqueue((request, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                message = request.Authorization + " " + handler.Requests[0].Authorization + " " + Credentials.PrivateKeyPem,
            })),
        }));
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => update
            ? client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None)
            : client.CreateIssueAsync(Draft, CancellationToken.None));
        AssertNoSecrets(error);
    }

    [Fact]
    public async Task TokenExchange_ErrorEchoingJwtAndKey_IsRedacted()
    {
        handler.Enqueue((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { message = request.Authorization + " " + Credentials.PrivateKeyPem })),
        }));
        using var client = Client();
        AssertNoSecrets(await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TransportExceptions_CannotLeakRequestSecrets(bool tokenExchange, bool update = false)
    {
        if (!tokenExchange) TokenResponse();
        for (int attempt = 0; attempt < (tokenExchange ? 3 : 1); attempt++)
            handler.Enqueue((request, _) => throw new HttpRequestException(request.Authorization,
                new Exception(Credentials.PrivateKeyPem)));
        using var client = Client();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => update
            ? client.UpdateIssueAsync(12, new GitHubIssueUpdate("Title", "Body"), CancellationToken.None)
            : client.CreateIssueAsync(Draft, CancellationToken.None));
        AssertNoSecrets(error);
        Assert.Null(error.InnerException);
        Assert.Equal(tokenExchange ? 3 : 2, handler.Requests.Length);
    }

    void AssertNoSecrets(Exception error)
    {
        string text = error.ToString();
        Assert.DoesNotContain("ghs_", text);
        Assert.DoesNotContain("Bearer", text);
        Assert.DoesNotContain("PRIVATE KEY", text);
        foreach (RecordedRequest request in handler.Requests)
            Assert.DoesNotContain(request.Authorization["Bearer ".Length..], text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"token\":\"ghs_bad\"}")]
    [InlineData("{\"token\":42,\"expires_at\":\"2026-10-17T01:00:00Z\"}")]
    [InlineData("{\"token\":\"\",\"expires_at\":\"2026-10-17T01:00:00Z\"}")]
    [InlineData("{\"token\":\"ghs_bad\",\"expires_at\":\"bad\"}")]
    [InlineData("{\"token\":\"ghs_bad\",\"expires_at\":null}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Token_MissingOrMalformedFields_ThrowSafeApiException(string json)
    {
        handler.Enqueue(HttpStatusCode.Created, json);
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.False(error.IsRateLimited);
        Assert.Equal(201, error.StatusCode);
        AssertNoSecrets(error);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"number\":\"42\"}")]
    [InlineData("{\"number\":0}")]
    [InlineData("{\"number\":-1}")]
    [InlineData("{\"number\":2147483648}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task CreateIssue_MissingOrMalformedNumber_ThrowsApiException(string json)
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Created, json);
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.False(error.IsRateLimited);
        Assert.Equal(201, error.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"message\":42}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task CreateIssue_MalformedErrorBody_ThrowsSafeApiException(string json)
    {
        TokenResponse();
        handler.Enqueue(HttpStatusCode.Forbidden, json);
        using var client = Client();
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.False(error.IsRateLimited);
        Assert.Null(error.RetryAfter);
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task CanceledCall_DoesNotSend()
    {
        using var client = Client();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateIssueAsync(Draft, cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Token_CanceledHeldExchange_ReleasesWaitingCaller()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Enqueue(async (_, ct) =>
        {
            entered.SetResult();
            return await held.Task.WaitAsync(ct);
        });
        TokenResponse();
        IssueResponse();
        using var client = Client();
        using var cancellation = new CancellationTokenSource();

        Task<int> canceled = client.CreateIssueAsync(Draft, cancellation.Token);
        await entered.Task;
        Task<int> next = client.CreateIssueAsync(Draft, CancellationToken.None);
        Assert.False(next.IsCompleted);
        Assert.Single(handler.Requests);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(42, await next);
        Assert.Equal(new[] { "/app/installations/789/access_tokens", "/app/installations/789/access_tokens",
            "/repos/example/reports/issues" }, handler.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.Equal("Bearer " + Token, handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task Token_FailedExchange_IsNotCached_AndNextCallSucceeds()
    {
        handler.Enqueue(HttpStatusCode.Forbidden, "{\"message\":\"exchange denied\"}");
        TokenResponse("ghs_recovered");
        IssueResponse();
        IssueResponse();
        using var client = Client();

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(403, error.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(42, await client.CreateIssueAsync(Draft, CancellationToken.None));
        Assert.Equal(42, await client.CreateIssueAsync(Draft, CancellationToken.None));

        Assert.Equal(new[] { "/app/installations/789/access_tokens", "/app/installations/789/access_tokens",
            "/repos/example/reports/issues", "/repos/example/reports/issues" }, handler.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.All(handler.Requests.Skip(2), request => Assert.Equal("Bearer ghs_recovered", request.Authorization));
    }

    public void Dispose()
    {
        http.Dispose();
        rsa.Dispose();
    }
}
