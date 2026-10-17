using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed class QuotaTableAdapter : IQuotaTable
{
    readonly TableClient table;

    public QuotaTableAdapter(TableClient table)
    {
        ArgumentNullException.ThrowIfNull(table);
        this.table = table;
    }

    public async Task<QuotaCounter?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            TableEntity entity = (await table.GetEntityAsync<TableEntity>(key, "q", cancellationToken: ct).ConfigureAwait(false)).Value;
            long count = entity.GetInt64("Count") ?? throw new InvalidDataException("The quota entity has no Count.");
            return new QuotaCounter(count, entity.ETag.ToString());
        }
        catch (RequestFailedException failure) when (AzureStatus.IsAbsent(failure.Status))
        {
            return null;
        }
    }

    public async Task<bool> AddAsync(string key, long count, DateTimeOffset expiresAt, CancellationToken ct)
    {
        try
        {
            await table.AddEntityAsync(Entity(key, count, expiresAt), ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException failure) when (failure.Status == 409)
        {
            return false;
        }
    }

    public async Task<bool> ReplaceAsync(string key, long count, string etag, DateTimeOffset expiresAt, CancellationToken ct)
    {
        try
        {
            await table.UpdateEntityAsync(Entity(key, count, expiresAt), new ETag(etag), TableUpdateMode.Replace, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException failure) when (failure.Status == 412)
        {
            return false;
        }
    }

    static TableEntity Entity(string key, long count, DateTimeOffset expiresAt) => new(key, "q")
    {
        ["Count"] = count,
        ["ExpiresAt"] = expiresAt,
    };
}
