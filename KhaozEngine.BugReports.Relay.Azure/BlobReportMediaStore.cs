using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;

namespace KhaozEngine.BugReports.Relay.Azure;

/// <summary>Blob media storage with separate public and pending containers and conditional creates.</summary>
public sealed class BlobReportMediaStore : IReportMediaStore
{
    readonly IReportBlobContainer publicContainer;
    readonly IReportBlobContainer pendingContainer;

    /// <summary>Wraps existing containers. The host owns credentials, provisioning and public access policy.</summary>
    public BlobReportMediaStore(BlobContainerClient publicContainer, BlobContainerClient pendingContainer)
        : this(new BlobContainerAdapter(publicContainer), new BlobContainerAdapter(pendingContainer)) { }

    internal BlobReportMediaStore(IReportBlobContainer publicContainer, IReportBlobContainer pendingContainer)
    {
        ArgumentNullException.ThrowIfNull(publicContainer);
        ArgumentNullException.ThrowIfNull(pendingContainer);
        this.publicContainer = publicContainer;
        this.pendingContainer = pendingContainer;
    }

    /// <inheritdoc />
    public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
        bool overwrite, CancellationToken cancellationToken)
    {
        IReportBlobContainer container = Container(area, path);
        cancellationToken.ThrowIfCancellationRequested();
        return container.UploadAsync(path, content, headers, overwrite, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
    {
        IReportBlobContainer container = Container(area, path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return container.DownloadAsync(path, maxBytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
    {
        IReportBlobContainer container = Container(area, path);
        cancellationToken.ThrowIfCancellationRequested();
        return container.ExistsAsync(path, cancellationToken);
    }

    IReportBlobContainer Container(ReportArea area, string path)
    {
        IReportBlobContainer container = area switch
        {
            ReportArea.Public => publicContainer,
            ReportArea.Pending => pendingContainer,
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown report area."),
        };
        if (!ReportPaths.IsValid(path))
            throw new ArgumentException("A relative lowercase report path is required.", nameof(path));
        return container;
    }
}
