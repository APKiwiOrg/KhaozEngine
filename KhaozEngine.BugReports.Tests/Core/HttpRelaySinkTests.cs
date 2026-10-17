using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Http;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Core;

public sealed class HttpRelaySinkTests
{
    const string ReportId = TestBundles.ReportId;

    static readonly byte[] BundleBytes = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5];

    static readonly Uri RelayBase = new("https://relay.example/api");

    static BugReportSubmission Submission() => new(ReportId, "subject-1", BundleBytes);

    static HttpRelaySink Sink(FakeHttpHandler handler, Func<string?>? token = null) =>
        new(new HttpClient(handler), RelayBase, token ?? (() => "tok"))
        {
            RetryPolicy = HttpRelaySink.DefaultPolicy with { Backoff = _ => TimeSpan.Zero },
        };

    static string AcceptedJson(string reference) =>
        "{\"reportId\":\"" + ReportId + "\",\"reference\":\"" + reference + "\"}";

    [Fact]
    public async Task Posts_zip_with_bearer_and_idempotency_key()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted, AcceptedJson("R-ABCDEF"));

        await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://relay.example/api/bug-reports", request.Uri?.AbsoluteUri);
        Assert.Equal("Bearer tok", request.Authorization);
        Assert.Equal(ReportId, request.IdempotencyKey);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal(BundleBytes, request.Body);
    }

    [Fact]
    public async Task Accepted_returns_the_relay_reference()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted, AcceptedJson("R-ABCDEF"));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Sent, "R-ABCDEF", null, null), result);
    }

    [Fact]
    public async Task Accepted_without_a_readable_body_uses_the_local_reference()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond(HttpStatusCode.Accepted, "not json");

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Sent, ReportIds.ReferenceCode(ReportId), null, null), result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Missing_token_queues_without_a_request()
    {
        foreach (string? token in new[] { null, "", "   " })
        {
            var handler = new FakeHttpHandler();

            BugReportSubmitResult result = await Sink(handler, () => token).SubmitAsync(Submission(), CancellationToken.None);

            Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "no_token", null), result);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task Too_many_requests_queues_with_retry_after()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond((HttpStatusCode)429, configure: response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(BugReportSubmitStatus.Queued, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal("http_429", result.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Server_error_retries_then_queues()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable);

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "http_503", null), result);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(BundleBytes, request.Body));
    }

    [Fact]
    public async Task Network_fault_queues()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Throw(new HttpRequestException("down"))
            .Throw(new HttpRequestException("down"))
            .Throw(new HttpRequestException("down"));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "network", null), result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Attempt_timeouts_queue_as_network()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Throw(new TaskCanceledException("attempt timed out"))
            .Throw(new TaskCanceledException("attempt timed out"))
            .Throw(new TaskCanceledException("attempt timed out"));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "network", null), result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Request_timeout_retries_then_queues()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.RequestTimeout)
            .Respond(HttpStatusCode.RequestTimeout)
            .Respond(HttpStatusCode.RequestTimeout);

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "http_408", null), result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Server_error_keeps_the_last_retry_after()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable, configure: response =>
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "http_503", TimeSpan.FromSeconds(30)), result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_after_date_in_the_past_is_zero()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond((HttpStatusCode)429, configure: response =>
            response.Headers.RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "http_429", TimeSpan.Zero), result);
    }

    [Fact]
    public void Non_http_relay_base_is_refused()
    {
        foreach (string address in new[] { "ftp://relay.example/", "file:///tmp/" })
            Assert.Throws<ArgumentException>(() => new HttpRelaySink(new HttpClient(), new Uri(address), () => "tok"));
    }

    [Fact]
    public async Task Unauthorized_queues_for_the_next_sign_in()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond(HttpStatusCode.Unauthorized, "{\"error\":\"bad_token\"}");

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Queued, null, "http_401", null), result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Validation_reject_is_not_queued()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.UnprocessableEntity, "{\"error\":\"hash_mismatch: clip/000.jpg\"}");

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null, "hash_mismatch: clip/000.jpg", null), result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reject_without_a_relay_error_uses_the_status()
    {
        FakeHttpHandler handler = new FakeHttpHandler().Respond(HttpStatusCode.RequestEntityTooLarge, "<html>too large</html>");

        BugReportSubmitResult result = await Sink(handler).SubmitAsync(Submission(), CancellationToken.None);

        Assert.Equal(new BugReportSubmitResult(BugReportSubmitStatus.Rejected, null, "http_413", null), result);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var caller = new CancellationTokenSource();
        FakeHttpHandler handler = new FakeHttpHandler().CancelThenHang(caller);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sink(handler).SubmitAsync(Submission(), caller.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Default_policy_retries_408_and_5xx_only()
    {
        HttpRetryPolicy policy = HttpRelaySink.DefaultPolicy;
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.PerAttemptTimeout);
        Assert.True(policy.IsRetryableStatus(HttpStatusCode.RequestTimeout));
        Assert.True(policy.IsRetryableStatus(HttpStatusCode.InternalServerError));
        Assert.True(policy.IsRetryableStatus(HttpStatusCode.HttpVersionNotSupported));
        Assert.True(policy.IsRetryableStatus((HttpStatusCode)599));
        Assert.False(policy.IsRetryableStatus(HttpStatusCode.Unauthorized));
        Assert.False(policy.IsRetryableStatus((HttpStatusCode)429));
        Assert.False(policy.IsRetryableStatus(HttpStatusCode.UnprocessableEntity));
        Assert.False(policy.IsRetryableStatus(HttpStatusCode.Accepted));
    }
}
