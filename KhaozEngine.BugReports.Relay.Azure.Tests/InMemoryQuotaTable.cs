using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

internal sealed class InMemoryQuotaTable : IQuotaTable
{
    readonly object gate = new();
    readonly Dictionary<string, QuotaCounter> counters = new(StringComparer.Ordinal);
    long version;
    public int Adds { get; private set; }
    public int Replaces { get; private set; }
    public DateTimeOffset LastExpiry { get; private set; }

    public Task<QuotaCounter?> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(counters.GetValueOrDefault(key));
    }

    public Task<bool> AddAsync(string key, long count, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            Adds++;
            LastExpiry = expiresAt;
            return Task.FromResult(!counters.ContainsKey(key) && Write(key, count));
        }
    }

    public Task<bool> ReplaceAsync(string key, long count, string etag, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            Replaces++;
            LastExpiry = expiresAt;
            return Task.FromResult(counters.TryGetValue(key, out QuotaCounter? value)
                && value.Version == etag && Write(key, count));
        }
    }

    bool Write(string key, long count)
    {
        counters[key] = new QuotaCounter(count, (++version).ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
