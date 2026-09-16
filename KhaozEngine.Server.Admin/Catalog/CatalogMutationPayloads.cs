using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// What <c>catalog-edit</c> answers with: the draft as it now stands, and how many edits landed. The
/// COPY's id is deliberately absent from a fork's answer, because ids are allocated at publish and
/// reporting one from an edit would be reporting a number that does not exist yet.
/// </summary>
/// <param name="Draft">The open draft's header.</param>
/// <param name="Applied">How many edits this request applied.</param>
public sealed record CatalogEditPayload(CatalogDraftHeader Draft, int Applied);

/// <summary>
/// What <c>catalog-discard</c> answers with. A discard against a store with no draft open is a 200 that
/// says so rather than a refusal, because "nothing pending" is an answer.
/// </summary>
/// <param name="Discarded">Whether a draft was there to discard.</param>
/// <param name="EditCount">How many edits went with it, which is what the audit row carries.</param>
public sealed record CatalogDiscardPayload(bool Discarded, int EditCount);

/// <summary>
/// What <c>catalog-validate</c> answers with: the full sweep over the draft-applied candidate, run WITHOUT
/// allocating an id and without writing anything.
/// <para>
/// It is the same validator a publish runs, so a green validate followed by a red publish can only mean
/// the draft changed in between. <c>KEC0000</c> is informational and is not a failure: it says the
/// publish-only checks did not run, which is the case on a store that has published nothing.
/// </para>
/// </summary>
/// <param name="Valid">True when nothing but <c>KEC0000</c> was found.</param>
/// <param name="FindingCount">How many findings the sweep accumulated.</param>
/// <param name="Findings">Every finding, in sweep order, and never just the first.</param>
/// <param name="BaseVersion">The version the draft stands on.</param>
/// <param name="CandidateVersion">The number publishing it would assign.</param>
public sealed record CatalogValidatePayload(
    bool Valid,
    int FindingCount,
    IReadOnlyList<CatalogFindingPayload> Findings,
    int BaseVersion,
    int CandidateVersion);

/// <summary>ONE field's before and after, rendered the way the audit renders the same value.</summary>
/// <param name="Field">The schema field name.</param>
/// <param name="Before">The source value, or null when the field was absent there.</param>
/// <param name="After">The destination value, or null when it is absent there.</param>
public sealed record CatalogDiffFieldPayload(string Field, string? Before, string? After);

/// <summary>One definition's entry in a diff, which is FIELD level rather than a chunk hash inequality.</summary>
/// <param name="Type">The content type's key.</param>
/// <param name="Id">The definition id. PROVISIONAL on a row the draft adds, because publish allocates.</param>
/// <param name="Key">The row's key, which never changes once published.</param>
/// <param name="Op">What happened to the definition: add, update, retire or removed.</param>
/// <param name="Fields">Every differing field, in schema order.</param>
public sealed record CatalogDiffChangePayload(
    string Type,
    int Id,
    string Key,
    string Op,
    IReadOnlyList<CatalogDiffFieldPayload> Fields);

/// <summary>
/// One type's share of the DOWNLOAD a publish would cost, which is what lets an operator see the price of
/// an edit before making it.
/// </summary>
/// <param name="Type">The content type's key.</param>
/// <param name="ChangedChunks">How many of its chunks hold a changed row.</param>
/// <param name="TotalChunks">How many chunks the destination holds for this type.</param>
public sealed record CatalogChunkSummaryPayload(string Type, int ChangedChunks, int TotalChunks);

/// <summary>
/// What <c>catalog-diff</c> answers with. <see cref="To"/> is null for the draft-applied candidate, which
/// is the request's <c>to</c> of 0 read as what it is: a row set with no number yet.
/// </summary>
/// <param name="From">The source version.</param>
/// <param name="To">The destination version, or null for the draft-applied candidate.</param>
/// <param name="ProvisionalIds">True when the destination is the candidate, whose new rows carry provisional ids.</param>
/// <param name="Changes">Every changed definition, ordered by type then id.</param>
/// <param name="ChunkSummary">One entry per type, ordered by type id.</param>
public sealed record CatalogDiffPayload(
    int From,
    int? To,
    bool ProvisionalIds,
    IReadOnlyList<CatalogDiffChangePayload> Changes,
    IReadOnlyList<CatalogChunkSummaryPayload> ChunkSummary);

