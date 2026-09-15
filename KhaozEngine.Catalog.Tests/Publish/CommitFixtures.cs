using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// A pack store decorator that counts what was actually WRITTEN, which is how the free republish of an
/// unchanged chunk is pinned: the claim is not that a second publish is fast, it is that the bytes of a
/// chunk nothing touched never reach the store a second time.
/// <para>
/// It carries the pointer and pruning halves as well, because the publish commit resolves its pointer half
/// off the store it was handed and a decorator that dropped them would turn every publish into a store the
/// commit refuses.
/// </para>
/// </summary>
internal sealed class CountingPackStore(FileSystemPackStore inner)
    : IPackStore, IPackStorePruning, IPackVersionPointerStore
{
    readonly List<string> _puts = [];
    readonly List<string> _existsChecks = [];

    /// <summary>Every hash <see cref="PutAsync"/> reached the store with, in call order.</summary>
    public IReadOnlyList<string> Puts => _puts;

    /// <summary>Every hash <see cref="ExistsAsync"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> ExistsChecks => _existsChecks;

    /// <summary>The store behind the decorator, which the tests read files back out of.</summary>
    public FileSystemPackStore Inner => inner;

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
    {
        _existsChecks.Add(hash);
        return inner.ExistsAsync(hash, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        => inner.GetAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _puts.Add(hash);
        return inner.PutAsync(hash, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ListAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken = default)
        => inner.EnumerateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task PutVersionPointerAsync(
        int versionNumber,
        string serverManifestHash,
        string clientManifestHash,
        CancellationToken cancellationToken = default)
        => inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken);

    /// <inheritdoc />
    public Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.GetVersionPointerAsync(versionNumber, cancellationToken);
}

/// <summary>
/// A pack store with no pointer half at all, which is what the publish commit refuses at construction: a
/// store a published version could never be enumerated out of is not a publish target, and saying so before
/// any file is written beats saying it after.
/// </summary>
internal sealed class PointerlessPackStore(IPackStore inner) : IPackStore
{
    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        => inner.GetAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => inner.PutAsync(hash, bytes, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ListAsync(versionNumber, cancellationToken);
}
