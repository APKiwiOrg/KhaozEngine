using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    readonly Queue<Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>> steps = new();
    readonly List<RecordedRequest> requests = new();
    readonly object gate = new();

    public RecordedRequest[] Requests
    {
        get { lock (gate) return requests.ToArray(); }
    }

    public void Enqueue(HttpStatusCode status, string json = "{}", params (string Name, string Value)[] headers) =>
        Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(json) };
            foreach ((string name, string value) in headers)
                response.Headers.TryAddWithoutValidation(name, value);
            return Task.FromResult(response);
        });

    public void Enqueue(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (gate) steps.Enqueue(step);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request, body);
        Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> step;
        lock (gate)
        {
            requests.Add(recorded);
            step = steps.Dequeue();
        }
        return await step(recorded, cancellationToken);
    }
}

internal sealed class RecordedRequest
{
    public RecordedRequest(HttpRequestMessage request, string body)
    {
        Method = request.Method;
        Uri = request.RequestUri!;
        Body = body;
        Authorization = request.Headers.Authorization?.ToString() ?? "";
        foreach (var header in request.Headers)
            Headers.Add(header.Key, string.Join(", ", header.Value));
    }

    public HttpMethod Method { get; }
    public Uri Uri { get; }
    public string Body { get; }
    public string Authorization { get; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}
