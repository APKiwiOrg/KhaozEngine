using System;
using System.Net;
using System.Threading;

namespace KhaozEngine.Http;

/// <summary>
/// Immutable options for <see cref="HttpRetry.SendAsync"/>: how many attempts to make, how long a single
/// attempt is allowed to run before it is abandoned, which delay to wait between attempts, and which HTTP
/// statuses are worth retrying. Every knob is injectable so a test can run the retry loop with zero sleeps
/// and a scripted status set. Construct with an object initializer or start from <see cref="Default"/> and
/// override with a <c>with</c> expression. Every property validates on assignment, including through a
/// <c>with</c> expression, so an invalid policy can never be built.
/// </summary>
public sealed record HttpRetryPolicy
{
    /// <summary>The default <see cref="MaxAttempts"/>, matching the field-proven Nullwake policy.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>
    /// The default <see cref="PerAttemptTimeout"/>. Deliberately SHORTER than a typical HttpClient-wide
    /// timeout, so a single stalled attempt against a cold or overloaded backend is abandoned and retried
    /// instead of eating the whole request budget on one hung connection.
    /// </summary>
    public static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(12);

    int _maxAttempts = DefaultMaxAttempts;
    TimeSpan _perAttemptTimeout = DefaultPerAttemptTimeout;

    /// <summary>
    /// The maximum number of send attempts (the first attempt plus up to <c>MaxAttempts - 1</c> retries).
    /// Must be at least 1. Default 3.
    /// </summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        init
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxAttempts must be at least 1.");
            _maxAttempts = value;
        }
    }

    /// <summary>
    /// The time budget for a single attempt, enforced via a linked <see cref="CancellationTokenSource"/>. When
    /// an attempt exceeds this it is abandoned and treated as a retryable transport fault (see
    /// <see cref="HttpRetry.SendAsync"/>), never as the caller's own cancellation. Must be positive, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable the per-attempt budget. Default 12 seconds.
    /// </summary>
    public TimeSpan PerAttemptTimeout
    {
        get => _perAttemptTimeout;
        init
        {
            if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "PerAttemptTimeout must be positive, or Timeout.InfiniteTimeSpan to disable it.");
            _perAttemptTimeout = value;
        }
    }

    /// <summary>
    /// Maps a zero-based attempt index (the attempt that just failed) to the delay before the next attempt.
    /// The injectable no-sleep seam for tests: pass <c>_ =&gt; TimeSpan.Zero</c> to run the retry loop
    /// instantly. Default is 700 ms / 1600 ms / 3000 ms for attempts 0 / 1 / 2 and beyond.
    /// </summary>
    public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

    /// <summary>
    /// Decides whether a completed response's status code is worth retrying. Only consulted on a non-final
    /// attempt: the final attempt always returns its response as-is, retryable status or not. Default is
    /// 408 (Request Timeout), 500 (Internal Server Error), 502 (Bad Gateway), 503 (Service Unavailable), and
    /// 504 (Gateway Timeout).
    /// </summary>
    public Func<HttpStatusCode, bool> IsRetryableStatus { get; init; } = DefaultIsRetryableStatus;

    /// <summary>The default policy: <see cref="MaxAttempts"/> 3, <see cref="PerAttemptTimeout"/> 12 seconds.</summary>
    public static HttpRetryPolicy Default { get; } = new();

    static TimeSpan DefaultBackoff(int attempt) =>
        TimeSpan.FromMilliseconds(attempt switch { 0 => 700, 1 => 1600, _ => 3000 });

    static bool DefaultIsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
