using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The publish pipeline of spec 6, in order, with each step delegating to its named type. This file holds
/// the ORDER and nothing else, so adding work to a step never grows it.
/// <para>
/// <b>Steps 1 to 8 write nothing durable.</b> Step 9 writes files nothing references, step 10 is the one
/// commit and step 11 is a sweep after it, and all three belong to <c>ContentPublishCommit</c>. The
/// invariant the whole section is built around is one sentence, a crash at any point leaves either the old
/// version or the new one and never a torn one, and it holds because nothing observable changes until step
/// 10 and step 10 is a single transaction.
/// </para>
/// <para>
/// <b>The base version arrives on the BASELINE rather than being read here.</b> The commit reads the base
/// under the row lock step 1 takes and hands it down, so the version the candidate was built against and the
/// version the transaction commits against cannot differ. Reading it here and using it there is exactly the
/// race the lock is meant to close, which is also why the new version NUMBER is confirmed inside that
/// transaction: this pipeline computes at <c>baseline.VersionNumber + 1</c> and the commit refuses if the
/// base moved underneath it.
/// </para>
/// </summary>
public sealed class ContentPublisher
{
    readonly IContentAuthoringStore _store;
    readonly IContentIdPersistence _idPersistence;
    readonly ContentIdAllocator _allocator;
    readonly ContentTypeRegistry _registry;
    readonly IContentRowSideEncoder _rowEncoder;

    /// <summary>Builds a publisher over one store, one id seam and one registry.</summary>
    /// <param name="store">The store the frozen draft is read from.</param>
    /// <param name="idPersistence">The durable id half, which step 3 allocates and seeds through.</param>
    /// <param name="registry">The registry the candidate's types are declared in. Per instance, never ambient.</param>
    /// <param name="onStep">The hook a crash test throws from, or null.</param>
    /// <param name="rowEncoder">The side encoder, or null for <see cref="ContentSideRowEncoder.Default"/>.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ContentPublisher(
        IContentAuthoringStore store,
        IContentIdPersistence idPersistence,
        ContentTypeRegistry registry,
        Action<ContentPublishStep>? onStep = null,
        IContentRowSideEncoder? rowEncoder = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(idPersistence);
        ArgumentNullException.ThrowIfNull(registry);

