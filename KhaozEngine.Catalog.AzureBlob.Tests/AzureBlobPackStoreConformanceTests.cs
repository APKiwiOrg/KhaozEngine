using KhaozEngine.Catalog;
using KhaozEngine.Catalog.AzureBlob;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The conformance suite over the blob provider, driven through the container seam's in-memory double so
/// the suite needs no account, no network and no credential.
/// </summary>
public sealed class AzureBlobPackStoreConformanceTests : PackStoreConformance
{
    readonly InMemoryBlobContainer _container = new();

    /// <inheritdoc />
    protected override IPackStore CreateStore() => new AzureBlobPackStore(_container);
}
