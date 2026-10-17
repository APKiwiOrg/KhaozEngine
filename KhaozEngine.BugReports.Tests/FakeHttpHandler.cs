using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.BugReports;

// A scripted HttpMessageHandler. Each send dequeues the next outcome and records a snapshot of the request,
// taken before the sender disposes the message, so a test can inspect headers and body after the call.
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> outcomes = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
    {
        outcomes.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (json is not null)
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        outcomes.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    // Cancels the caller's own source while the request is in flight, then waits for the linked token to fire.
    public FakeHttpHandler CancelThenHang(CancellationTokenSource caller)
    {
        outcomes.Enqueue(async token =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Task.Delay(Infinite) returned without cancellation.");
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Idempotency-Key", out IEnumerable<string>? keys) ? string.Join(",", keys) : null,
            request.Content?.Headers.ContentType?.ToString(),
            body));
        if (outcomes.Count == 0)
            throw new InvalidOperationException("FakeHttpHandler ran out of scripted outcomes.");
        return await outcomes.Dequeue()(cancellationToken);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method, Uri? Uri, string? Authorization, string? IdempotencyKey, string? ContentType, byte[] Body);