/// <summary>
/// What <c>catalog-publish</c> answers with: the new version's identity on both sides, and the work it
/// cost. The two chunk counts are the operator-facing half of the one-item-edit budget.
/// </summary>
/// <param name="Version">The number the commit assigned.</param>
/// <param name="ServerManifestHash">The server manifest digest, lower hex.</param>
/// <param name="ClientManifestHash">The client manifest digest, lower hex, which the connect door carries.</param>
/// <param name="FormatGeneration">The pack format generation this version was written at.</param>
/// <param name="ChunksWritten">Chunks whose bytes were written, which is the download an adopting client pays.</param>
/// <param name="ChunksReused">Chunks carried forward unchanged, which a client holding them refetches never.</param>
/// <param name="BytesWritten">Stored bytes written across every chunk and both manifests.</param>
/// <param name="RulesAppended">Remap rules appended, 0 for a publish that retires and forks nothing.</param>
/// <param name="ElapsedMs">Wall clock the publish took.</param>
public sealed record CatalogPublishPayload(
    int Version,
    string ServerManifestHash,
    string ClientManifestHash,
    int FormatGeneration,
    int ChunksWritten,
    int ChunksReused,
    long BytesWritten,
    int RulesAppended,
    long ElapsedMs);

/// <summary>
/// What <c>catalog-pin</c> answers with.
/// <para>
/// <b><see cref="ConfigPinnedVersion"/> is the property that stops a silent no-op.</b> A server's own
/// config pin WINS over the operator's hold, always, so a pin written against such a server takes effect
/// only once the config pin is removed. The write happened, so this is a 200 rather than a refusal, and an
/// operator who received a bare 200 for a call with no effect on the next restart is precisely the failure
/// this property exists to prevent.
/// </para>
/// </summary>
/// <param name="PinnedVersion">The hold as it now stands, or null when the call cleared it.</param>
/// <param name="ConfigPinnedVersion">The version the server's own config pins, or null when it pins none.</param>
/// <param name="Warnings">Anything an operator needs to read about a pin that was nonetheless written.</param>
public sealed record CatalogPinPayload(
    int? PinnedVersion,
    int? ConfigPinnedVersion,
    IReadOnlyList<string> Warnings);

/// <summary>One rule that BLOCKS a rollback, with the publish that introduced it named beside it.</summary>
/// <param name="Sequence">The rule's sequence, or 0 when no rule names the row.</param>
/// <param name="IntroducedIn">The version the rule was published in, or 0 when no rule names the row.</param>
/// <param name="Type">The content type's key.</param>
/// <param name="FromId">The retired definition id.</param>
/// <param name="Kind">The remap rule kind, which is <c>Retired</c> for every blocker.</param>
public sealed record CatalogBlockingRulePayload(
    int Sequence,
    int IntroducedIn,
    string Type,
    int FromId,
    string Kind);

/// <summary>
/// What <c>catalog-rollback</c> answers with. It BUILDS A DRAFT rather than publishing one, so the operator
/// reviews the diff and publishes it, which is what makes a rollback reviewable rather than a second
/// uncontrolled change.
/// </summary>
/// <param name="DraftCreated">True when the rollback built a draft.</param>
/// <param name="EditCount">How many edits the draft now holds.</param>
/// <param name="BlockedByRules">Empty on a rollback that may proceed.</param>
public sealed record CatalogRollbackPayload(
    bool DraftCreated,
    int EditCount,
    IReadOnlyList<CatalogBlockingRulePayload> BlockedByRules);

