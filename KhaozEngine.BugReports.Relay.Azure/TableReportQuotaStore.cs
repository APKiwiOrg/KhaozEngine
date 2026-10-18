using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Optimistic quota counters in an existing Azure Table, using the entity ETag as their version.</summary>
public sealed class TableReportQuotaStore : IReportQuotaStore
{
    readonly IQuotaTable table;

    /// <summary>Wraps an existing table. The host owns credentials, provisioning and expired entity cleanup.</summary>
    public TableReportQuotaStore(TableClient table) : this(new QuotaTableAdapter(table)) { }

    internal TableReportQuotaStore(IQuotaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        this.table = table;
    }

    /// <inheritdoc />
    public Task<QuotaCounter?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();
        return table.GetAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(string key, long count, string? expectedVersion, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        cancellationToken.ThrowIfCancellationRequested();
        return expectedVersion is null
            ? table.AddAsync(key, count, expiresAt, cancellationToken)
            : table.ReplaceAsync(key, count, expectedVersion, expiresAt, cancellationToken);
    }
}
