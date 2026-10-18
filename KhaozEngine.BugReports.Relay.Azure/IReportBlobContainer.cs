using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay.Azure;

internal interface IReportBlobContainer
{
    Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> bytes, ReportBlobHeaders headers, bool overwrite, CancellationToken ct);
    Task<ReadOnlyMemory<byte>?> DownloadAsync(string path, int maxBytes, CancellationToken ct);
    Task<bool> ExistsAsync(string path, CancellationToken ct);
}
