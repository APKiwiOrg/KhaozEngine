using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Http;
using Xunit;

namespace KhaozEngine.Tests.Http;

/// <summary>
/// Headless coverage of <see cref="HttpRetry.SendAsync"/> against a scripted fake
/// <see cref="HttpMessageHandler"/>: no real network, no real sleeps (every test but
/// <see cref="BackoffSeam_ReceivesAttemptIndices"/> zeroes the backoff), so the whole suite runs instantly.
/// </summary>
public class HttpRetryTests
{
    static HttpRetryPolicy NoSleepPolicy(int maxAttempts = HttpRetryPolicy.DefaultMaxAttempts) =>
        HttpRetryPolicy.Default with { MaxAttempts = maxAttempts, Backoff = _ => TimeSpan.Zero };

    static Func<HttpRequestMessage> Factory() => () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

    [Fact]
    public async Task TransientThenSuccess_ReturnsSuccessAfterTwoAttempts()
    {
        var handler = new ScriptedHandler(
            Outcomes.Throw(new HttpRequestException("transient")),
            Outcomes.Status(HttpStatusCode.OK));
        using HttpClient client = new(handler);

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), NoSleepPolicy());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryableStatusThenSuccess_ReturnsSuccessAndDisposesFirstResponse()
    {
        var firstContent = new DisposeTrackingContent();
        var handler = new ScriptedHandler(
            Outcomes.StatusWithContent(HttpStatusCode.ServiceUnavailable, firstContent),
            Outcomes.Status(HttpStatusCode.OK));
        using HttpClient client = new(handler);

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), NoSleepPolicy());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(firstContent.Disposed);
    }

    [Fact]
    public async Task ExhaustToFailure_Status_ReturnsFinalResponseAfterMaxAttempts()
    {
        var handler = new ScriptedHandler(
            Outcomes.Status(HttpStatusCode.ServiceUnavailable),
            Outcomes.Status(HttpStatusCode.ServiceUnavailable),
            Outcomes.Status(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = new(handler);

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), NoSleepPolicy(maxAttempts: 3));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ExhaustToFailure_Transport_RethrowsAfterMaxAttempts()
    {
        var handler = new ScriptedHandler(
            Outcomes.Throw(new HttpRequestException("down")),
            Outcomes.Throw(new HttpRequestException("down")),
            Outcomes.Throw(new HttpRequestException("down")));
        using HttpClient client = new(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => HttpRetry.SendAsync(client, Factory(), NoSleepPolicy(maxAttempts: 3)));

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task NoRetryOn401_ReturnsResponseAfterSingleAttempt()
    {
        var handler = new ScriptedHandler(Outcomes.Status(HttpStatusCode.Unauthorized));
        using HttpClient client = new(handler);

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), NoSleepPolicy());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NoRetryOn400_ReturnsResponseAfterSingleAttempt()
    {
        var handler = new ScriptedHandler(Outcomes.Status(HttpStatusCode.BadRequest));
        using HttpClient client = new(handler);

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), NoSleepPolicy());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellationPropagates_AlreadyCanceled_ThrowsWithZeroAttempts()
    {
        var handler = new ScriptedHandler();
        using HttpClient client = new(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpRetry.SendAsync(client, Factory(), NoSleepPolicy(), cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationPropagates_MidFlightCancel_PropagatesWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new ScriptedHandler(Outcomes.CancelThenHang(cts), Outcomes.Status(HttpStatusCode.OK));
        using HttpClient client = new(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => HttpRetry.SendAsync(client, Factory(), NoSleepPolicy(), cts.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task BackoffSeam_ReceivesAttemptIndices()
    {
        var handler = new ScriptedHandler(
            Outcomes.Throw(new HttpRequestException("down")),
            Outcomes.Throw(new HttpRequestException("down")),
            Outcomes.Throw(new HttpRequestException("down")));
        using HttpClient client = new(handler);
        var seenAttempts = new List<int>();
        HttpRetryPolicy policy = HttpRetryPolicy.Default with
        {
            MaxAttempts = 3,
            Backoff = attempt =>
            {
                seenAttempts.Add(attempt);
                return TimeSpan.Zero;
            },
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => HttpRetry.SendAsync(client, Factory(), policy));

        Assert.Equal(new[] { 0, 1 }, seenAttempts);
    }

    [Fact]
    public async Task PerAttemptTimeout_AbandonsHungAttemptAndRetries()
    {
        var handler = new ScriptedHandler(Outcomes.Hang(), Outcomes.Status(HttpStatusCode.OK));
        using HttpClient client = new(handler);
        HttpRetryPolicy policy = HttpRetryPolicy.Default with
        {
            MaxAttempts = 2,
            PerAttemptTimeout = TimeSpan.FromMilliseconds(50),
            Backoff = _ => TimeSpan.Zero,
        };

        HttpResponseMessage response = await HttpRetry.SendAsync(client, Factory(), policy);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FactoryPerAttempt_BuildsAFreshMessageEachTime()
    {
        var handler = new ScriptedHandler(
            Outcomes.Throw(new HttpRequestException("transient")),
            Outcomes.Status(HttpStatusCode.OK));
        using HttpClient client = new(handler);
        int factoryCalls = 0;
        HttpRequestMessage BuildRequest()
        {
            factoryCalls++;
            return new HttpRequestMessage(HttpMethod.Get, "https://example.test/");
        }

        await HttpRetry.SendAsync(client, BuildRequest, NoSleepPolicy());

        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.NotSame(handler.Requests[0], handler.Requests[1]);
    }

    [Fact]
    public void Policy_RejectsMaxAttemptsBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpRetryPolicy.Default with { MaxAttempts = 0 });
    }

    [Fact]
    public void Policy_RejectsNonPositiveFinitePerAttemptTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HttpRetryPolicy.Default with { PerAttemptTimeout = TimeSpan.Zero });
    }

    [Fact]
    public void Policy_AllowsInfinitePerAttemptTimeout()
    {
        HttpRetryPolicy policy = HttpRetryPolicy.Default with { PerAttemptTimeout = Timeout.InfiniteTimeSpan };
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.PerAttemptTimeout);
    }

    // A scripted outcome for one attempt: build the response (or throw / hang) given the request the
    // production code built and the per-attempt cancellation token it sent with.
    delegate Task<HttpResponseMessage> ScriptedOutcome(HttpRequestMessage request, CancellationToken cancellationToken);

    static class Outcomes
    {
        public static ScriptedOutcome Status(HttpStatusCode status) =>
            (_, _) => Task.FromResult(new HttpResponseMessage(status));

        public static ScriptedOutcome StatusWithContent(HttpStatusCode status, HttpContent content) =>
            (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = content });

        public static ScriptedOutcome Throw(Exception ex) =>
            (_, _) => Task.FromException<HttpResponseMessage>(ex);

        // Never completes until the per-attempt cancellation token fires, at which point Task.Delay throws
        // the way a genuinely stalled connection eventually does under the linked CancellationTokenSource.
        public static ScriptedOutcome Hang() =>
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable: Task.Delay(Infinite) should have thrown on cancellation.");
            };

        // Simulates a caller cancellation that lands while a request is in flight: cancel the caller's own
        // CancellationTokenSource (which HttpRetry linked into the per-attempt token), then hang until the
        // linked token observes it.
        public static ScriptedOutcome CancelThenHang(CancellationTokenSource callerCts) =>
            async (_, ct) =>
            {
                callerCts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable: Task.Delay(Infinite) should have thrown on cancellation.");
            };
    }

    // Records every request it saw and replays one scripted outcome per call, in order.
    sealed class ScriptedHandler : HttpMessageHandler
    {
        readonly Queue<ScriptedOutcome> _outcomes;

        public ScriptedHandler(params ScriptedOutcome[] outcomes) => _outcomes = new Queue<ScriptedOutcome>(outcomes);

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_outcomes.Count == 0)
                throw new InvalidOperationException("ScriptedHandler ran out of scripted outcomes.");
            ScriptedOutcome outcome = _outcomes.Dequeue();
            return await outcome(request, cancellationToken);
        }
    }

    // A content body that records whether it was disposed, so RetryableStatusThenSuccess can prove the
    // discarded retryable response really is disposed before the retry, not just abandoned.
    sealed class DisposeTrackingContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Disposed = true;
            base.Dispose(disposing);
        }
    }
}
