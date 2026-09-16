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
/// A pack store that writes pointers and cannot PRUNE, which is a legitimate provider rather than a defect:
/// deleting from a store is only safe where the provider says so, so a read-mostly target simply has no
/// sweep. The publish still commits and the sweep says why it did nothing.
/// </summary>
internal sealed class PrunelessPackStore(FileSystemPackStore inner) : IPackStore, IPackVersionPointerStore
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

/// <summary>
/// A clock that can be made to THROW after a given number of further reads, which is how the in-memory
/// store's commit is driven off a cliff part way through.
/// <para>
/// The clock is the one seam every audit write goes through and the one the version row is stamped from, so
/// arming it for one read through and a failure on the next lands the failure on the audit render: the LAST
/// thing the commit does that can fail, and the one the torn state of
/// https://github.com/APKiwiOrg/KhaozEngine/issues/927 was reached past.
/// </para>
/// </summary>
internal sealed class ArmableClock
{
    int _allowed = -1;

    /// <summary>How many times the clock has been read since it was last armed.</summary>
    public int Reads { get; private set; }

    /// <summary>Lets <paramref name="reads"/> more reads through and throws on the one after.</summary>
    /// <param name="reads">Reads to allow before the failure.</param>
    public void FailAfter(int reads)
    {
        _allowed = reads;
        Reads = 0;
    }

    /// <summary>Disarms, so every read from here answers normally.</summary>
    public void Disarm() => _allowed = -1;

    /// <summary>The clock the store is built over.</summary>
    public DateTimeOffset Read()
    {
        Reads++;
        if (_allowed >= 0 && Reads > _allowed)
        {
            throw new InvalidOperationException(
                "The clock is armed to fail, which stands in for the audit render failing mid commit.");
        }

        return DateTimeOffset.UtcNow;
    }
}
