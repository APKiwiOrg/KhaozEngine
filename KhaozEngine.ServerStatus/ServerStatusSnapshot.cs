using System;

#nullable enable

namespace KhaozEngine.ServerStatus;

/// <summary>
/// The poller's degradable view of the endpoint: the last report that successfully parsed (retained even
/// after later failures), when that success happened, when the most recent attempt happened, and how many
/// attempts in a row have failed since. A never-throwing poller mutates this instead of surfacing errors, so
/// a consumer reads a stable snapshot and the <see cref="ServerStatusEvaluator"/> decides when the retained
/// report is too stale to trust.
/// </summary>
public readonly record struct ServerStatusSnapshot
{
    /// <summary>The last report that parsed, or null before the first success.</summary>
    public ServerStatusReport? LastReport { get; init; }

    /// <summary>UTC of the last successful fetch, or null before the first success.</summary>
    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>UTC of the most recent fetch attempt (success or failure), or null before the first attempt.</summary>
    public DateTimeOffset? LastAttemptUtc { get; init; }

    /// <summary>Consecutive failed attempts since the last success. Zero right after a success.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>True once at least one fetch has succeeded and a report is retained.</summary>
    public bool HasReport => LastReport is not null;

    /// <summary>
    /// Age of the retained report at <paramref name="nowUtc"/> (time since the last successful fetch).
    /// <see cref="TimeSpan.MaxValue"/> when no fetch has ever succeeded, so a never-answered endpoint reads
    /// as maximally stale.
    /// </summary>
    public TimeSpan StalenessAt(DateTimeOffset nowUtc) =>
        LastSuccessUtc is { } success ? nowUtc - success : TimeSpan.MaxValue;

    /// <summary>The empty starting snapshot: no report, no attempts.</summary>
    public static ServerStatusSnapshot Empty => default;
}
