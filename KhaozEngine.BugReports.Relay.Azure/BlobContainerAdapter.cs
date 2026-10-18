using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace KhaozEngine.BugReports.Relay.Azure;

internal sealed class BlobContainerAdapter : IReportBlobContainer
{
    readonly BlobContainerClient container;

    public BlobContainerAdapter(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.container = container;
    }

    public async Task<bool> UploadAsync(string path, ReadOnlyMemory<byte> bytes, ReportBlobHeaders headers,
        bool overwrite, CancellationToken ct)
    {
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = headers.ContentType, CacheControl = headers.CacheControl },
            Conditions = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All },
        };
        try
        {
            await container.GetBlobClient(path).UploadAsync(BinaryData.FromBytes(bytes), options, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException failure) when (!overwrite && AzureStatus.IsConflict(failure.Status))
        {
            return false;
        }
    }

    public async Task<ReadOnlyMemory<byte>?> DownloadAsync(string path, int maxBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        try
        {
            var response = await container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
            using BlobDownloadStreamingResult result = response.Value;
            long length = result.Details.ContentLength;
            if (length < 0 || length > maxBytes)
                throw new InvalidDataException("The blob's declared length exceeds the requested cap or is invalid.");
            byte[] bytes = new byte[(int)length];
            await result.Content.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch (RequestFailedException failure) when (failure.Status == 404 && failure.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException failure) when (failure.Status == 404 && failure.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return false;
        }
    }
}
