using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>What step 9 put in the store: how many objects it wrote, and how many bytes they came to.</summary>
/// <param name="ObjectsWritten">Objects actually written, so a republish of an unchanged chunk counts 0.</param>
/// <param name="BytesWritten">Stored bytes written, across chunks, the rule chunk and both manifests.</param>
public sealed record ContentPackWrite(int ObjectsWritten, long BytesWritten);

/// <summary>
/// Steps 9, 10 and 11 of spec 6.1: write the pack, commit the one transaction, then sweep. It is the half of
/// a publish that WRITES, and it is a separate type from the pipeline that prepares one for exactly that
/// reason.
/// <para>
/// <b>The ordering is the whole crash-safety property.</b> Every chunk file, the rule chunk, both manifests
/// and the version pointer go to the pack store BEFORE the database transaction, at content-addressed names
/// nothing references yet, so a crash there leaves inert bytes and the old version. The transaction is the
/// only commit point and it is atomic by construction, so a crash at any moment leaves either the old
/// version or the new one and never a torn one.
/// </para>
/// <para>
/// <b>Step 11 runs only after a SUCCESSFUL commit</b>, and it is skipped whenever the store listing fails,
/// which is what stops a bad publish from turning into a lost pack.
/// </para>
/// <para>
/// <b>This type owns the END of step 1's freeze.</b> The pipeline marks the draft frozen and only a caller
/// that runs all the way through step 11 knows when the publish is over, so the release is a <c>finally</c>
/// here rather than anything the pipeline can do for itself. A <see cref="ContentPublisher.PrepareAsync"/>
/// driven on its own therefore leaves a frozen draft behind, deliberately: half a publish is exactly the
/// state the marker describes, and the next baseline read or the next publish clears it.
/// </para>
/// </summary>
public sealed class ContentPublishCommit
{
    readonly IContentAuthoringStore _store;
    readonly ContentPublisher _publisher;

