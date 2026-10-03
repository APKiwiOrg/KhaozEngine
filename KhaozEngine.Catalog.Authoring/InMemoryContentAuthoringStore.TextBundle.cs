using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The COMPANION IMPORT of the in-memory store: a complete bundle, rows and text together, into an empty
/// store. It is the only route a text-bearing bundle lands through. It never runs the row-only import and
/// appends text afterwards, because a failure between the two would leave a version without its text.
/// <para>
/// <b>It shares the row import's one core.</b> The empty-store rule, the type agreement, the staging of
/// families, id marks and restamped rules, and the reset to empty on any refusal after staging began are the
/// row import's own. What this adds is the bundle's text: its format is resolved first, a format 1 bundle
/// converting explicitly to empty text, and every value's target is checked against the bundle's rows and
/// this registry BEFORE anything is staged. The rows, every declared language with its wire spelling, empty
/// ones included, and every value then land in ONE draft, which the ordinary text publish commits as a
/// complete version 1.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// A format 1 bundle imports as empty text. A format 2 bundle that lost its section, a later format, an
    /// open draft holding any work, a value naming a row the bundle does not carry and an ineligible target are
    /// refused before anything is staged. Any refusal after staging began resets this store to empty, exactly as the row import does.
    /// </remarks>
    public Task<ContentPublishResult> ImportTextBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        ContentBundleTextState text = ContentBundleTextCompatibility.TextOf(bundle, nameof(ImportTextBundleAsync));
        return ImportAsync(bundle, text, actor, operatorId, note, nameof(ImportTextBundleAsync), cancellationToken);
    }

    /// <summary>
    /// The bundle's text onto the draft the import's row edits just opened: one Set per value, audited with its
    /// language, and every declared language as an introduction with its exact wire spelling, so a historical
    /// <c>en-US</c> publishes as <c>en-US</c> and an empty language still publishes its empty chunk. The
    /// targets were checked before staging. The caller already holds the gate.
    /// </summary>
    /// <param name="text">The bundle's complete text.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    void ApplyImportedTextLocked(ContentBundleTextState text, string actor, string operatorId, string note)
    {
        ContentDraft open = _draft
            ?? throw new InvalidOperationException("The import's row edits open the draft its text lands in.");

        var edits = new ContentTextEdit[text.Values.Count];
        var staged = new List<ContentAuditEntry>(edits.Length);
        for (int i = 0; i < edits.Length; i++)
        {
            ContentBundleTextValue value = text.Values[i];
            edits[i] = ContentTextEdit.Set(value.Target, value.Value);
            _audit.Stage(
                staged,
                ContentAuditActions.DraftEdit,
                actor,
                operatorId,
                value.Target.Type,
                0,
                value.Target.Key,
                value.Target.FieldName,
                null,
                ContentTextAuditRendering.Render(value.Value),
                0,
                note,
                value.Target.Language);
        }

        _draft = new ContentDraft(
            new ContentDraftTextState(edits, text.Languages),
            open.BaseVersion,
            open.OpenedBy,
            open.OpenedAtUtc,
            open.Note,
            open.Changes);
        _audit.Commit(staged);
    }
}
