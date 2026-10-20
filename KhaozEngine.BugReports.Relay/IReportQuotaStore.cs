using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>A stored quota count and the opaque version that guards its next write.</summary>
/// <param name="Count">Reports counted in the window.</param>
/// <param name="Version">The store's concurrency token for this value.</param>
public sealed record QuotaCounter(long Count, string Version);

/// <summary>Fixed-window quota counters with optimistic concurrency.</summary>
public interface IReportQuotaStore
{
    /// <summary>Reads the counter at <paramref name="key"/>, or null when it does not exist.</summary>
    Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="count"/> at <paramref name="key"/>. A null <paramref name="expectedVersion"/> means create
    /// only. Answers false on any version conflict, including a create over an existing counter.
    /// </summary>
    Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}
