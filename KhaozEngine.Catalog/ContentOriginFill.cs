using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog;

/// <summary>
/// What one origin fill did. A refusal is a RESULT here rather than a throw, exactly as it is on
/// <see cref="ContentFetchResult"/>, because a fill runs on a server boot path where the useful answer is a
/// reason token an operator can read rather than a stack.
/// </summary>
/// <param name="Filled">True only when the origin now holds the whole client closure, its manifest included.</param>
/// <param name="Version">The version the fill was asked for, number and CLIENT manifest hash.</param>
/// <param name="ObjectsRequired">
/// The client manifest plus every hash it names, which is what the origin has to hold. It is 1 when the
/// manifest itself is what failed, because a manifest that did not read names nothing, and 0 when a write
/// into the origin faulted mid walk, because the count is the fetch loop's answer and the loop was stopped
/// before it gave one.
/// </param>
/// <param name="ObjectsWritten">
/// How many objects this call actually wrote into the origin, counted at the write rather than inferred: 0
/// on a second fill of a version the origin already holds, and 0 on a refusal that never reached a chunk.
/// </param>
/// <param name="RefusalReason">The stable reason token the fetch refused with, or null on a filled origin.</param>
/// <param name="RefusalHash">The address the fill stopped at, or null on a filled origin.</param>
/// <param name="RefusalDetail">
/// What the refusal knows beyond its token, or null when the token says everything. Today that is the
/// message of the fault an origin's write threw (<see cref="ContentOriginFill.RefusedOriginWrite"/>), which
/// is the one refusal whose cause is a foreign library's and cannot be a token of ours.
/// </param>
public sealed record ContentOriginFillResult(
    bool Filled,
    ContentVersionIdentity Version,
    int ObjectsRequired,
    int ObjectsWritten,
    string? RefusalReason,
    string? RefusalHash,
    string? RefusalDetail = null);

/// <summary>
/// A game server is the PRODUCER of its clients' content origin. Before it opens a socket it makes sure the
/// origin holds the active version's CLIENT closure, so a version published through the admin console reaches
/// clients at the restart that activates it.
/// <para>
/// This is <see cref="ContentFetchLoop"/> pointed the other way round and nothing else: the destination is the
/// ORIGIN and the source is the server's own pack store. A second walker over the same manifest would be a
/// second place for the client closure to be computed differently, which is the one difference no test on
/// either side would catch until a chunk went missing in production.
/// </para>
/// <para>
/// <b>It writes NO version pointer.</b> The <c>versions/&lt;n&gt;</c> pointer carries the SERVER manifest hash
/// (see <see cref="PackVersionPointer"/>), an origin is public, and a client never reads a pointer anyway: it
/// learns its version and its client manifest hash from the connect door, which is the authenticated channel
/// the whole trust chain hangs on. So there is nothing for a pointer to do here except leak.
/// </para>
/// <para>
/// <b>It never prunes.</b> A caller that wants stale objects removed does it itself through
/// <see cref="IPackStorePruning"/>, and a hosted origin should think twice before it does: a client may be part
/// way through downloading the PREVIOUS version, and a chunk deleted under it turns an update into a refusal
/// at the door. For the same reason a fill trusts what the origin says it holds, so an object that went bad in
/// the origin is repaired by deleting it and filling again rather than by a second fill.
/// </para>
/// </summary>
public static class ContentOriginFill
{
    /// <summary>
    /// A write into the origin faulted, which is the one failure here that is neither the fetch's nor an
    /// argument's. A read can answer absent and a write cannot, so the store's own contract has no room for
    /// it, and a server boot wants a reason token and an exit code rather than a stack out of a fill.
    /// </summary>
    public const string RefusedOriginWrite = "origin-write-failed";

