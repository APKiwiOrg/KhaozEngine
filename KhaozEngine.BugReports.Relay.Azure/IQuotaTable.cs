using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay.Azure;

internal interface IQuotaTable
{
    Task<QuotaCounter?> GetAsync(string key, CancellationToken ct);
    Task<bool> AddAsync(string key, long count, DateTimeOffset expiresAt, CancellationToken ct);
    Task<bool> ReplaceAsync(string key, long count, string etag, DateTimeOffset expiresAt, CancellationToken ct);
}
