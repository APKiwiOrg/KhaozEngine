using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The version POINTER half of a publish target (spec 6.9). It is the one object in a pack store that is not
/// named by its own hash: it sits at <c>versions/&lt;n&gt;</c>, it holds that version's two manifest hashes
/// and nothing else, and it is the only way a content-addressed store can answer "what does version 47
/// contain" once the authoring database is out of reach, which is exactly where the sweep of spec 6.12 runs.
/// <para>
/// <b>It is a SEPARATE interface rather than two more members on the read side's store seam</b>, for the
/// same reason pruning is: a read-only provider cannot be a half-working publish target, and that is a
/// compile-time fact rather than a runtime throw. A publisher resolves one through
/// <see cref="PackVersionPointers.Resolve"/> and refuses a store that has none.
/// </para>
/// <para>
/// A client NEVER reads a pointer. A client learns its manifest hash from the connect door, which is the
/// authenticated channel the whole trust chain hangs on, so a mutable name in the store is never in the
/// integrity path and a pointer an attacker rewrote costs the publisher's own sweep and nothing else.
/// </para>
/// </summary>
public interface IPackVersionPointerStore
{
    /// <summary>
    /// Writes one version's pointer. It is written at step 9, BEFORE the database commit, so a crash between
    /// the two leaves a pointer for a version that never committed. That keeps a set of orphan files ALIVE
    /// rather than deleting live ones, which is the safe direction, and it self repairs: version numbers are
    /// never skipped, so the retried publish takes the same number and overwrites the pointer with its own.
    /// </summary>
    /// <param name="versionNumber">The version the pointer is for, from 1.</param>
    /// <param name="serverManifestHash">The version's server manifest hash, lower hex.</param>
    /// <param name="clientManifestHash">The version's client manifest hash, lower hex.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task PutVersionPointerAsync(
        int versionNumber,
        string serverManifestHash,
        string clientManifestHash,
        CancellationToken cancellationToken = default);

    /// <summary>The pointer, or NULL when it is absent or is not two content addresses.</summary>
    /// <param name="versionNumber">The version to read the pointer of.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<PackVersionPointer?> GetVersionPointerAsync(
        int versionNumber,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// How a publish finds the pointer half of the store it was handed.
/// <para>
/// <b>The read side declares no pointer member on its store seam and this package does not add one to it.</b>
/// A provider that writes pointers either implements <see cref="IPackVersionPointerStore"/> or is the
/// filesystem provider, whose pointer members this adapter names directly. A store that is neither cannot be
/// published to, and the refusal says so at the top of the publish rather than after the chunk files are
/// already written.
/// </para>
/// </summary>
public static class PackVersionPointers
{
    /// <summary>
    /// The pointer half of one store, or null when it has none.
    /// </summary>
    /// <param name="store">The pack store a publish writes to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static IPackVersionPointerStore? Resolve(IPackStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store switch
        {
            IPackVersionPointerStore pointers => pointers,
            FileSystemPackStore local => new FileSystemPointers(local),
            _ => null,
        };
    }

    /// <summary>
    /// The filesystem provider's own pointer members behind the interface. Its two methods already carry
    /// these signatures, so this adapter is a name and nothing else, and the day that provider declares the
    /// interface itself the first arm of the switch above takes over with no change here.
    /// </summary>
    sealed class FileSystemPointers(FileSystemPackStore store) : IPackVersionPointerStore
    {
        public Task PutVersionPointerAsync(
            int versionNumber,
            string serverManifestHash,
            string clientManifestHash,
            CancellationToken cancellationToken = default)
            => store.PutVersionPointerAsync(
                versionNumber, serverManifestHash, clientManifestHash, cancellationToken);

        public Task<PackVersionPointer?> GetVersionPointerAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
            => store.GetVersionPointerAsync(versionNumber, cancellationToken);
    }
}
