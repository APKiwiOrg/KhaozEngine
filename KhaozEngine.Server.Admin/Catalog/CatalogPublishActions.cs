using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The publish trio of spec 10.6: <c>catalog-validate</c>, <c>catalog-diff</c> and <c>catalog-publish</c>.
/// <para>
/// <b><c>expectedBaseVersion</c> on a publish is REQUIRED optimistic concurrency.</b> Two consoles cannot
/// both publish the same draft: the second one's expectation is stale and it gets a 409 naming BOTH
/// numbers, which turns a race into an error message a caller resolves by re-reading and retrying.
/// </para>
/// <para>
/// <b>There is deliberately NO validation override flag anywhere here.</b> A publish that bypassed
/// validation would make boot the only real gate while boot fails CLOSED, so the operator would have
/// published a version that cannot be served. The repair path for a validator bug is an engine patch:
/// export the failing candidate through <c>catalog-export</c> and replay it in a unit test.
/// </para>
/// </summary>
/// <param name="store">The authoring store the candidate is built from and published through.</param>
/// <param name="registry">The registry the candidate's types are declared in. Per instance, never ambient.</param>
internal sealed class CatalogPublishActions(IContentAuthoringStore store, ContentTypeRegistry registry)
{
    /// <summary>The refusal a publish the validator rejected comes back under.</summary>
    public const string CandidateRefused = "content candidate refused";

