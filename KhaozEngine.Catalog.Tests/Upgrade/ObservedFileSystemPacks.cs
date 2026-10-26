using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Observes real filesystem pruning without losing pointer or deletion capabilities.</summary>
internal sealed class ObservedFileSystemPacks(FileSystemPackStore inner)
    : IPackStore, IPackStorePruning, IPackVersionPointerStore, IContentVersionPointerSource
{
    public Action? BeforeRead { get; set; }
    public Func<CancellationToken, Task>? BeforeEnumeration { get; set; }
    public OperationCanceledException? EnumerationCancellation { get; private set; }
    public int Enumerations { get; private set; }
    public int Deletions { get; private set; }

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("filesystem pack exists", () => inner.ExistsAsync(hash, cancellationToken));

    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("filesystem pack read", () =>
        {
            BeforeRead?.Invoke();
            return inner.GetAsync(hash, cancellationToken);
        });

    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("filesystem pack write", () => inner.PutAsync(hash, bytes, cancellationToken));

    public async IAsyncEnumerable<string> ListAsync(int versionNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using IAsyncEnumerator<string> hashes = inner.ListAsync(versionNumber, cancellationToken).GetAsyncEnumerator();
        while (await CatalogPhaseWatch.ObserveAsync("filesystem version listing", () => hashes.MoveNextAsync().AsTask()))
        {
            yield return hashes.Current;
        }
    }

    public Task PutVersionPointerAsync(int versionNumber, string serverManifestHash, string clientManifestHash,
        CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("filesystem pointer write",
            () => inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken));

    public Task<PackVersionPointer?> GetVersionPointerAsync(int versionNumber, CancellationToken cancellationToken = default)
        => CatalogPhaseWatch.ObserveAsync("filesystem pointer read", () => inner.GetVersionPointerAsync(versionNumber, cancellationToken));

    public async Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
    {
        bool deleted = await CatalogPhaseWatch.ObserveAsync("filesystem prune delete", () => inner.DeleteAsync(hash, cancellationToken));
        Deletions += deleted ? 1 : 0;
        return deleted;
    }

    public async IAsyncEnumerable<string> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CatalogPhaseWatch.Enter("filesystem pruning enumeration entry");
        Enumerations++;
        if (BeforeEnumeration is not null)
        {
            await BeforeEnumeration(cancellationToken);
        }

        await using IAsyncEnumerator<string> files = inner.EnumerateAsync(cancellationToken).GetAsyncEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = await CatalogPhaseWatch.ObserveAsync("filesystem pruning enumeration", () => files.MoveNextAsync().AsTask());
            }
            catch (OperationCanceledException failure)
            {
                EnumerationCancellation = failure;
                throw;
            }

            if (!moved)
            {
                yield break;
            }

            yield return files.Current;
        }
    }
}
