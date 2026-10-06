using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// One participant's view of the shared pack root, which can park that participant inside a real pack write
/// and records every write it was asked for and whether one was cancelled there. Everything else forwards to
/// the file store. A view built after the catalog was seeded therefore observes nothing of the seed.
/// <para>
/// <b>It declares every half the file store has</b>, the version pointer half and the pruning half included, so
/// wrapping a participant's pack target changes neither how its commit is constructed nor what its sweep does.
/// The file store's pointer half is reached through its own public methods, exactly as
/// <see cref="PackVersionPointers.Resolve"/> reaches it for an unwrapped store.
/// </para>
/// </summary>
/// <param name="inner">The shared pack root.</param>
internal sealed class FreezeRacePackStore(FileSystemPackStore inner)
    : IPackStore, IPackStorePruning, IContentVersionPointerSource, IPackVersionPointerStore
{
    readonly List<string> _writes = [];
    OnceGate? _put;

    /// <summary>
    /// Every write this participant asked for, in order: an object by its hash, a version pointer as
    /// <c>pointer:</c> and its number, a sweep deletion as <c>delete:</c> and its hash. Recorded at entry,
    /// whatever the write answered.
    /// </summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>How many object writes this participant's token cancelled while it was parked in one.</summary>
    public int CancelledPuts { get; private set; }

    /// <summary>Parks this participant at the entry of its next object write, before any byte is written.</summary>
    /// <param name="gate">The gate the write waits on.</param>
    public void ArmPut(OnceGate gate) => _put = gate;

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => inner.ExistsAsync(hash, cancellationToken);

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        => inner.GetAsync(hash, cancellationToken);

    /// <inheritdoc />
    public async Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _writes.Add(hash);
        if (_put is OnceGate gate)
        {
            _put = null;
            try
            {
                await gate.PauseAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancelledPuts++;
                throw;
            }
        }

        await inner.PutAsync(hash, bytes, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => inner.ListAsync(versionNumber, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken = default)
        => inner.EnumerateAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
    {
        _writes.Add("delete:" + hash);
        return inner.DeleteAsync(hash, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutVersionPointerAsync(
        int versionNumber,
        string serverManifestHash,
        string clientManifestHash,
        CancellationToken cancellationToken = default)
    {
        _writes.Add("pointer:" + versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => inner.GetVersionPointerAsync(versionNumber, cancellationToken);
}