    /// <summary>Registers the three names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.ValidateAction, ValidateAsync, mutating: true);
        admin.RegisterAction(CatalogAdminActions.DiffAction, DiffAsync, mutating: true);
        admin.RegisterAction(CatalogAdminActions.PublishAction, PublishAsync, mutating: true);
    }

    /// <summary>
    /// The DRY RUN: the candidate a publish would build, swept by the same validator, with no id allocated
    /// and no row written. It needs no body.
    /// <para>
    /// The one write it makes is the one a publish's own first read makes: <c>ReadPublishBaselineAsync</c>
    /// clears a STALE freeze marker, meaning one naming a base version the store no longer stands at. Only a
    /// publish that died between its commit and its own cleanup leaves one, and the draft it names would
    /// otherwise refuse every edit forever, so this is a recovery rather than a side effect.
    /// </para>
    /// </summary>
    async Task<AdminActionResult> ValidateAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        ContentDraftCandidate candidate;
        try
        {
            candidate = await CandidateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }

        ContentValidationReport report = candidate.Report;
        return AdminActionResult.Ok(new CatalogValidatePayload(
            report.IsValid,
            report.Findings.Count,
            CatalogRefusal.Render(report.Findings, registry),
            candidate.BaseVersion,
            candidate.CandidateVersion));
    }

    /// <summary>
    /// The FIELD LEVEL diff, computed over the rows rather than over chunk hashes, plus the chunk summary
    /// that says what publishing it would cost a client to download.
    /// </summary>
    async Task<AdminActionResult> DiffAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        int from = 0;
        int to = 0;
        if (payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
        {
            if (!CatalogRequest.TryVersion(body, "from", out from, out string? refusal)
                || !CatalogRequest.TryVersion(body, "to", out to, out refusal))
            {
                return CatalogRefusal.Malformed(refusal!);
            }
        }

        int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        if (from == 0)
        {
            from = active;
        }

        // Both endpoints are checked to EXIST. A 0 is the caller's "use the active one" on from and "diff the
        // open draft" on to, and neither names a version. Any other number that the store does not hold is a
        // typo, and answering an empty change set to a typo tells an operator their edit is already published
        // when nothing of the sort is true. catalog-pin, catalog-verify and catalog-export all refuse an
        // unknown version, so this refuses under the same token.
        if (await UnknownVersionAsync(from, cancellationToken).ConfigureAwait(false) is AdminActionResult unknownFrom)
        {
            return unknownFrom;
        }

        if (await UnknownVersionAsync(to, cancellationToken).ConfigureAwait(false) is AdminActionResult unknownTo)
        {
            return unknownTo;
        }

        try
        {
            List<ContentRowRevision> source = from == 0
                ? []
                : await CatalogRequest.LiveRowsAsync(store, registry, from, cancellationToken).ConfigureAwait(false);

            ContentDiff diff;
            bool provisional = to == 0;
            if (provisional)
            {
                ContentDraftCandidate candidate = await CandidateAsync(cancellationToken).ConfigureAwait(false);
                diff = ContentDiff.Between(from, source, null, candidate.Rows, registry);
            }
            else
            {
                IReadOnlyList<ContentRowRevision> destination = await CatalogRequest
                    .LiveRowsAsync(store, registry, to, cancellationToken).ConfigureAwait(false);
                diff = ContentDiff.Between(from, source, to, destination, registry);
            }

            return AdminActionResult.Ok(Render(diff, provisional));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }

    /// <summary>
    /// The refusal for a diff endpoint the store does not hold, or null when the number is fine. A 0 is
    /// always fine: it is the caller declining to name a version rather than naming a missing one.
    /// </summary>
    /// <param name="version">The endpoint the request named.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    async Task<AdminActionResult?> UnknownVersionAsync(int version, CancellationToken cancellationToken)
    {
        if (version == 0
            || await store.GetVersionAsync(version, cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        return CatalogRefusal.BadRequest(
            FormattableString.Invariant($"Version {version} does not exist, so there is nothing to diff."),
            ContentAuthoringException.UnknownVersionReason);
    }

    /// <summary>
    /// Publishes the open draft as a new immutable version. The elapsed milliseconds come back on the
    /// response, which is where the publish-time budget is read from.
    /// </summary>
    async Task<AdminActionResult> PublishAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload,
            CatalogAdminActions.PublishAction,
            "'expectedBaseVersion'",
            out JsonElement body,
            out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNote(body, out string note, out refusal)
            || !CatalogRequest.TryNullableCount(body, "expectedBaseVersion", out int? expected, out bool present, out refusal)
            || !CatalogRequest.TryNullableCount(body, "minimumServerBuild", out int? minimumServer, out _, out refusal)
            || !CatalogRequest.TryNullableCount(body, "minimumClientBuild", out int? minimumClient, out _, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (!present || expected is null)
        {
            return CatalogRefusal.Malformed(
                "'expectedBaseVersion' is REQUIRED optimistic concurrency and this request carries none. Two consoles cannot both publish the same draft, so a publish names the version its draft was opened against.");
        }

        try
        {
            ContentPublishResult result = await store
                .PublishAsync(
                    new ContentPublishRequest(
                        CatalogAdminActions.Actor, operatorId, note, expected.Value, minimumServer, minimumClient),
                    cancellationToken)
                .ConfigureAwait(false);

            return AdminActionResult.Ok(new CatalogPublishPayload(
                result.VersionNumber,
                result.ServerManifestHash,
                result.ClientManifestHash,
                result.FormatGeneration,
                result.ChunksWritten,
                result.ChunksReused,
                result.BytesWritten,
                result.RulesAppended,
                result.ElapsedMilliseconds));
        }
        catch (ContentAuthoringException failure)
            when (failure.Reason == ContentAuthoringException.BaseVersionMovedReason)
        {
            // The 409 names BOTH numbers, read fresh rather than parsed out of the message, so a caller
            // re-reads the draft against the version the store actually stands at and retries.
            int actual = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
            return AdminActionResult.Conflict(new CatalogBaseVersionMovedPayload(
                "base version moved",
                ContentAuthoringException.BaseVersionMovedReason,
                expected.Value,
                actual));
        }
        catch (ContentAuthoringException failure)
            when (failure.Reason == ContentAuthoringException.CandidateInvalidReason)
        {
            return CatalogRefusal.Findings(
                CandidateRefused,
                ContentAuthoringException.CandidateInvalidReason,
                CatalogRefusal.Render(failure.Findings, registry));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }

    /// <summary>
    /// The draft-applied candidate, which both the dry run and a diff against 0 are asked about. It reads
    /// the baseline the way a publish does, so the two answer about one row set rather than two reads of it.
    /// </summary>
    async Task<ContentDraftCandidate> CandidateAsync(CancellationToken cancellationToken)
    {
        ContentPublishBaseline baseline = await store
            .ReadPublishBaselineAsync(cancellationToken).ConfigureAwait(false);
        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        return ContentDraftCandidate.Build(baseline, draft?.Changes ?? new ContentChangeSet(), registry);
    }

    /// <summary>The diff as a console receives it, with every type id turned into the key it renders under.</summary>
    CatalogDiffPayload Render(ContentDiff diff, bool provisional)
    {
        var changes = new List<CatalogDiffChangePayload>(diff.Changes.Count);
        for (int i = 0; i < diff.Changes.Count; i++)
        {
            ContentDiffEntry entry = diff.Changes[i];
            var fields = new List<CatalogDiffFieldPayload>(entry.Fields.Count);
            for (int f = 0; f < entry.Fields.Count; f++)
            {
                ContentDiffField field = entry.Fields[f];
                fields.Add(new CatalogDiffFieldPayload(field.Field, field.Before, field.After));
            }

            changes.Add(new CatalogDiffChangePayload(
                registry.TryGet(entry.Type, out ContentTypeRegistration? type) ? type.TypeKey : string.Empty,
                entry.Id,
                entry.Key.ToString(),
                Operation(entry.Operation),
                fields));
        }

        var summary = new List<CatalogChunkSummaryPayload>(diff.ChunkSummary.Count);
        for (int i = 0; i < diff.ChunkSummary.Count; i++)
        {
            ContentChunkSummaryEntry entry = diff.ChunkSummary[i];
            summary.Add(new CatalogChunkSummaryPayload(entry.TypeKey, entry.ChangedChunks, entry.TotalChunks));
        }

        return new CatalogDiffPayload(diff.FromVersion, diff.ToVersion, provisional, changes, summary);
    }

    /// <summary>The lower-case operation vocabulary an edit REQUEST uses, so a diff reads back in it.</summary>
    static string Operation(ContentDiffOperation operation) => operation switch
    {
        ContentDiffOperation.Add => "add",
        ContentDiffOperation.Update => "update",
        ContentDiffOperation.Retire => "retire",
        ContentDiffOperation.Removed => "removed",
        _ => "unknown",
    };
}
