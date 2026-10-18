using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay;

/// <summary>Injects a bounded number of write conflicts over the real in-memory quota store.</summary>
internal sealed class ConflictingQuotaStore(int conflicts, string? conflictKey = null) : IReportQuotaStore
{
    readonly Dictionary<string, int> reads = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> writes = new(StringComparer.Ordinal);
    int remainingConflicts = conflicts;

    public InMemoryReportQuotaStore Inner { get; } = new();
    public List<(string Key, long Count, string? ExpectedVersion, DateTimeOffset ExpiresAt)> Attempts { get; } = new();
    public Func<string, int, CancellationToken, Task>? BeforeRead { get; set; }

    public int ReadCount(string key) => reads.GetValueOrDefault(key);
    public int WriteCount(string key) => writes.GetValueOrDefault(key);

    public async Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = reads[key] = ReadCount(key) + 1;
        if (BeforeRead is not null)
            await BeforeRead(key, count, cancellationToken);
        return await Inner.ReadAsync(key, cancellationToken);
    }

    public Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        writes[key] = WriteCount(key) + 1;
        Attempts.Add((key, count, expectedVersion, expiresAt));
        if (remainingConflicts > 0 && (conflictKey is null || key == conflictKey))
        {
            remainingConflicts--;
            return Task.FromResult(false);
        }

        return Inner.TryWriteAsync(key, count, expectedVersion, expiresAt, cancellationToken);
    }
}
