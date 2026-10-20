using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

internal sealed class HookedMediaStore(InMemoryReportMediaStore inner, string prefix, Func<Task> beforeWrite) : IReportMediaStore
{
    bool hooked;

    public async Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content,
        ReportBlobHeaders headers, bool overwrite, CancellationToken cancellationToken)
    {
        if (!hooked && path.StartsWith(prefix, StringComparison.Ordinal))
        {
            hooked = true;
            await beforeWrite();
        }
        return await inner.PutAsync(area, path, content, headers, overwrite, cancellationToken);
    }

    public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
        => inner.GetAsync(area, path, maxBytes, cancellationToken);

    public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
        => inner.ExistsAsync(area, path, cancellationToken);

    public Task<IReadOnlyList<string>> ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)
        => inner.ListAsync(area, prefix, cancellationToken);

    public Task<bool> DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)
        => inner.DeleteAsync(area, path, cancellationToken);
}
