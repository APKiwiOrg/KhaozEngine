using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Http;

/// <summary>
/// The extension-method form of <see cref="HttpRetry.SendAsync"/>, so a call site reads
/// <c>client.SendWithRetryAsync(...)</c> the way the original Nullwake helper did.
/// </summary>
public static class HttpClientRetryExtensions
{
    /// <summary>
    /// Forwards to <see cref="HttpRetry.SendAsync"/>. See its remarks for the exact retry semantics.
    /// </summary>
    /// <param name="client">The client to send with. Not disposed by this method.</param>
    /// <param name="requestFactory">Builds a fresh <see cref="HttpRequestMessage"/> for each attempt.</param>
    /// <param name="policy">The retry policy, or <see cref="HttpRetryPolicy.Default"/> when null.</param>
    /// <param name="ct">The caller's cancellation token. Never retried.</param>
    /// <returns>
    /// The response from a non-retryable attempt, or the last response once attempts are exhausted.
    /// </returns>
    public static Task<HttpResponseMessage> SendWithRetryAsync(
        this HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpRetryPolicy? policy = null,
        CancellationToken ct = default)
        => HttpRetry.SendAsync(client, requestFactory, policy, ct);
}
