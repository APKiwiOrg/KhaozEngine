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
/// <b>The ordering is the whole crash-safety property.</b> Every chunk file, the rule chunk and both
/// manifests go to the pack store BEFORE the database transaction, at content-addressed names nothing
/// references yet, so a crash there leaves inert bytes and the old version. The transaction is the only
/// commit point and it is atomic by construction, so a crash at any moment leaves either the old version or
/// the new one and never a torn one.
/// </para>
/// <para>
/// <b>The version pointer is the one file that is not content addressed, so step 9 does not write it.</b>
/// It is handed to step 10, which writes it inside the transaction once the version number is confirmed. Two
/// publishers can prepare the same number from the same base, and a pointer written in step 9 let the one
/// that went on to lose overwrite the committed version's pointer with manifests that never committed.
/// </para>
/// <para>
/// <b>Step 11 runs only after a SUCCESSFUL commit</b>, and it is skipped whenever the store listing fails,
/// which is what stops a bad publish from turning into a lost pack.
/// </para>
/// <para>
/// <b>This type owns the END of step 1's freeze.</b> The transaction consumes the draft on success, and a
/// pre-commit failure releases only this attempt's base through <see cref="IContentDraftFreezeStore"/> when
/// supplied. Cleanup after the sweep must leave a later draft alone. A <see cref="ContentPublisher.PrepareAsync"/>
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
        var freeze = new ContentDraftFreezeRelease(_store, request.ExpectedBaseVersion);
        try
        {
            // A store that implements the text companion publishes through it, row-only work included, so
            // every version it commits is text complete and no text it holds is left behind. A store that
            // does not keeps exactly the route below.
            if (_store is IContentTextAuthoringStore text)
            {
                return await PublishTextAsync(text, request, started, freeze, cancellationToken).ConfigureAwait(false);
            }

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

            // STEP 10. The one transaction, which is the only commit point there is. It writes the version
            // pointer too, and only once it knows this publish is the one that takes the number.
            Step(ContentPublishStep.BeforeCommit);
            ContentVersionRecord record = await _store
                .CommitPublishAsync(plan, request, Pointers, cancellationToken).ConfigureAwait(false);
            freeze.Committed();
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
            // A commit consumed its own draft. A later draft may already be frozen during the sweep, so
            // cleanup is owed only before commit and is scoped to this attempt's base version.
            await ClearTheFreezeAsync(freeze).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The text-bearing publish: the companion freezes the complete draft and its baseline text in one step,
    /// <see cref="ContentTextPublisher"/> prepares the complete plan, <see cref="ContentTextPackWriter"/> puts
    /// text objects before the row chunks, rule chunk and manifests, and the ONE companion commit lands rows,
    /// text, every language mapping and the version together. It never runs the row-only commit and appends
    /// text afterwards. The caller's <c>finally</c> releases the freeze exactly as on the row route.
    /// </summary>
    async Task<ContentPublishResult> PublishTextAsync(
        IContentTextAuthoringStore text,
        ContentPublishRequest request,
        long started,
        ContentDraftFreezeRelease freeze,
        CancellationToken cancellationToken)
    {
        ContentTextPublishSnapshot snapshot = await text
            .FreezeChangesAsync(request.ExpectedBaseVersion, cancellationToken).ConfigureAwait(false);
        ContentTextPublishPlan plan = await new ContentTextPublisher(_publisher)
            .PrepareAsync(request, snapshot, cancellationToken).ConfigureAwait(false);

        // STEP 9. Text objects, then every row object, at names nothing references yet.
        ContentPackWrite write = await ContentTextPackWriter
            .WriteAsync(PackStore, plan, cancellationToken).ConfigureAwait(false);

        // STEP 10. The one companion transaction, which writes the pointer once it holds the number.
        Step(ContentPublishStep.BeforeCommit);
        ContentVersionRecord record = await text
            .CommitTextPublishAsync(plan, request, Pointers, cancellationToken).ConfigureAwait(false);
        freeze.Committed();
        Step(ContentPublishStep.AfterCommit);

        // STEP 11. Only now, and only because the commit succeeded.
        LastSweep = await SweepAsync(cancellationToken).ConfigureAwait(false);

        int textWritten = 0;
        foreach (ContentTextChunkRecord chunk in plan.Chunks)
        {
            textWritten += chunk.IsReused ? 0 : 1;
        }

        ContentPublishPlan rows = plan.RowPlan;
        return new ContentPublishResult(
            record.VersionNumber,
            record.ServerManifestHash,
            record.ClientManifestHash,
            record.FormatGeneration,
            rows.ChunksWritten + textWritten,
            rows.ChunksReused + plan.Chunks.Count - textWritten,
            write.BytesWritten,
            rows.AppendedRules.Count,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>
    /// Releases the draft step 1 froze, with its OWN failure swallowed. Whatever ended the publish is what
    /// the caller needs to read, and replacing it with a failure from the release would lose it. A release
    /// that did not happen is recoverable on its own: the marker names a base version, and the next baseline
    /// read clears one the store no longer stands at.
    /// </summary>
    static async Task ClearTheFreezeAsync(ContentDraftFreezeRelease freeze)
    {
        try
        {
            await freeze.ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception release) when (release is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Step 9. Every chunk file, the rule chunk and both manifest files, in that order, before the database is
    /// touched at all. Every one of them is named by its own hash, which is why two publishers may run this
    /// step at once: they write the same bytes under the same name or different bytes under different names.
    /// <para>
    /// <b>A hash the store already holds is not rewritten.</b> That is checked first, and it is what makes a
    /// republish of an unchanged chunk free: a chunk file's name is a hash of its own contents, so writing one
    /// twice is idempotent by construction and writing one nothing references is inert.
    /// </para>
    /// <para>
    /// <b>The version pointer is not written here.</b> It names a version NUMBER rather than content, and the
    /// number is only decided inside step 10, which writes it. A plan written here and never committed leaves
    /// no pointer at all, which reads as nothing to the sweep because the sweep only lists committed versions.
    /// </para>
    /// </summary>
    /// <param name="plan">A plan that validated.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    /// <para>
    /// <b>It refuses languages it does not represent.</b> This writer puts no text chunk, so the row half of a
    /// text plan, whose new text chunks only the text writer puts, is refused, and so is a plan whose
    /// manifests name a text chunk the store does not already hold. Either would file a manifest naming an
    /// object nobody wrote.
    /// </para>
    /// <exception cref="ContentAuthoringException"><paramref name="plan"/> did not validate, so it has no manifests to write, or it names text this writer does not represent.</exception>
    public async Task<ContentPackWrite> WriteAsync(
        ContentPublishPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid)
        {
            throw Invalid(plan);
        }

        if (plan.FrozenTextState is not null)
        {
            throw ContentTextCompatibility.Unrepresented(nameof(WriteAsync), FormattableString.Invariant(
                $"the plan for version {plan.VersionNumber} is the row half of a text plan, whose text chunks this writer does not put"));
        }

        for (int i = 0; i < plan.Languages.Count; i++)
        {
            ManifestLanguageEntry language = plan.Languages[i];
            if (!await PackStore.ExistsAsync(language.TextHash, cancellationToken).ConfigureAwait(false))
            {
                throw ContentTextCompatibility.Unrepresented(nameof(WriteAsync), FormattableString.Invariant(
                    $"the manifests name text chunk {language.TextHash} for language '{language.Tag}', which the pack store does not hold and this writer does not put"));
            }
        }

        return await WriteRowsAsync(PackStore, plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The scoped row writer: every chunk, the rule chunk and both manifests, put-if-absent, in that order,
    /// with no text guard. <see cref="WriteAsync"/> reaches it after its guard, and the text writer reaches it
    /// only after putting the text objects the manifests name.
    /// </summary>
    /// <param name="store">The pack store.</param>
    /// <param name="plan">A plan that validated.</param>
    /// <param name="cancellationToken">Cancels the writes.</param>
    internal static async Task<ContentPackWrite> WriteRowsAsync(
        IPackStore store,
        ContentPublishPlan plan,
        CancellationToken cancellationToken)
    {
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

            (int wrote, long put) = await PutIfAbsentAsync(store, chunk.Hash, chunk.StoredFile, cancellationToken)
                .ConfigureAwait(false);
            objects += wrote;
            bytes += put;
        }

        (int ruleWrote, long ruleBytes) = await PutIfAbsentAsync(
            store,
            plan.RemapRuleChunkHash,
            ContentRuleChunkCodec.Encode(plan.Rules),
            cancellationToken).ConfigureAwait(false);
        objects += ruleWrote;
        bytes += ruleBytes;

        (int serverWrote, long serverBytes) = await PutIfAbsentAsync(
            store, plan.ServerManifestHash, ContentManifestCodec.Encode(server), cancellationToken).ConfigureAwait(false);
        (int clientWrote, long clientBytes) = await PutIfAbsentAsync(
            store, plan.ClientManifestHash, ContentManifestCodec.Encode(client), cancellationToken).ConfigureAwait(false);
        objects += serverWrote + clientWrote;
        bytes += serverBytes + clientBytes;

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

        return await ContentPackSweep.RunValidatedAsync(PackStore, versions, cancellationToken).ConfigureAwait(false);
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

    void Step(ContentPublishStep step) => _publisher.OnStep?.Invoke(step);

    /// <summary>The refusal a plan the validator rejected meets before anything is written.</summary>
    /// <param name="plan">The refused plan, whose findings the exception carries.</param>
    internal static ContentAuthoringException Invalid(ContentPublishPlan plan)
        => new(
            FormattableString.Invariant(
                $"The candidate for version {plan.VersionNumber} carries {plan.Validation.Findings.Count} finding(s), so nothing was written. A publish that wrote a version the validator refused would be a version a boot then fails closed on."),
            default,
            0,
            ContentAuthoringException.CandidateInvalidReason,
            plan.Validation.Findings);
}
