using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// An <see cref="IReportMediaStore"/> held in memory, for tests and local runs. Throws <see cref="ArgumentException"/>
/// for any path a real store would refuse.
/// </summary>
public sealed class InMemoryReportMediaStore : IReportMediaStore
{
    readonly object gate = new();
    readonly Dictionary<(ReportArea Area, string Path), (byte[] Content, ReportBlobHeaders Headers)> blobs = new();

    /// <inheritdoc />
    public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
        bool overwrite, CancellationToken cancellationToken)
    {
        var key = Key(area, path);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!overwrite && blobs.ContainsKey(key))
                return Task.FromResult(false);
            blobs[key] = (content.ToArray(), headers);
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes,
        CancellationToken cancellationToken)
    {
        var key = Key(area, path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] content;
        lock (gate)
        {
            if (!blobs.TryGetValue(key, out var blob))
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            content = blob.Content;
        }

        if (content.Length > maxBytes)
            throw new InvalidDataException($"The blob at '{path}' holds {content.Length} bytes, over the {maxBytes} byte cap.");
        return Task.FromResult<ReadOnlyMemory<byte>?>(content.ToArray());
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
    {
        var key = Key(area, path);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(blobs.ContainsKey(key));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith('/') || !ReportPaths.IsValid(prefix[..^1]))
            throw new ArgumentException("A valid report path followed by '/' is required.", nameof(prefix));
        _ = Key(area, prefix[..^1]);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(blobs.Keys
                .Where(key => key.Area == area && key.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)
    {
        var key = Key(area, path);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(blobs.Remove(key));
    }

    /// <summary>The headers the blob at <paramref name="path"/> was last written with, or null when it is absent.</summary>
    public ReportBlobHeaders? HeadersOf(ReportArea area, string path)
    {
        var key = Key(area, path);
        lock (gate)
            return blobs.TryGetValue(key, out var blob) ? blob.Headers : null;
    }

    /// <summary>Every path stored in <paramref name="area"/>, in ordinal order.</summary>
    public IReadOnlyList<string> Paths(ReportArea area)
    {
        lock (gate)
        {
            return blobs.Keys.Where(key => key.Area == area).Select(key => key.Path)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
    }

    static (ReportArea, string) Key(ReportArea area, string path)
    {
        if (!Enum.IsDefined(area))
            throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown report area.");
        if (!ReportPaths.IsValid(path))
            throw new ArgumentException($"'{path}' is not a valid report path.", nameof(path));
        return (area, path);
    }
}