/// <summary>
/// The 409 of a rollback blocked by an irreversible retire, which names the way OUT rather than leaving an
/// operator guessing. A retire is irreversible for pages already migrated past it, so there is no un-retire
/// branch and there never was a reachable one.
/// </summary>
/// <param name="Error">The human-readable refusal.</param>
/// <param name="Reason">The stable reason token.</param>
/// <param name="Code">The finding code, <c>KEC0039</c>.</param>
/// <param name="BlockedByRules">Every rule that blocks it.</param>
/// <param name="Remedy">The mint-a-new-id path, named in full.</param>
public sealed record CatalogRollbackBlockedPayload(
    string Error,
    string Reason,
    string Code,
    IReadOnlyList<CatalogBlockingRulePayload> BlockedByRules,
    string Remedy);

/// <summary>
/// What <c>catalog-import</c> answers with: the version the bundle published as, which is always 1, and the
/// work it cost. An import runs into an EMPTY database only, so its version line starts here.
/// </summary>
/// <param name="Version">The version the import published, which is 1.</param>
/// <param name="RowsImported">How many rows the bundle carried.</param>
/// <param name="ServerManifestHash">The server manifest digest, lower hex.</param>
/// <param name="ClientManifestHash">The client manifest digest, lower hex.</param>
/// <param name="ChunksWritten">How many chunks the publish wrote.</param>
/// <param name="BytesWritten">Stored bytes written.</param>
/// <param name="ElapsedMs">Wall clock the import took.</param>
public sealed record CatalogImportPayload(
    int Version,
    int RowsImported,
    string ServerManifestHash,
    string ClientManifestHash,
    int ChunksWritten,
    long BytesWritten,
    long ElapsedMs);

/// <summary>
/// What <c>catalog-export</c> answers with: the whole catalog as ONE document, ids included, which is what
/// makes it a lossless export rather than an approximation.
/// <para>
/// <b>A lossless export is not a backup</b>, and the difference is the version LINE. An import republishes
/// at version 1, so the new database's history starts there. When the version line must be preserved, the
/// path is an ordinary database restore of the authoring store.
/// </para>
/// </summary>
/// <param name="Version">The version exported.</param>
/// <param name="RowCount">How many rows the bundle carries, so a console shows a size without walking it.</param>
/// <param name="Bundle">The bundle document, exactly as the bundle writer produced it.</param>
public sealed record CatalogExportPayload(int Version, int RowCount, JsonElement Bundle);

/// <summary>
/// What <c>catalog-sweep</c> answers with. A sweep that SKIPPED is not a failure and is not silent either:
/// deleting nothing and deleting everything are one keystroke apart, so the reason is on the answer.
/// </summary>
/// <param name="Ran">True when the pack store was actually pruned.</param>
/// <param name="Kept">How many distinct objects the keep set held, which is the count it did NOT delete.</param>
/// <param name="Deleted">How many orphans were deleted.</param>
/// <param name="SkipReason">Why the sweep did not run, or null when it did.</param>
public sealed record CatalogSweepPayload(bool Ran, int Kept, int Deleted, string? SkipReason);

/// <summary>One object whose bytes do not digest to the address they are filed under.</summary>
/// <param name="Side">Which manifest named it: <c>server</c>, <c>client</c>, or <c>shared</c> for a rule or text chunk.</param>
/// <param name="Hash">The address it was fetched under.</param>
/// <param name="Reason">The pack reader's own stable reason token.</param>
public sealed record CatalogChunkMismatchPayload(string Side, string Hash, string Reason);

/// <summary>
/// What <c>catalog-verify</c> answers with. It walks a version's manifests, fetches every object they name
/// and rehashes it.
/// <para>
/// <b>It is read only and it NEVER repairs</b>, because a repair means deciding which copy is right and
/// only a republish can know that.
/// </para>
/// </summary>
/// <param name="Version">The version verified.</param>
/// <param name="Healthy">True when every object digested to the address it was filed under.</param>
/// <param name="ObjectsChecked">How many objects were fetched and rehashed, both manifests included.</param>
/// <param name="Mismatches">Every object that did not match, empty on a healthy pack.</param>
public sealed record CatalogVerifyPayload(
    int Version,
    bool Healthy,
    int ObjectsChecked,
    IReadOnlyList<CatalogChunkMismatchPayload> Mismatches);
