using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace KhaozEngine.Catalog.AzureBlob;

/// <summary>
/// The ONLY file that names an Azure SDK type, the store's public constructor apart. Everything the service
/// answers in its own shapes (a status code, an <see cref="ETag"/> condition, a streaming download's
/// declared length) is turned into the seam's plain answers here, so the provider above stays ordinary code.
/// </summary>
internal sealed class BlobContainerAdapter : IBlobContainer
{
    /// <summary>The error code a conditional upload gets when the blob is already there.</summary>
    const string AlreadyExistsCode = "BlobAlreadyExists";

    readonly BlobContainerClient _container;

    /// <summary>Wraps a container client the caller built and owns.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is null.</exception>
    public BlobContainerAdapter(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        Response<bool> response = await _container.GetBlobClient(key)
            .ExistsAsync(cancellationToken)
            .ConfigureAwait(false);
        return response.Value;
    }

    /// <inheritdoc />
    public async Task<bool> UploadIfAbsentAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        BlobObjectHeaders headers,
        CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = headers.ContentType,
                CacheControl = headers.CacheControl,
            },
            // If-None-Match: *, so the service itself decides the race. A read-then-write would have a
            // window between the two calls, and a hash object is immutable, so the loser must not overwrite.
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };

        try
        {
            await _container.GetBlobClient(key)
                .UploadAsync(BinaryData.FromBytes(bytes), options, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException failure) when (AlreadyExists(failure))
        {
            // The object is there, which is the answer the caller wanted. The name is the content, so the
            // writer that won the race wrote these same bytes.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> DownloadAsync(
        string key,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            Response<BlobDownloadStreamingResult> response = await _container.GetBlobClient(key)
                .DownloadStreamingAsync(new BlobDownloadOptions(), cancellationToken)
                .ConfigureAwait(false);
            using BlobDownloadStreamingResult result = response.Value;

            long declared = result.Details.ContentLength;
            if (declared > maximumBytes || declared < 0)
            {
                // Refused from the DECLARED length, before a byte is buffered, which is the one bound a
                // store can apply at all: every other length check in the format runs once the whole body
                // is already in hand.
                return null;
            }

            byte[] exact = new byte[declared];
            await result.Content.ReadExactlyAsync(exact, cancellationToken).ConfigureAwait(false);
            return new ReadOnlyMemory<byte>(exact);
        }
        catch (RequestFailedException failure) when (failure.Status == 404)
        {
            // Absent is NULL rather than a throw, so a sweep or a validation pass is never taken down by
            // one lookup.
            return null;
        }
        catch (EndOfStreamException)
        {
            // A body that arrived short is a body that did not arrive: the caller's next move is the same
            // as for an absent object, which is to retry or to try another source.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Response<bool> response = await _container.GetBlobClient(key)
            .DeleteIfExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListKeysAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (BlobItem item in _container
            .GetBlobsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item.Name;
        }
    }

    static bool AlreadyExists(RequestFailedException failure)
        => failure.Status == 412
        || (failure.Status == 409 && string.Equals(failure.ErrorCode, AlreadyExistsCode, StringComparison.Ordinal));
}
