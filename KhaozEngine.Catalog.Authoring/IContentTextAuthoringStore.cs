using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The opt-in TEXT AUTHORING companion of <see cref="IContentAuthoringStore"/>: atomic mixed row and text
/// changes, a complete freeze, exact-version text snapshots, a complete commit and an atomic expected-draft
/// discard. The row-only seam keeps every signature it had, and a backend that implements this companion
/// makes each of its row-only routes refuse rather than drop text it holds.
/// <para>
/// <b>The backend is authoritative.</b> A caller's empty list, reconstructed old DTO or completeness flag is
/// never proof that text is absent. Every member that consumes or destroys state compares what it was handed
/// against what the store actually holds, inside the store's own gate or transaction.
/// </para>
/// <para>
/// <b>Text-bearing publication dispatches through <see cref="CommitTextPublishAsync"/></b> and text-bearing
/// import through <see cref="ImportTextBundleAsync"/>. Neither runs the row-only member and appends text
/// afterwards, because a failure between the two would expose a version without its text.
/// </para>
/// </summary>
public interface IContentTextAuthoringStore : IContentAuthoringStore
{
    /// <summary>
    /// Applies one batch of row and text intents to the open draft, opening one against the active version
    /// when none is open. Every intent lands in one transaction with its audit, or none does.
    /// <para>
    /// A text target needs a registered CLIENT-visible type, a CLIENT-visible localized text marker field and
    /// a row live at the active version, retired rows included, or added or forked earlier in the same draft.
    /// A Set introduces its language when neither the base nor the draft declares it. A Remove in a declared
    /// language is idempotent when nothing is there to remove, and a Remove in an undeclared one is refused.
    /// The last intent for a target wins and keeps its first ordinal.
    /// </para>
    /// </summary>
    /// <param name="changes">The row and text intents.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded, empty when it forwarded none.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The complete draft after the batch.</returns>
    /// <exception cref="ContentAuthoringException">An intent is ineligible, out of bounds, in an undeclared language, collides, or a publish holds the draft frozen.</exception>
    Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Freezes the complete open draft for the publish standing on <paramref name="expectedBaseVersion"/> and
    /// reads, in the same step, the row baseline, the complete baseline text and the frozen draft. A draft
    /// whose total work is 0 is refused, so a truly empty publish stays refused.
    /// <para>
    /// A base version with no complete text record is read as empty only on a read-only proof that its
    /// manifests named no language, the verified stored manifests at its recorded hashes or no-language
    /// manifests rebuilt from its recorded chunks. Nothing records that proof, and the commit that follows
    /// writes its own version complete. Without a proof the freeze is refused with
    /// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before any marker is written.
    /// </para>
    /// </summary>
    /// <param name="expectedBaseVersion">The active version the caller expects to publish onto.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The base moved, no draft holds work, or the base version's text is unknown.</exception>
    Task<ContentTextPublishSnapshot> FreezeChangesAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The complete text of one exact, positive, committed version. Version 0 is not a shortcut for the active
    /// version and is refused, like a version the store does not hold.
    /// <para>
    /// A version with no complete text record yields an empty snapshot only on the same read-only proof the
    /// freeze runs, and the read writes nothing. Otherwise it is refused with
    /// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/>.
    /// </para>
    /// </summary>
    /// <param name="versionNumber">The committed version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="versionNumber"/> is negative.</exception>
    /// <exception cref="ContentAuthoringException">The version is 0 or unknown, or its text cannot be proven complete.</exception>
    Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
        int versionNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a complete text plan in ONE transaction: it confirms the epoch, the base, the frozen rows, text
    /// and declarations against the store's actual state, then commits rows, rules, chunks, text revisions,
    /// every language mapping, the audit, the version and the draft consumption together, moving the active
    /// pointer last. The version is recorded as text complete.
    /// </summary>
    /// <param name="plan">The complete plan, whose pack files have already been written.</param>
    /// <param name="request">The publish request, whose actor, operator and note the version and audit carry.</param>
    /// <param name="pointers">Where the version's pointer is written inside the commit, or null to write none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The plan does not match the store's actual state, or the version moved.</exception>
    Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the open draft only when it is EXACTLY <paramref name="expected"/>: the same base, freeze
    /// state, rows, text intents and introductions. The comparison and the delete share one gate or
    /// transaction, so a rival translation, introduction or freeze returns false with nothing deleted and no
    /// discard audited. A draft built by a row-only route is never a proof and always returns false.
    /// </summary>
    /// <param name="expected">The complete draft the caller proved it owns.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the expected draft was discarded.</returns>
    Task<bool> TryDiscardChangesAsync(
        ContentDraft expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a complete bundle into an EMPTY store, rows, ids, families, rules, declarations and values
    /// together, as one complete version 1. A format 1 bundle imports as empty text. A format 2 bundle that
    /// lost its section, a later format, a value naming a row the bundle does not carry and an ineligible
    /// target are refused before anything is reset or staged, and any later refusal restores the empty store.
    /// An open draft holding any row, text or language work refuses the import with
    /// <see cref="ContentAuthoringException.DraftOpenReason"/> before anything is staged, and stays as it was.
    /// Every declared language keeps its wire spelling, empty languages included.
    /// </summary>
    /// <param name="bundle">The bundle to import.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The import is refused, including with <see cref="ContentAuthoringException.TextOperationUnavailableReason"/> by a store that does not complete it yet.</exception>
    Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a draft restoring the rows and text of an earlier version, retaining every currently declared
    /// language.
    /// <para>
    /// The text half stages complete value changes: every string of a row live at the target reads as it did
    /// there, retired rows included, and a language the target never had stays declared with its last value
    /// removed. A row retired since the target still blocks the rollback, and no text intent unretires a row
    /// or changes an old version. Both versions need complete or proved text, and an unknown one is refused
    /// with <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/> before anything is written.
    /// </para>
    /// </summary>
    /// <param name="targetVersion">The version to restore.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The rollback is refused, including with <see cref="ContentAuthoringException.TextOperationUnavailableReason"/> by a store that does not complete it yet.</exception>
    Task<ContentDraft> RollbackTextToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);
}
