using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// The verifying decorator of spec 8.4: a LOCAL store in front of a REMOTE one. <c>GetAsync</c> asks local,
/// on a miss asks remote, VERIFIES, writes through to local and returns. <c>ExistsAsync</c> asks local then
/// remote. <c>PutAsync</c> goes to local only.
/// <para>
/// <b>The verification is the whole value of the decorator and it is not optional.</b> Bytes that do not
/// digest to the name they were fetched under are discarded, never cached and never returned, which is the
/// entire defence against a poisoned CDN or a corrupted proxy. It is why the cache is keyed by hash rather
/// than by version: an entry that does not hash to its own key is self-evidently wrong and can be discarded
/// with no other information.
/// </para>
/// <para>
/// <b>It verifies on every READ from the cache, not only on write</b> (spec 11 row 7), which is what makes a
/// local file replaced with attacker bytes self-healing rather than permanent. A failed local read is
/// DELETED before the refetch, because a content-addressed <c>PutAsync</c> is a no-op when the name already
/// exists, so a poisoned entry that was not evicted would never be replaced.
/// </para>
/// <para>
/// The decompression that feeds the verify is BOUNDED and the ORDER is load bearing, and both live in
/// <see cref="ContentPackReader.TryVerify"/>: the declared uncompressed length is checked against
/// <see cref="ContentPackFormat.MaxChunkUncompressedBytes"/> and the stored length against the body that
/// arrived BEFORE a buffer is allocated, the buffer is exactly the declared length, and the decompressor
/// refuses the first byte that would overrun it. Only bytes that survive all of that are hashed, and only
/// bytes whose hash matches are cached.
/// </para>
/// </summary>
public sealed class CachingPackStore : IPackStore, IPackStorePruning
{
    readonly Action<string, string>? refused;

    /// <summary>Puts <paramref name="local"/> in front of <paramref name="remote"/>.</summary>
    /// <param name="local">The cache, which is the only half that is ever written or pruned.</param>
    /// <param name="remote">The origin, which is read only as far as this decorator is concerned.</param>
    /// <param name="onRefused">
    /// Called with the content address and the stable reason token every time bytes are discarded, which is
    /// how a fetch loop reports <c>hash-mismatch</c> without this type knowing what a fetch loop is.
    /// </param>
    /// <exception cref="ArgumentNullException">Either store is null.</exception>
    public CachingPackStore(IPackStore local, IPackStore remote, Action<string, string>? onRefused = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        Local = local;
        Remote = remote;
        refused = onRefused;
    }

    /// <summary>The cache, in front.</summary>
    public IPackStore Local { get; }

    /// <summary>The origin, behind.</summary>
    public IPackStore Remote { get; }

    /// <summary>
    /// Whether either side holds it, cache first. It does NOT verify: existence is not integrity, and a
    /// poisoned entry is caught by the read that follows, which is the only place the bytes matter.
    /// </summary>
    public async Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
        => FileSystemPackStore.IsContentAddress(hash)
            && (await Local.ExistsAsync(hash, cancellationToken).ConfigureAwait(false)
                || await Remote.ExistsAsync(hash, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!FileSystemPackStore.IsContentAddress(hash))
        {
            return null;
        }

        ReadOnlyMemory<byte>? cached = await Local.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            if (ContentPackReader.TryVerify(cached.Value.Span, hash, out string? cacheReason))
            {
                return cached;
            }

            Report(hash, cacheReason);
            await EvictAsync(hash, cancellationToken).ConfigureAwait(false);
        }

        ReadOnlyMemory<byte>? fetched = await Remote.GetAsync(hash, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            return null;
        }

        if (!ContentPackReader.TryVerify(fetched.Value.Span, hash, out string? reason))
        {
            Report(hash, reason);
            return null;
        }

        await Local.PutAsync(hash, fetched.Value, cancellationToken).ConfigureAwait(false);
        return fetched;
    }

    /// <summary>
    /// Writes to the CACHE only. A decorator that wrote through to the origin would make every client a
    /// publisher, and the origin's write side belongs to whoever published the version.
    /// </summary>
    public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => Local.PutAsync(hash, bytes, cancellationToken);

    /// <summary>
    /// Lists from the CACHE only. The listing exists for the publish sweep, which runs against the store the
    /// publisher owns and never through a client's cache, and an empty listing is the seam's own "the
    /// listing failed" answer, which is the sweep's skip condition.
    /// </summary>
    public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
        => Local.ListAsync(versionNumber, cancellationToken);

    /// <summary>
    /// Everything the CACHE holds, or nothing when the cache has no pruning half. The origin is never
    /// enumerated through the decorator, so one client's eviction policy can never empty a CDN.
    /// </summary>
    public IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken = default)
        => Local is IPackStorePruning pruning ? pruning.EnumerateAsync(cancellationToken) : Nothing();

    /// <summary>Deletes from the CACHE only, answering false when the cache has no pruning half.</summary>
    public Task<bool> DeleteAsync(string hash, CancellationToken cancellationToken = default)
        => Local is IPackStorePruning pruning
            ? pruning.DeleteAsync(hash, cancellationToken)
            : Task.FromResult(false);

    static async IAsyncEnumerable<string> Nothing()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    async Task EvictAsync(string hash, CancellationToken cancellationToken)
    {
        if (Local is IPackStorePruning pruning)
        {
            await pruning.DeleteAsync(hash, cancellationToken).ConfigureAwait(false);
        }
    }

    void Report(string hash, string? reason)
        => refused?.Invoke(hash, reason ?? ContentPackReader.ReasonHashMismatch);
}