        _store = store;
        _idPersistence = idPersistence;
        _allocator = new ContentIdAllocator(idPersistence);
        _registry = registry;
        _rowEncoder = rowEncoder ?? ContentSideRowEncoder.Default;
        OnStep = onStep;
    }

    /// <summary>
    /// The hook the crash tests of spec 6.11 throw from, called at each point of
    /// <see cref="ContentPublishStep"/> this pipeline reaches. Null on an ordinary publisher.
    /// </summary>
    public Action<ContentPublishStep>? OnStep { get; }

    /// <summary>
    /// Runs steps 1 to 8 and hands back everything the commit needs.
    /// </summary>
    /// <param name="request">The publish request, carrying the required expected base version.</param>
    /// <param name="baseline">The base version, read under the lock step 1 took.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ContentAuthoringException">No draft is open, the base version moved, or an edit names something the registry or the base version does not carry.</exception>
    public async Task<ContentPublishPlan> PrepareAsync(
        ContentPublishRequest request,
        ContentPublishBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(baseline);

        // STEP 1. Freeze the draft. The store holds the row lock for the whole publish, so the change set
        // read here is the one the commit will delete.
        ContentDraft draft = await FreezeAsync(request, baseline, cancellationToken).ConfigureAwait(false);
        int version = baseline.VersionNumber + 1;

        // KEC0041, the fork preconditions, BEFORE the candidate is built. A fork of a row that is not there
        // cannot be applied at all, so the refusal has to come from the edit rather than from the middle of
        // the walk that applies it.
        IReadOnlyList<ContentFinding> forkFindings =
            ContentForkChecks.Check(baseline, draft.Changes, _registry);
        if (forkFindings.Count > 0)
        {
            return Refused(version, baseline, Empty(version), new ContentValidationReport(false, forkFindings));
        }

        // STEP 2. Build the candidate by applying the draft's edits to the base version. Nothing walks a row
        // no edit names, which is what makes step 6 cheap.
        (List<ContentCandidateRow> rows, List<ContentPendingRule> pending) =
            ContentCandidateBuilder.Build(baseline, draft.Changes, _registry, version);

        // STEP 3. Allocate, BEFORE validation, because KEC0006 and KEC0010 need the ids the new rows carry.
        Step(ContentPublishStep.BeforeIdAllocation);
        ContentIdAllocationRecord allocation = await ContentIdAllocation
            .RunAsync(rows, _allocator, _idPersistence, cancellationToken).ConfigureAwait(false);
        Step(ContentPublishStep.AfterIdAllocation);

        IReadOnlyList<RemapRule> appended = Materialize(pending, baseline, version);
        IReadOnlyList<RemapRule> rules = Concat(baseline.Rules, appended);
        ContentSnapshot candidate = Materialize(rows, rules, version);

        // STEP 4. Validate the candidate plus the rule set as it will stand, with previous NON-NULL and this
        // the only place it is. An invalid candidate aborts and the console gets every finding at once.
        ContentSnapshot? previous = baseline.IsEmpty ? null : Materialize(baseline, rules);
        ContentValidationReport swept = ContentValidator.Validate(candidate, previous, rules, _registry);

        // STEP 5. The temporal rows, which the walk of step 2 already decided.
        (IReadOnlyList<ContentRowClose> closes, IReadOnlyList<ContentRowInsert> inserts, IReadOnlyList<ContentRowRevision> live) =
            Temporal(rows, version);

        string ruleHash = ContentRuleChunkCodec.Hash(rules);
        int minimumServerBuild = request.MinimumServerBuild ?? baseline.MinimumServerBuild;
        int minimumClientBuild = request.MinimumClientBuild ?? baseline.MinimumClientBuild;

        if (!swept.IsValid)
        {
            return Refused(
                version, baseline, candidate, swept, allocation, closes, inserts, live, appended, rules,
                [], ruleHash, minimumServerBuild, minimumClientBuild);
        }

        // STEPS 6 AND 7. Select the affected chunks, then encode, compress and hash each one at every side
        // its type produces. Every other chunk keeps its previous version's hash and is not touched.
        SortedSet<(ushort TypeId, int ChunkIndex)> affected = ContentChunkBuilder.Affected(_registry, closes, inserts);
        var findings = new List<ContentFinding>(swept.Findings);
        Step(ContentPublishStep.BeforeChunkWrite);
        List<ContentChunkRecord> chunks = ContentChunkBuilder
            .Build(_registry, rows, affected, baseline, _rowEncoder, findings);
        Step(ContentPublishStep.AfterChunkWrite);

        if (findings.Count != swept.Findings.Count)
        {
            // KEC0014: the client bytes still carry a ServerOnly field, which is the encoder failing to
            // honour the schema. The chunks are kept on the plan, because what they hold is the evidence.
            return Refused(
                version, baseline, candidate, Report(findings), allocation, closes, inserts, live, appended,
                rules, chunks, ruleHash, minimumServerBuild, minimumClientBuild);
        }

        // STEP 8. Both manifests, each under its own hash sub-domain, so a head gating on one can never
        // accidentally agree with a head gating on the other.
        Step(ContentPublishStep.BeforeManifestWrite);
        ContentManifest server = ContentManifestBuilder.Build(
            ContentManifestSide.Server, _registry, chunks, baseline.Languages, version,
            minimumServerBuild, minimumClientBuild, ruleHash);
        ContentManifest client = ContentManifestBuilder.Build(
            ContentManifestSide.Client, _registry, chunks, baseline.Languages, version,
            minimumServerBuild, minimumClientBuild, ruleHash);
        string serverHash = ContentManifestText.Hash(server);
        string clientHash = ContentManifestText.Hash(client);
        Step(ContentPublishStep.AfterManifestWrite);

        return new ContentPublishPlan(
            version,
            baseline.VersionNumber,
            candidate,
            swept,
            allocation,
            closes,
            inserts,
            live,
            appended,
            rules,
            chunks,
            baseline.Languages,
            ruleHash,
            server,
            client,
            serverHash,
            clientHash,
            minimumServerBuild,
            minimumClientBuild);
    }

    /// <summary>
    /// Step 1. The draft is frozen for the duration of the publish and the store holds the row lock, so a
    /// draft edit arriving while a publish is in flight is refused rather than half applied.
    /// <para>
    /// <see cref="ContentPublishRequest.ExpectedBaseVersion"/> is optimistic concurrency and it is checked
    /// against the BASELINE, which the commit read under that lock. Two consoles cannot both publish the
    /// same draft: the second one's expectation is stale and it is refused with both numbers named.
    /// </para>
    /// </summary>
    async Task<ContentDraft> FreezeAsync(
        ContentPublishRequest request,
        ContentPublishBaseline baseline,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedBaseVersion != baseline.VersionNumber)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The publish expects base version {request.ExpectedBaseVersion} and the store stands at {baseline.VersionNumber}. Another publish landed in between, so this draft is against a version that is no longer the base."),
                default,
                0,
                ContentAuthoringException.BaseVersionMovedReason);
        }

        ContentDraft? draft = await _store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        if (draft is null || draft.EditCount == 0)
        {
            throw new ContentAuthoringException(
                "There is no open draft with pending edits, so there is nothing to publish. A version is a set of changes and an empty one would be a number with no content behind it.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);
        }

        return draft;
    }

    void Step(ContentPublishStep step) => OnStep?.Invoke(step);

    /// <summary>
    /// Turns the pending rules into real ones, which is only possible AFTER step 3, because a fork's kind 3
    /// rule names a <c>to_id</c> the allocator had not issued when the edit was applied.
    /// <para>
    /// Sequences are contiguous from 1 across the WHOLE list (<c>KEC0018</c>), so this publish's rules
    /// continue the base version's numbering rather than starting their own.
    /// </para>
    /// </summary>
    static IReadOnlyList<RemapRule> Materialize(
        List<ContentPendingRule> pending,
        ContentPublishBaseline baseline,
        int version)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        var appended = new RemapRule[pending.Count];
        for (int i = 0; i < pending.Count; i++)
        {
            ContentPendingRule rule = pending[i];
            appended[i] = new RemapRule(
                baseline.Rules.Count + i + 1,
                version,
                rule.Type,
                rule.Kind,
                rule.From.DefinitionId,
                rule.To?.DefinitionId ?? 0,
                rule.Payload);
        }

        return appended;
    }

    ContentSnapshot Materialize(
        List<ContentCandidateRow> rows,
        IReadOnlyList<RemapRule> rules,
        int version)
    {
        var builder = new ContentSnapshotBuilder(_registry);
        builder.WithIdentity(version, string.Empty).WithRules(rules);
        for (int i = 0; i < rows.Count; i++)
        {
            builder.AddRow(rows[i].ToRow());
        }

        return builder.Build();
    }

    /// <summary>
    /// The base version as a snapshot, which is what step 4 hands the validator as <c>previous</c>. It is
    /// built from the rows the baseline already carries, so it costs a walk and no store read.
    /// </summary>
    ContentSnapshot Materialize(ContentPublishBaseline baseline, IReadOnlyList<RemapRule> rules)
    {
        var builder = new ContentSnapshotBuilder(_registry);
        builder.WithIdentity(baseline.VersionNumber, string.Empty).WithRules(rules);
        for (int i = 0; i < baseline.Rows.Count; i++)
        {
            builder.AddRow(baseline.Rows[i].Row);
        }

        return builder.Build();
    }

    /// <summary>
    /// Step 5's two writes per changed row, and the live set the next publish's baseline is.
    /// <para>
    /// A row that ENTERS is an insert, and a row it replaces is a close. Every close here has a successor
    /// at the same id, because none of the four operations removes a definition: a definition that leaves
    /// play is retired and its row stays in the pack forever.
    /// </para>
    /// </summary>
    static (IReadOnlyList<ContentRowClose> Closes, IReadOnlyList<ContentRowInsert> Inserts, IReadOnlyList<ContentRowRevision> Live)
        Temporal(List<ContentCandidateRow> rows, int version)
    {
        var closes = new List<ContentRowClose>();
        var inserts = new List<ContentRowInsert>();
        var live = new List<ContentRowRevision>(rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            ContentRow materialized = row.ToRow();
            live.Add(new ContentRowRevision(materialized, row.ValidFromVersion, null, row.FamilyId));
            if (!row.IsEntering)
            {
                continue;
            }

            inserts.Add(new ContentRowInsert(materialized, version, row.FamilyId));
            if (row.ClosesValidFrom is int closed)
            {
                closes.Add(new ContentRowClose(row.Registration.Type, row.DefinitionId, closed, version));
            }
        }

        return (closes, inserts, live);
    }

    /// <summary>
    /// The plan a refusal that happened BEFORE step 2 hands back: the findings, and nothing else, because
    /// nothing else was computed. The rule list is the base version's, unchanged, since a publish that does
    /// not happen appends none.
    /// </summary>
    static ContentPublishPlan Refused(
        int version,
        ContentPublishBaseline baseline,
        ContentSnapshot candidate,
        ContentValidationReport report)
        => Refused(
            version,
            baseline,
            candidate,
            report,
            ContentIdAllocationRecord.Empty,
            [],
            [],
            [],
            [],
            baseline.Rules,
            [],
            ContentRuleChunkCodec.Hash(baseline.Rules),
            baseline.MinimumServerBuild,
            baseline.MinimumClientBuild);

    /// <summary>An empty candidate at the new version, which is what a refusal before step 2 carries.</summary>
    ContentSnapshot Empty(int version)
        => new ContentSnapshotBuilder(_registry).WithIdentity(version, string.Empty).Build();

    static ContentPublishPlan Refused(
        int version,
        ContentPublishBaseline baseline,
        ContentSnapshot candidate,
        ContentValidationReport report,
        ContentIdAllocationRecord allocation,
        IReadOnlyList<ContentRowClose> closes,
        IReadOnlyList<ContentRowInsert> inserts,
        IReadOnlyList<ContentRowRevision> live,
        IReadOnlyList<RemapRule> appended,
        IReadOnlyList<RemapRule> rules,
        IReadOnlyList<ContentChunkRecord> chunks,
        string ruleHash,
        int minimumServerBuild,
        int minimumClientBuild)
        => new(
            version,
            baseline.VersionNumber,
            candidate,
            report,
            allocation,
            closes,
            inserts,
            live,
            appended,
            rules,
            chunks,
            baseline.Languages,
            ruleHash,
            null,
            null,
            string.Empty,
            string.Empty,
            minimumServerBuild,
            minimumClientBuild);

    static ContentValidationReport Report(List<ContentFinding> findings)
    {
        foreach (ContentFinding finding in findings)
        {
            if (!string.Equals(finding.Code, ContentValidator.InformationalCode, StringComparison.Ordinal))
            {
                return new ContentValidationReport(false, findings);
            }
        }

        return new ContentValidationReport(true, findings);
    }

    static IReadOnlyList<RemapRule> Concat(IReadOnlyList<RemapRule> first, IReadOnlyList<RemapRule> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        var all = new RemapRule[first.Count + second.Count];
        for (int i = 0; i < first.Count; i++)
        {
            all[i] = first[i];
        }

        for (int i = 0; i < second.Count; i++)
        {
            all[first.Count + i] = second[i];
        }

        return all;
    }
}
