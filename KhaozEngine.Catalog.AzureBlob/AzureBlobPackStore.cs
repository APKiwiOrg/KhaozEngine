using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;

namespace KhaozEngine.Catalog.AzureBlob;

/// <summary>
/// The WRITE side of a public content origin: a pack store over one Azure Blob Storage container, laying
/// out the same two-level shard key <see cref="HttpPackStore"/> already fetches by, so a server fills the
/// container its clients read over HTTPS and a client needs no change at all.
/// <para>
/// <b>It implements NEITHER pointer interface</b>, deliberately: not <c>IPackVersionPointerStore</c> and not
/// <see cref="IContentVersionPointerSource"/>. That is a RUN time refusal rather than a compile time
/// impossibility: <c>ContentPublishCommit</c> resolves the pointer half in its constructor and
/// <c>ContentPackRebuild</c> inside its <c>RunAsync</c>, and each refuses with
/// <c>ContentAuthoringException.NoPackStoreReason</c> (<c>no-pack-store</c>) before a byte moves, so a full
/// server pack cannot reach a blob container by mistake. <c>ContentPackRebuild.RunAsync</c> also takes an
/// explicit pointer store parameter, so a caller who passes one on purpose is not stopped by any of this.
/// Filling a public origin is a deliberate copy of the CLIENT manifest's closure, which
/// <see cref="ContentOriginFill"/> makes, and it is that copy rather than this type that keeps server-only
/// chunks out of a container anyone can read.
/// </para>
/// <para>
/// <b>A public origin gets no <c>versions/&lt;n&gt;</c> pointer either</b>, and that is the same decision
/// rather than a second one. The pointer format carries the SERVER manifest hash beside the client one
/// (<see cref="PackVersionPointer"/>), and no client ever reads a pointer: a client learns the version and
/// the client manifest hash from the connect door, which is the authenticated channel the whole trust chain
/// hangs on. Publishing the server hash into a public container would give away a name for nothing.
/// Consequently <see cref="ListAsync"/> yields NOTHING, which is the publish sweep's own skip condition, so
/// nothing here can ever authorize a delete.
/// </para>
/// <para>
/// Hash objects are IMMUTABLE. A put is a conditional upload rather than a check and a write, so two
/// publisher hosts putting the same chunk is the ordinary case rather than a race, and an object is never
/// rewritten once it exists.
/// </para>
/// </summary>
public sealed class AzureBlobPackStore : IPackStore, IPackStorePruning
{
    /// <summary>The content type every hash object is stored with: the bytes are a pack file, not text.</summary>
    public const string ObjectContentType = "application/octet-stream";

    /// <summary>
    /// The cache directive every hash object is stored with. A year and <c>immutable</c> are safe PRECISELY
    /// because the name is the content: an object under a content address never changes, so a client and
    /// every cache in front of it may keep it until it is evicted, and a republish writes a new name.
    /// </summary>
    public const string ObjectCacheControl = "public, max-age=31536000, immutable";

    static readonly BlobObjectHeaders Headers = new(ObjectContentType, ObjectCacheControl);

    readonly IBlobContainer _container;

    /// <summary>
    /// Points the store at a container the CALLER built and owns.
    /// <para>
    /// The client arrives ready, which is why this package takes no identity dependency: a host builds the
    /// container client with whatever credential it already has (a managed identity, a workload identity, a
    /// shared-access signature), and the engine never chooses one for it. The identity needs
    /// <c>Storage Blob Data Contributor</c> scoped to this container and nothing wider.
    /// </para>
    /// </summary>
    /// <param name="container">The container every object is written into and read from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is null.</exception>
    public AzureBlobPackStore(BlobContainerClient container)
        : this(new BlobContainerAdapter(container))
    {
    }

    /// <summary>The same store over the container seam, which is what the tests drive.</summary>
    internal AzureBlobPackStore(IBlobContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => FileSystemPackStore.IsContentAddress(hash)
            ? _container.ExistsAsync(FileSystemPackStore.RelativeKeyFor(hash), cancellationToken)
            : Task.FromResult(false);

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        // A name that is not a content address never becomes a key, because a hash arrives from a manifest a
        // remote peer may have written and a name that is not an address must never become a path segment.
        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            return Task.FromResult<ReadOnlyMemory<byte>?>(null);
        }

        // The format owns the ceiling because the client reading this container applies exactly that number
        // and a writer's store that would hand back more is a store the client could not read.
        return _container.DownloadAsync(
            FileSystemPackStore.RelativeKeyFor(hash),
            ContentPackFormat.MaxObjectBytes,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task PutAsync(
        string hash,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            throw new ContentPackException(
                FormattableString.Invariant(
                    $"'{hash}' is not a content address, and a store that files bytes under a name that is not their digest is not content addressed."),
                hash,
                ContentPackReader.ReasonHashMismatch);
        }

        if (!ContentPackReader.TryVerify(bytes.Span, hash, out string? reason))
        {
            throw new ContentPackException(
                FormattableString.Invariant(
                    $"The {bytes.Length} bytes offered under '{hash}' do not digest to it ({reason}). Every later verify-on-read is meaningless if this one is skipped."),
                hash,
                reason);
        }

        // Conditional, so there is no read-then-write window: the object being there already is the answer
        // "another writer wrote these same bytes", which is an idempotent success and not an error.
        await _container
            .UploadIfAbsentAsync(FileSystemPackStore.RelativeKeyFor(hash), bytes, Headers, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Yields NOTHING, always. A public origin holds hash objects and no version pointer, so there is
    /// nothing for a listing to answer from, and an empty sequence is the same answer a store whose pointer
    /// is unreadable gives: the publish sweep's skip condition, which deletes nothing.
    /// </summary>
    /// <param name="versionNumber">Ignored, because no version is described here.</param>
    /// <param name="cancellationToken">Ignored, because nothing is read.</param>
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => Nothing();

    /// <inheritdoc />
    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string key in _container.ListKeysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (HashOf(key) is string hash)
            {
                yield return hash;
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        => FileSystemPackStore.IsContentAddress(hash)
            ? _container.DeleteAsync(FileSystemPackStore.RelativeKeyFor(hash), cancellationToken)
            : Task.FromResult(false);

    /// <summary>
    /// The hash one key names, or null when the key is not a hash object at all. A container may hold
    /// anything a host put in it, so the walk keeps only what the store itself would have written, which
    /// means the key has to be the CANONICAL one for the hash and not merely end in it. A
    /// <c>junk/&lt;hash&gt;.kec</c> yielded here would be enumerated under an address whose real object is a
    /// different blob, and a prune driven off the enumeration would then delete the canonical one.
    /// </summary>
    static string? HashOf(string key)
    {
        if (!key.EndsWith(FileSystemPackStore.FileExtension, StringComparison.Ordinal))
        {
            return null;
        }

        ReadOnlySpan<char> name = key.AsSpan(0, key.Length - FileSystemPackStore.FileExtension.Length);
        int slash = name.LastIndexOf('/');
        string stem = name[(slash + 1)..].ToString();
        return FileSystemPackStore.IsContentAddress(stem)
            && string.Equals(key, FileSystemPackStore.RelativeKeyFor(stem), StringComparison.Ordinal)
                ? stem
                : null;
    }

    static async IAsyncEnumerable<string> Nothing()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
