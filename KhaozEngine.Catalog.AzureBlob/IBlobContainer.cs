using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.AzureBlob;

/// <summary>The two headers every hash object is stored with, which is what makes it cacheable forever.</summary>
/// <param name="ContentType">The stored content type, always the binary one.</param>
/// <param name="CacheControl">The stored cache directive, always the immutable one.</param>
internal readonly record struct BlobObjectHeaders(string ContentType, string CacheControl);

/// <summary>
/// The five container operations the store needs, and no more. It exists so the Azure SDK is named in ONE
/// file (<see cref="BlobContainerAdapter"/>) plus the store's public constructor: the store itself is then
/// ordinary code that can be driven by an in-memory double, and the SDK's own shapes (a
/// <c>RequestFailedException</c> status, an <c>ETag</c> condition, a streaming download's declared length)
/// stop at the adapter instead of spreading through the provider.
/// <para>
/// Each member is written so the DECISION lives here rather than at the call site: the upload is conditional
/// and answers whether it wrote, the download answers null for absent and refuses an oversize object from
/// its declared length before a body is buffered, and the delete answers whether the object was there.
/// </para>
/// <para>
/// The two READS never fault. A service that refused the read answers the same as a container that holds
/// nothing, because the caller's next move is the same either way, so the SDK's exception stops at the
/// adapter (<see cref="BlobContainerAdapter.IsAbsentReadAnswer"/>). The two WRITES are loud, deliberately: an
/// origin that silently did not write is an origin a client is about to be sent to.
/// </para>
/// </summary>
internal interface IBlobContainer
{
    /// <summary>Whether the container holds an object under that key, and FALSE when the read faulted.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads ONLY when the key is absent, answering whether this call wrote it. A key that was already
    /// there is not an error: the name is a content address, so the other writer wrote the same bytes.
    /// </summary>
    Task<bool> UploadIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        BlobObjectHeaders headers,
        CancellationToken cancellationToken);

    /// <summary>
    /// The object's bytes, or NULL when the key is absent, the read faulted, or the object declares more than
    /// <paramref name="maximumBytes"/>. The ceiling is applied to the DECLARED length, so a hostile or
    /// misconfigured origin costs one round trip rather than an allocation.
    /// </summary>
    Task<ReadOnlyMemory<byte>?> DownloadAsync(string key, int maximumBytes, CancellationToken cancellationToken);

    /// <summary>Deletes one object, answering whether it was there.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every key the container holds, in whatever order the service lists them.</summary>
    IAsyncEnumerable<string> ListKeysAsync(CancellationToken cancellationToken);
}
