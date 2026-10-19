using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.BugReports.Relay;

/// <summary>The two storage areas the relay writes to.</summary>
public enum ReportArea
{
    /// <summary>Report folders served at an unguessable capability URL.</summary>
    Public,

    /// <summary>Relay-private documents that are never served.</summary>
    Pending,
}

/// <summary>The content type and cache policy a stored blob is served with.</summary>
/// <param name="ContentType">The <c>Content-Type</c> header value.</param>
/// <param name="CacheControl">The <c>Cache-Control</c> header value.</param>
public readonly record struct ReportBlobHeaders(string ContentType, string CacheControl);

/// <summary>
/// The relay's blob storage. Paths are relative, lowercase and <c>/</c> separated. A store refuses any other path.
/// </summary>
public interface IReportMediaStore
{
    /// <summary>
    /// Writes <paramref name="content"/> at <paramref name="path"/>. Answers false only when <paramref name="overwrite"/> is
    /// false and the path already exists, in which case the first write is kept.
    /// </summary>
    Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
        bool overwrite, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the blob at <paramref name="path"/>. Answers null only when it is absent. Throws
    /// <see cref="System.IO.InvalidDataException"/> when it holds more than <paramref name="maxBytes"/> bytes. Any other
    /// fault throws, so a read failure is never mistaken for an absent blob.
    /// </summary>
    Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken);

    /// <summary>Answers whether a blob exists at <paramref name="path"/>.</summary>
    Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every full path under <paramref name="prefix"/> at any depth, in ordinal order. The prefix must end in
    /// <c>/</c> and the path before that slash must pass <see cref="ReportPaths.IsValid"/>. Answers an empty list when absent.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)
        => throw new NotSupportedException("IReportMediaStore.ListAsync is not implemented by this store.");

    /// <summary>Deletes the blob at <paramref name="path"/>. Answers true when deleted, false when already absent.</summary>
    Task<bool> DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)
        => throw new NotSupportedException("IReportMediaStore.DeleteAsync is not implemented by this store.");
}
