using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// An <see cref="IReportQuotaStore"/> held in memory, for tests and local runs. Versions are a per-store increasing
/// integer rendered as a string. The expiry is kept with the counter but never enforced, because window keys already
/// carry their window.
/// </summary>
public sealed class InMemoryReportQuotaStore : IReportQuotaStore
{
    readonly object gate = new();
    readonly Dictionary<string, (long Count, long Version, DateTimeOffset ExpiresAt)> counters = new(StringComparer.Ordinal);
    long lastVersion;

    /// <inheritdoc />
    public Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(counters.TryGetValue(key, out var counter)
                ? new QuotaCounter(counter.Count, counter.Version.ToString(CultureInfo.InvariantCulture))
                : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            bool exists = counters.TryGetValue(key, out var current);
            bool matches = expectedVersion is null
                ? !exists
                : exists && expectedVersion == current.Version.ToString(CultureInfo.InvariantCulture);
            if (!matches)
                return Task.FromResult(false);
            counters[key] = (count, ++lastVersion, expiresAt);
        }

        return Task.FromResult(true);
    }
}
