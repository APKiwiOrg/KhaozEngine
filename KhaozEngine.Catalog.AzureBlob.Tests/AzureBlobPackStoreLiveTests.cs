using System;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Blobs;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.AzureBlob;
using Xunit;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The gated leg against a REAL container, which is the only place the adapter's own behaviour is exercised:
/// the conditional upload's status codes, the headers the service actually stored, and a delete that answers
/// whether the object was there. Everything else runs against the seam's in-memory double.
/// <para>
/// It builds its own client, which is the whole point of the package taking one: the shipped package has no
/// Azure.Identity dependency, so the credential is the HOST's decision. This leg makes the same decision a
/// host would, from the URL it was given: a shared-access signature in the query authenticates itself, and a
/// bare container URL gets the ambient identity.
/// </para>
/// </summary>
public class AzureBlobPackStoreLiveTests
{
    [AzureBlobPackStoreFact]
    public async Task A_live_container_round_trips_a_chunk_and_a_delete()
    {
        var container = new Uri(Environment.GetEnvironmentVariable(AzureBlobPackStoreFactAttribute.EnvironmentVariable)!);
        BlobContainerClient client = string.IsNullOrEmpty(container.Query)
            ? new BlobContainerClient(container, new DefaultAzureCredential())
            : new BlobContainerClient(container);
        var store = new AzureBlobPackStore(client);
        StoredObject english = CatalogChunks.English();

        try
        {
            await store.PutAsync(english.Hash, english.File);

            Assert.True(await store.ExistsAsync(english.Hash));
            ReadOnlyMemory<byte>? read = await store.GetAsync(english.Hash);
            Assert.NotNull(read);
            Assert.True(english.File.AsSpan().SequenceEqual(read.Value.Span));

            // The second put meets the object it wrote, which is the conditional upload's 409 or 412 turning
            // into the idempotent no-op the seam promises.
            await store.PutAsync(english.Hash, english.File);
        }
        finally
        {
            await ((IPackStorePruning)store).DeleteAsync(english.Hash);
        }

        Assert.False(await store.ExistsAsync(english.Hash));
        Assert.False(await ((IPackStorePruning)store).DeleteAsync(english.Hash));
    }
}