    /// <summary>
    /// Copies the version's client closure from the server's own store into the origin, verified on the way
    /// through, and writes the manifest LAST so a fill that stopped leaves nothing a client could follow into
    /// a hole.
    /// <para>
    /// <paramref name="clientVersion"/> MUST carry the CLIENT manifest hash. Nothing can tell a client
    /// manifest from a server one by hash alone, so the manifest is read as a client manifest and the file's
    /// own <c>side</c> byte refuses the other one (<see cref="ContentManifestCodec.ReasonWrongSide"/>), before
    /// any object is written. Handing the server hash here would otherwise publish every server only chunk of
    /// the version.
    /// </para>
    /// <para>
    /// A fault WRITING the origin is a refusal too (<see cref="RefusedOriginWrite"/>), carrying the address it
    /// stopped at and the fault's own message in <see cref="ContentOriginFillResult.RefusalDetail"/>. A read
    /// answers absent for a failure and a write has no such answer, so an expired signature or a throttled
    /// container arrives here as an exception, and a boot path wants one line rather than a stack.
    /// </para>
    /// </summary>
    /// <param name="source">The server's own pack store, which is read and never written.</param>
    /// <param name="origin">The clients' origin: a directory in dev, a public blob container when hosted.</param>
    /// <param name="clientVersion">The version to fill for, number and CLIENT manifest hash.</param>
    /// <param name="registry">This build's content type registry, which binds the manifest's types to codecs.</param>
    /// <param name="concurrency">How many objects are copied at once, both stores being the server's own.</param>
    /// <param name="cancellationToken">Cancels the fill.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="clientVersion"/> carries no manifest hash.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrency"/> is not positive.</exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled, which is the ONE throwing exit on a surface whose every other failure is a
    /// result. Nothing is left half done by it: the objects that completed are content addressed and the
    /// manifest is written last, so a cancelled fill leaves an origin the next fill completes.
    /// </exception>
    public static async Task<ContentOriginFillResult> RunAsync(
        IPackStore source,
        IPackStore origin,
        ContentVersionIdentity clientVersion,
        ContentTypeRegistry registry,
        int concurrency = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);

        if (string.IsNullOrEmpty(clientVersion.ManifestHash))
        {
            throw new ArgumentException(
                "A fill needs the version's CLIENT manifest hash. A version number alone names no bytes.",
                nameof(clientVersion));
        }

        var destination = new OriginWriteView(origin, clientVersion.ManifestHash);
        var loop = new ContentFetchLoop(destination, source, registry, new ContentFetchOptions
        {
            // A SERVER is not a client, so the client build gate must not stop it. A version that raises
            // MinimumClientBuild is exactly the version whose closure has to reach the origin, and the gate
            // that matters is the connect door's, which enforces the floor on the clients themselves.
            ClientBuild = int.MaxValue,

            // ONE attempt and no backoff: both stores are the server's own, so a failure here is a fault to
            // report rather than a flaky network to wait out.
            Attempts = 1,
            BackoffBase = TimeSpan.Zero,
            Concurrency = concurrency,

            // Every language the version ships. Which languages a PLAYER wants is the player's choice, and an
            // origin that held only some of them would make that choice for everyone.
            Languages = [],
        });

