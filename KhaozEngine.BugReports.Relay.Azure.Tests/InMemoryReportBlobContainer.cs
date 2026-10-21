using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

internal sealed class InMemoryReportBlobContainer : IReportBlobContainer
{
    readonly object gate = new();
    readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
    public bool? LastOverwrite { get; private set; }
    public ReportBlobHeaders? LastHeaders { get; private set; }

    public Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> bytes, ReportBlobHeaders headers,
        bool overwrite, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            LastOverwrite = overwrite;
            LastHeaders = headers;
            if (!overwrite && blobs.ContainsKey(path))
                return Task.FromResult(false);
            blobs[path] = bytes.ToArray();
            return Task.FromResult(true);
        }
    }

    public Task<ReadOnlyMemory<byte>?> DownloadAsync(string path, int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!blobs.TryGetValue(path, out byte[]? bytes))
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            if (bytes.Length > maxBytes)
                throw new InvalidDataException("Blob exceeds the requested cap.");
            return Task.FromResult<ReadOnlyMemory<byte>?>(bytes.ToArray());
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(blobs.ContainsKey(path));
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(blobs.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }
    }

    public Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(blobs.Remove(path));
    }
}
