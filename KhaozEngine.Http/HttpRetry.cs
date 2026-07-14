using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Http;

/// <summary>
/// A bounded send-with-retry helper over a caller-supplied <see cref="HttpClient"/>. Absorbs a cold or
/// momentarily-stalled backend (a warming-up App Service, a transient 503 behind a load balancer) so the
/// caller sees a real response instead of a client timeout, without retrying forever or retrying a
/// definitive rejection. Field-proven as Nullwake's sign-in retry policy before being promoted here.
/// </summary>
public static class HttpRetry
{
    /// <summary>
    /// Sends a request built by <paramref name="requestFactory"/>, retrying on a retryable status or a
    /// transport fault up to <paramref name="policy"/>'s <see cref="HttpRetryPolicy.MaxAttempts"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="requestFactory"/> is a FACTORY, not a shared message instance, because a sent
    /// <see cref="HttpRequestMessage"/> cannot be resent. Each attempt builds and disposes its own message.
    /// </para>
    /// <para>
    /// <paramref name="ct"/> is the caller's own cancellation and is never retried: a cancellation already
    /// requested when this method is called throws before any attempt is made, and a cancellation that
    /// arrives mid-attempt propagates immediately instead of being treated as a retryable transport fault.
    /// </para>
    /// <para>
    /// Each attempt gets its own budget via a linked <see cref="CancellationTokenSource"/> armed with
    /// <see cref="HttpRetryPolicy.PerAttemptTimeout"/>. An attempt that trips this per-attempt timeout (as
    /// opposed to the caller's own <paramref name="ct"/>) surfaces as a transport fault and is retried like
    /// any other one.
    /// </para>
    /// <para>
    /// A completed response whose status <see cref="HttpRetryPolicy.IsRetryableStatus"/> accepts is disposed
    /// and retried after <see cref="HttpRetryPolicy.Backoff"/>, unless this was the final attempt, in which
    /// case the response is returned as-is so the caller sees the real status. A non-retryable status (a
    /// definitive rejection such as 401 or 400) is returned immediately on the first attempt. A transport
    /// fault (<see cref="HttpRequestException"/>, or a cancellation that is not the caller's own) is retried
    /// with backoff and rethrown on the final attempt.
    /// </para>
    /// </remarks>
    /// <param name="client">The client to send with. Not disposed by this method.</param>
    /// <param name="requestFactory">Builds a fresh <see cref="HttpRequestMessage"/> for each attempt.</param>
    /// <param name="policy">The retry policy, or <see cref="HttpRetryPolicy.Default"/> when null.</param>
    /// <param name="ct">The caller's cancellation token. Never retried (see remarks).</param>
    /// <returns>
    /// The response from a non-retryable attempt, or the last response once attempts are exhausted.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    /// <exception cref="HttpRequestException">
    /// Every attempt failed with a transport fault and the final attempt's exception was rethrown.
    /// </exception>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpRetryPolicy? policy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        HttpRetryPolicy effectivePolicy = policy ?? HttpRetryPolicy.Default;

        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            bool lastAttempt = attempt >= effectivePolicy.MaxAttempts - 1;
            using CancellationTokenSource attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(effectivePolicy.PerAttemptTimeout);
            using HttpRequestMessage request = requestFactory();
            try
            {
                HttpResponseMessage response = await client.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                if (!lastAttempt && effectivePolicy.IsRetryableStatus(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(effectivePolicy.Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            // A per-attempt timeout cancels attemptCts (not ct) and surfaces as a transport fault: retry it.
            // A caller cancellation trips ct and is rethrown by ThrowIfCancellationRequested next loop, or
            // propagates straight through here since the filter below then evaluates false.
            catch (Exception ex) when (IsTransport(ex) && !ct.IsCancellationRequested)
            {
                if (lastAttempt)
                    throw;
                await Task.Delay(effectivePolicy.Backoff(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException;
}