        ContentFetchResult fetched;
        try
        {
            fetched = await loop.FetchAsync(clientVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (Refusable(failure, destination, cancellationToken))
        {
            // The required count is the LOOP's answer and the loop did not return one, so it is 0 here, the
            // same shape as the 1 a fill reports when the manifest itself is what failed.
            return Refused(clientVersion, 0, destination);
        }

        int required = fetched.Progress.ChunksRequired + 1;
        if (!fetched.Success)
        {
            return new ContentOriginFillResult(
                false,
                clientVersion,
                required,
                destination.Written,
                fetched.Reason,
                fetched.Hash);
        }

        try
        {
            await destination.CommitManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (Refusable(failure, destination, cancellationToken))
        {
            // Every chunk landed and the manifest did not, which is exactly the state a stopped fill is meant
            // to leave: content addressed objects and nothing a client could follow into a hole.
            return Refused(clientVersion, required, destination);
        }

        return new ContentOriginFillResult(true, clientVersion, required, destination.Written, null, null);
    }

    /// <summary>
    /// Whether an exception the fill caught is the origin WRITE fault the view recorded, which is a refusal
    /// rather than a throw.
    /// <para>
    /// The RECORDED fault decides rather than the exception's own type, because the parallel walk cancels its
    /// remaining workers the moment one of them throws, so what the await surfaces may be one of those
    /// cancellations rather than the fault that caused them. A cancellation the CALLER asked for is never
    /// absorbed, which is what the token is read for, and neither is anything the view would not record.
    /// </para>
    /// </summary>
    static bool Refusable(Exception failure, OriginWriteView destination, CancellationToken cancellationToken)
        => destination.Fault is not null
            && !cancellationToken.IsCancellationRequested
            && (failure is OperationCanceledException || OriginWriteView.IsWriteFault(failure));

    /// <summary>The refusal one recorded write fault makes, which carries the address and the fault's message.</summary>
    static ContentOriginFillResult Refused(
        ContentVersionIdentity clientVersion,
        int required,
        OriginWriteView destination)
    {
        OriginWriteFault fault = destination.Fault!;
        return new ContentOriginFillResult(
            false,
            clientVersion,
            required,
            destination.Written,
            RefusedOriginWrite,
            fault.Hash,
            fault.Detail);
    }

    /// <summary>The first write into the origin that faulted: what it was writing, and what the fault said.</summary>
    /// <param name="Hash">The address the write was for.</param>
    /// <param name="Detail">The fault's message, which belongs to whatever library the origin is built on.</param>
    sealed record OriginWriteFault(string Hash, string Detail);

    /// <summary>
    /// The origin as the fetch loop's local half, with two jobs the origin itself has no business having.
    /// <para>
    /// It HOLDS BACK the manifest. The loop reads the manifest first and writes it through the caching pair
    /// before it fetches a single chunk, which for a client cache is fine and for an origin is not: a fill
    /// that then stopped would leave a public manifest naming objects that are not there. So the manifest's
    /// bytes are buffered here and written by <see cref="CommitManifestAsync"/> once every chunk landed, which
    /// keeps "nothing followable on failure" without a second walk over the manifest.
    /// </para>
    /// <para>
    /// It also COUNTS the writes, which is the honest way to answer how many objects a fill wrote:
    /// <see cref="ContentFetchProgress"/> counts what arrived over the wire rather than what this store filed,
    /// and the manifest is outside its required set entirely.
    /// </para>
    /// <para>
    /// It deliberately does NOT carry the <see cref="IPackStorePruning"/> half the origin may have, so the
    /// caching pair cannot evict from a public origin on a verify failure of its own.
    /// </para>
    /// </summary>
    sealed class OriginWriteView(IPackStore origin, string manifestHash) : IPackStore
    {
        ReadOnlyMemory<byte>? manifest;
        int written;
        OriginWriteFault? fault;

        /// <summary>
        /// Whether a fault out of the origin's write is one this view RECORDS and the fill turns into a
        /// refusal. The kinds cannot be listed the way a catch over one library's own would, because the
        /// origin is whatever store the caller handed in and its fault is that library's. What CAN be listed
        /// is what must never be absorbed: a cancellation is the caller's own decision and the one throwing
        /// exit this surface has, a <see cref="ContentPackException"/> means bytes that did not digest to the
        /// name they were offered under, which is a broken invariant rather than a fault of the origin's, and
        /// the fatal kinds are nobody's to swallow.
        /// </summary>
        public static bool IsWriteFault(Exception failure)
            => failure is not OperationCanceledException
                and not ContentPackException
                and not OutOfMemoryException;

        /// <summary>How many objects this view actually wrote into the origin.</summary>
        public int Written => Volatile.Read(ref written);

        /// <summary>The FIRST write that faulted, or null while every write this view made went through.</summary>
        public OriginWriteFault? Fault => Volatile.Read(ref fault);

        /// <inheritdoc />
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => origin.ExistsAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => origin.GetAsync(hash, cancellationToken);

        /// <inheritdoc />
        public async Task PutAsync(
            string hash,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(hash, manifestHash, StringComparison.Ordinal))
            {
                // A copy, because the buffer outlives the call that offered it.
                manifest = bytes.ToArray();
                return;
            }

            await WriteAsync(hash, bytes, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => origin.ListAsync(versionNumber, cancellationToken);

        /// <summary>
        /// Writes the manifest that was held back, which is the last write of a fill and the one that makes
        /// the version followable. Nothing to do when the origin already held it, which is the ordinary second
        /// fill.
        /// </summary>
        public async Task CommitManifestAsync(CancellationToken cancellationToken)
        {
            if (manifest is not ReadOnlyMemory<byte> bytes)
            {
                return;
            }

            await WriteAsync(manifestHash, bytes, cancellationToken).ConfigureAwait(false);
            manifest = null;
        }

        /// <summary>
        /// One write into the origin, counted when it went through and RECORDED when it faulted. The fault is
        /// rethrown rather than swallowed: the loop would otherwise carry on believing the object landed, and
        /// a container that refused one write refuses the next one too, so stopping is both the honest answer
        /// and the kind one to a throttled origin. The FIRST fault is the one kept, because it is the one
        /// with a cause, and the workers that follow it are just the same container saying the same thing.
        /// </summary>
        async Task WriteAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            try
            {
                await origin.PutAsync(hash, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (IsWriteFault(failure))
            {
                Interlocked.CompareExchange(ref fault, new OriginWriteFault(hash, failure.Message), null);
                throw;
            }

            Interlocked.Increment(ref written);
        }
    }
}
