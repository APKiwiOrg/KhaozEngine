using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

// Hooks surround the real store so races retain its atomic create-if-absent behavior.
internal sealed class WorkerMediaStore(InMemoryReportMediaStore inner) : IReportMediaStore
{
    public Func<string, Task>? BeforeWrite { get; set; }
    public Func<string, Task>? AfterWrite { get; set; }
    public Func<string, Task>? BeforeRead { get; set; }
    public Func<string, Task>? BeforeExists { get; set; }

    public async Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content,
        ReportBlobHeaders headers, bool overwrite, CancellationToken cancellationToken)
    {
        if (BeforeWrite is not null) await BeforeWrite(path);
        bool written = await inner.PutAsync(area, path, content, headers, overwrite, cancellationToken);
        if (AfterWrite is not null) await AfterWrite(path);
        return written;
    }

    public async Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
    {
        if (BeforeRead is not null) await BeforeRead(path);
        return await inner.GetAsync(area, path, maxBytes, cancellationToken);
    }

    public async Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
    {
        if (BeforeExists is not null) await BeforeExists(path);
        return await inner.ExistsAsync(area, path, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)
        => inner.ListAsync(area, prefix, cancellationToken);

    public Task<bool> DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)
        => inner.DeleteAsync(area, path, cancellationToken);
}
