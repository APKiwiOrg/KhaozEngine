using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.BugReports.Relay;

/// <summary>Whether a report is allowed, and how long a denied caller should wait.</summary>
internal readonly record struct QuotaDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Enforces fixed UTC report quotas through optimistic concurrency. Quota is consumed before validation, so a client
/// resend of an already-accepted report counts against the account's hourly and daily limits. Earlier increments stay
/// counted when a later window denies a request. This safe over-count avoids rolling back another caller's increment.
/// </summary>
internal sealed class ReportQuotaGate(IReportQuotaStore store, BugReportRelayOptions options, TimeProvider time,
    ILogger logger)
{
    /// <summary>The maximum write attempts for each window.</summary>
    public const int MaxWriteAttempts = 5;

    /// <summary>The short retry interval when write contention exhausts the attempt budget.</summary>
    public static readonly TimeSpan ContentionRetryAfter = TimeSpan.FromSeconds(5);

    /// <summary>Checks all windows first, then increments each in order. Caller cancellation and store faults propagate.</summary>
    public async Task<QuotaDecision> TryConsumeAsync(string subject, CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow().ToUniversalTime();
        var windows = QuotaWindows.For(subject, now, options);
        TimeSpan longestRetry = TimeSpan.Zero;
        foreach (QuotaWindow window in windows)
        {
            QuotaCounter? counter = await store.ReadAsync(window.Key, cancellationToken).ConfigureAwait(false);
            if (counter is not null && counter.Count >= window.Limit)
            {
                TimeSpan retry = RetryFor(window, now);
                if (retry > longestRetry)
                    longestRetry = retry;
            }
        }

        if (longestRetry > TimeSpan.Zero)
            return new QuotaDecision(false, longestRetry);

        foreach (QuotaWindow window in windows)
        {
            bool written = false;
            for (int attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                QuotaCounter? counter = await store.ReadAsync(window.Key, cancellationToken).ConfigureAwait(false);
                if (counter is not null && counter.Count >= window.Limit)
                    return new QuotaDecision(false, RetryFor(window, now));
                if (await store.TryWriteAsync(window.Key, (counter?.Count ?? 0) + 1, counter?.Version,
                    window.End.AddDays(1), cancellationToken).ConfigureAwait(false))
                {
                    written = true;
                    break;
                }
            }

            if (!written)
            {
                logger.Warn($"Quota contention exhausted for {window.Key[..2]} window.");
                return new QuotaDecision(false, ContentionRetryAfter);
            }
        }

        return new QuotaDecision(true, TimeSpan.Zero);
    }

    static TimeSpan RetryFor(QuotaWindow window, DateTimeOffset now) =>
        TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((window.End - now).TotalSeconds)));
}