    /// <summary>Builds the commit half over one store, one pack target and one prepared pipeline.</summary>
    /// <param name="store">The authoring store, which owns the one transaction.</param>
    /// <param name="packStore">The pack store every file is written to, which must carry a pointer half.</param>
    /// <param name="publisher">The pipeline that runs steps 1 to 8.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ContentAuthoringException"><paramref name="packStore"/> cannot write a version pointer, so it is not a publish target.</exception>
    public ContentPublishCommit(
        IContentAuthoringStore store,
        IPackStore packStore,
        ContentPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(packStore);
        ArgumentNullException.ThrowIfNull(publisher);

        _store = store;
        _publisher = publisher;
        PackStore = packStore;
        Pointers = PackVersionPointers.Resolve(packStore)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The pack store {packStore.GetType().Name} writes no version pointer, so a published version could never be enumerated out of it and the sweep could never find it again. A publish target carries both halves."),
                default,
                0,
                ContentAuthoringException.NoPackStoreReason);
    }

    /// <summary>The pack store every file is written to.</summary>
    public IPackStore PackStore { get; }

    /// <summary>The pointer half of <see cref="PackStore"/>, resolved once at construction.</summary>
    public IPackVersionPointerStore Pointers { get; }

    /// <summary>
    /// What the last publish through this instance swept, or null before the first one. A sweep that skipped
    /// carries its reason here, because a publish response that says nothing about the sweep is how a store
    /// quietly stops being pruned.
    /// </summary>
    public ContentPackSweepResult? LastSweep { get; private set; }

    /// <summary>
    /// The whole publish: steps 1 to 8 through the pipeline, then 9, 10 and 11 here.
    /// <para>
    /// <b>An exception thrown AFTER step 10 means the version may already be live.</b> The commit is the only
    /// commit point, and step 11's sweep runs past it, so a throw from the sweep leaves a published version
    /// behind a failed call. A caller reads the active version, or republishes: the same draft against a base
    /// that has moved is refused, which makes the retry idempotent rather than a second version.
    /// </para>
    /// </summary>
    /// <param name="request">The publish request, carrying the required expected base version.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ContentAuthoringException">There is no open draft, the base version moved, or the candidate did not validate.</exception>
    public async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long started = Stopwatch.GetTimestamp();
        try
        {
            ContentPublishBaseline baseline = await _store
                .ReadPublishBaselineAsync(cancellationToken).ConfigureAwait(false);
            ContentPublishPlan plan = await _publisher
                .PrepareAsync(request, baseline, cancellationToken).ConfigureAwait(false);
            if (!plan.IsValid)
            {
                throw Invalid(plan);
            }

            // STEP 9. Every file first, at names nothing references yet.
            ContentPackWrite write = await WriteAsync(plan, cancellationToken).ConfigureAwait(false);

            // STEP 10. The one transaction, which is the only commit point there is.
            Step(ContentPublishStep.BeforeCommit);
            ContentVersionRecord record = await _store
                .CommitPublishAsync(plan, request, cancellationToken).ConfigureAwait(false);
            Step(ContentPublishStep.AfterCommit);

            // STEP 11. Only now, and only because the commit succeeded.
            LastSweep = await SweepAsync(cancellationToken).ConfigureAwait(false);

            return new ContentPublishResult(
                record.VersionNumber,
                record.ServerManifestHash,
                record.ClientManifestHash,
                record.FormatGeneration,
                plan.ChunksWritten,
                plan.ChunksReused,
                write.BytesWritten,
                plan.AppendedRules.Count,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            // The freeze of step 1 ends HERE, on every exit path there is. A commit that ran already took the
            // draft with it and this is a no-op, and any other ending is one where a draft is still sitting
            // there frozen for a publish that is over. CancellationToken.None because a cancelled publish is
            // the case that most needs its draft handed back.
            await ClearTheFreezeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the draft step 1 froze, with its OWN failure swallowed. Whatever ended the publish is what
    /// the caller needs to read, and replacing it with a failure from the release would lose it. A release
    /// that did not happen is recoverable on its own: the marker names a base version, and the next baseline
    /// read clears one the store no longer stands at.
    /// </summary>
    async Task ClearTheFreezeAsync()
    {
        try
        {
            await _store.ClearDraftFreezeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception release) when (release is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Step 9. Every chunk file, the rule chunk, both manifest files and the version POINTER, in that order,
    /// before the database is touched at all.
    /// <para>
    /// <b>A hash the store already holds is not rewritten.</b> That is checked first, and it is what makes a
    /// republish of an unchanged chunk free: a chunk file's name is a hash of its own contents, so writing one
    /// twice is idempotent by construction and writing one nothing references is inert.
    /// </para>
    /// <para>
    /// The pointer goes last of the four, so a crash part way through leaves a version whose pointer is
    /// absent, which reads as a listing failure and SKIPS the next sweep rather than authorising it to delete
    /// on a partial view.
    /// </para>
    /// </summary>
    /// <param name="plan">A plan that validated.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <exception cref="ContentAuthoringException"><paramref name="plan"/> did not validate, so it has no manifests to write.</exception>
    public async Task<ContentPackWrite> WriteAsync(
        ContentPublishPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.ServerManifest is not ContentManifest server || plan.ClientManifest is not ContentManifest client)
        {
            throw Invalid(plan);
        }

        int objects = 0;
        long bytes = 0;

        for (int i = 0; i < plan.Chunks.Count; i++)
        {
            ContentChunkRecord chunk = plan.Chunks[i];
            if (chunk.IsReused)
            {
                // A reused chunk carries no bytes and its file is already in the store, put there by the
                // version that encoded it.
                continue;
            }

            (int wrote, long put) = await PutAsync(chunk.Hash, chunk.StoredFile, cancellationToken)
                .ConfigureAwait(false);
            objects += wrote;
            bytes += put;
        }

        (int ruleWrote, long ruleBytes) = await PutAsync(
            plan.RemapRuleChunkHash,
            ContentRuleChunkCodec.Encode(plan.Rules),
            cancellationToken).ConfigureAwait(false);
        objects += ruleWrote;
        bytes += ruleBytes;

        (int serverWrote, long serverBytes) = await PutAsync(
            plan.ServerManifestHash, ContentManifestCodec.Encode(server), cancellationToken).ConfigureAwait(false);
        (int clientWrote, long clientBytes) = await PutAsync(
            plan.ClientManifestHash, ContentManifestCodec.Encode(client), cancellationToken).ConfigureAwait(false);
        objects += serverWrote + clientWrote;
        bytes += serverBytes + clientBytes;

        await Pointers.PutVersionPointerAsync(
            plan.VersionNumber, plan.ServerManifestHash, plan.ClientManifestHash, cancellationToken)
            .ConfigureAwait(false);

        return new ContentPackWrite(objects, bytes);
    }

    /// <summary>
    /// Step 11 over every version the store knows. Its production caller is <see cref="PublishAsync"/>, one
    /// line after the transaction, and it is public so a TEST can drive step 11 on its own against a commit
    /// it built.
    /// <para>
    /// <b>The operator's sweep is <c>catalog-sweep</c> and it does not come through here.</b> An operator
    /// running the recovery path has no prepared <see cref="ContentPublisher"/>, which this type's
    /// constructor requires, so the admin action reaches <see cref="ContentPackSweep"/> directly. The two are
    /// no longer the same operation either: the operator's refuses while a publish holds the draft frozen,
    /// and records an audit row naming who ran it, neither of which applies to the step inside a publish
    /// that already holds the freeze and is already audited by its own commit.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    public async Task<ContentPackSweepResult> SweepAsync(CancellationToken cancellationToken = default)
    {
        Step(ContentPublishStep.DuringSweep);
        IReadOnlyList<ContentVersionRecord> versions = await _store
            .ListVersionsAsync(cancellationToken).ConfigureAwait(false);
        var numbers = new List<int>(versions.Count);
        for (int i = 0; i < versions.Count; i++)
        {
            numbers.Add(versions[i].VersionNumber);
        }

        return await ContentPackSweep.RunAsync(PackStore, numbers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ONE put-if-absent in this package, shared with <see cref="ContentPackRebuild"/>, which writes the
    /// same four kinds of object in the same order against a store that may already hold some of them. A
    /// second copy of these six lines would be a second place for "a hash the store already holds is not
    /// rewritten" to stop being true.
    /// </summary>
    /// <param name="store">The pack store to write into.</param>
    /// <param name="hash">The object's content address.</param>
    /// <param name="bytes">The file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>1 and the length when the file was written, 0 and 0 when the store already held it.</returns>
    internal static async Task<(int Written, long Bytes)> PutIfAbsentAsync(
        IPackStore store,
        string hash,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        if (await store.ExistsAsync(hash, cancellationToken).ConfigureAwait(false))
        {
            return (0, 0);
        }

        await store.PutAsync(hash, bytes, cancellationToken).ConfigureAwait(false);
        return (1, bytes.Length);
    }

    Task<(int Written, long Bytes)> PutAsync(
        string hash,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
        => PutIfAbsentAsync(PackStore, hash, bytes, cancellationToken);

    void Step(ContentPublishStep step) => _publisher.OnStep?.Invoke(step);

    static ContentAuthoringException Invalid(ContentPublishPlan plan)
        => new(
            FormattableString.Invariant(
                $"The candidate for version {plan.VersionNumber} carries {plan.Validation.Findings.Count} finding(s), so nothing was written. A publish that wrote a version the validator refused would be a version a boot then fails closed on."),
            default,
            0,
            ContentAuthoringException.CandidateInvalidReason,
            plan.Validation.Findings);
}
