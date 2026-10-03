using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The TEXT half of the in-memory store, which is the reference behavior of
/// <see cref="IContentTextAuthoringStore"/>: atomic mixed apply, sticky language introductions, the complete
/// freeze, exact-version snapshots, the atomic expected-draft discard, and the gates the row-only routes run.
/// <para>
/// Text revisions are temporal like rows, and every committed version records its COMPLETE language list.
/// A version holding an entry in that record is text complete, and every commit this store makes writes
/// one, row-only commits included. Its drafts always carry a text state, so an empty one here is the
/// backend's own proof that nothing is held.
/// </para>
/// <para>
/// Like the rest of the store, every member BUILDS into locals under the gate and applies in a tail that
/// cannot throw, so a clock, audit or validation failure leaves the draft, the text and the audit unchanged.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore : IContentTextAuthoringStore
{
    readonly List<ContentTextRevision> _textRevisions = [];
    readonly Dictionary<int, ContentTextLanguage[]> _textLanguages = [];

    /// <inheritdoc />
    public Task<ContentDraft> ApplyChangesAsync(
        ContentAuthoringChanges changes,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        lock (_gate)
        {
            RequireNotFrozen(nameof(ApplyChangesAsync));
            for (int i = 0; i < changes.RowEdits.Count; i++)
            {
                CheckAgainstSchema(changes.RowEdits[i]);
            }

            ContentDraft? open = _draft;
            var working = new ContentChangeSet(open?.Changes.Edits ?? Array.Empty<ContentEdit>());
            for (int i = 0; i < changes.RowEdits.Count; i++)
            {
                working.Apply(changes.RowEdits[i]);
            }

            ContentDraftTextState held = open?.TextState ?? ContentDraftTextState.Empty;
            var edits = new List<ContentTextEdit>(held.Edits);
            var introductions = new List<ContentTextLanguageDeclaration>(held.Introductions);
            var applied = new List<(ContentTextEdit Edit, int DefinitionId)>(changes.TextEdits.Count);
            HashSet<string> declared = DeclaredAt(_activeVersion);
            HashSet<(ushort, ContentKey)> pending = PendingKeys(working);
            for (int i = 0; i < changes.TextEdits.Count; i++)
            {
                ContentTextEdit edit = changes.TextEdits[i];
                if (ApplyText(edit, declared, pending, edits, introductions) is int id)
                {
                    applied.Add((edit, id));
                }
            }

            var text = new ContentDraftTextState(edits, introductions);
            ContentDraft current = open
                ?? new ContentDraft(ContentDraftTextState.Empty, _activeVersion, actor, _clock(), note, new ContentChangeSet());

            var staged = new List<ContentAuditEntry>(changes.RowEdits.Count + applied.Count);
            for (int i = 0; i < changes.RowEdits.Count; i++)
            {
                _audit.StageEdit(staged, changes.RowEdits[i], actor, operatorId, note);
            }

            foreach ((ContentTextEdit edit, int id) in applied)
            {
                _audit.Stage(
                    staged,
                    ContentAuditActions.DraftEdit,
                    actor,
                    operatorId,
                    edit.Target.Type,
                    id,
                    edit.Target.Key,
                    edit.Target.FieldName,
                    null,
                    ContentTextAuditRendering.Render(edit.Value),
                    0,
                    note,
                    edit.Target.Language);
            }

            var next = new ContentDraft(
                text,
                current.BaseVersion,
                current.OpenedBy,
                current.OpenedAtUtc,
                note.Length == 0 ? current.Note : note,
                working);
            _draft = next;
            _audit.Commit(staged);
            return Task.FromResult(Protected(next));
        }
    }

    /// <inheritdoc />
    public Task<ContentTextPublishSnapshot> FreezeChangesAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ClearStaleFreeze();
            if (expectedBaseVersion != _activeVersion)
            {
                throw Moved(FormattableString.Invariant(
                    $"The publish expects base version {expectedBaseVersion} and the store stands at {_activeVersion}. Another publish landed in between, so this draft is against a version that is no longer the base."));
            }

            if (_draft is not ContentDraft open || open.TotalWorkCount == 0)
            {
                throw new ContentAuthoringException(
                    "There is no open draft with pending row work, text work or language introductions, so there is nothing to publish.",
                    default,
                    0,
                    ContentAuthoringException.NoOpenDraftReason);
            }

            ContentVersionTextSnapshot baselineText = TextSnapshotAt(_activeVersion);
            ContentDraft frozen = Reframe(open, open.BaseVersion, open.Changes, _activeVersion);
            var snapshot = new ContentTextPublishSnapshot(_storeEpoch, ReadBaseline(), baselineText, frozen);
            _draft = frozen;
            return Task.FromResult(snapshot);
        }
    }

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        lock (_gate)
        {
            if (versionNumber == 0 || FindVersion(versionNumber) is null)
            {
                throw UnknownVersion(versionNumber);
            }

            return Task.FromResult(TextSnapshotAt(versionNumber));
        }
    }

    /// <inheritdoc />
    public Task<bool> TryDiscardChangesAsync(
        ContentDraft expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        lock (_gate)
        {
            if (_draft is not ContentDraft open || open.IsFrozen || !ContentTextCompatibility.SameDraft(expected, open))
            {
                return Task.FromResult(false);
            }

            var staged = new List<ContentAuditEntry>(3);
            StageDiscard(staged, actor, operatorId, string.Empty, open.EditCount);
            if (open.TextEditCount > 0)
            {
                StageDiscard(staged, actor, operatorId, "text-edits", open.TextEditCount);
            }

            if (open.LanguageIntroductionCount > 0)
            {
                StageDiscard(staged, actor, operatorId, "language-introductions", open.LanguageIntroductionCount);
            }

            _draft = null;
            _audit.Commit(staged);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    /// <remarks>Not completed by this store yet, so it is refused whole rather than importing rows alone.</remarks>
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
        throw Unavailable(nameof(ImportTextBundleAsync));
    }

    /// <inheritdoc />
    /// <remarks>Not completed by this store yet, so it is refused whole rather than restoring rows alone.</remarks>
    public Task<ContentDraft> RollbackTextToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);
        throw Unavailable(nameof(RollbackTextToAsync));
    }

    /// <summary>
    /// The refusal a row-only publish step runs: no held text, and no fork of a row whose text the row-only
    /// route cannot copy. The caller already holds the gate.
    /// </summary>
    void RequireRowOnlyRepresentable(ContentDraft? draft, string member)
    {
        ContentTextCompatibility.RequireNoHeldText(draft, member);
        if (draft is not null)
        {
            RequireNoTextFork(draft.Changes.Edits, member);
        }
    }

    /// <summary>Refuses a fork of a row holding text at the active version. The caller already holds the gate.</summary>
    void RequireNoTextFork(IReadOnlyList<ContentEdit> edits, string member)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            ContentEdit edit = edits[i];
            if (edit.Operation == ContentEditOperation.Fork && HoldsTextAt(edit.Type, edit.DefinitionId, _activeVersion))
            {
                throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                    $"the fork of type {edit.Type.Value} row {edit.DefinitionId} owes its copy text this route cannot write"));
            }
        }
    }

    /// <summary>Refuses a row-only export of a version that holds text. The caller already holds the gate.</summary>
    void RequireRowOnlyExport(int versionNumber)
    {
        ContentVersionTextSnapshot text = TextSnapshotAt(versionNumber);
        if (text.Languages.Count > 0 || text.Revisions.Count > 0)
        {
            throw ContentTextCompatibility.Unrepresented(nameof(ExportBundleAsync), FormattableString.Invariant(
                $"version {versionNumber} records {text.Languages.Count} language(s) and {text.Revisions.Count} value(s), which bundle format {ContentBundle.CurrentFormatVersion} cannot carry"));
        }
    }

    /// <summary>
    /// Refuses a row-only rollback when the current or the target version holds a text value or a declared
    /// language, or has no complete text record. The caller already holds the gate.
    /// </summary>
    void RequireRowOnlyRollback(int from, int target)
    {
        RequireTextFree(from, nameof(RollbackToAsync));
        RequireTextFree(target, nameof(RollbackToAsync));
    }

    /// <summary>Refuses a version a row-only route cannot prove text free. The caller already holds the gate.</summary>
    void RequireTextFree(int versionNumber, string member)
    {
        if (versionNumber != NoActiveVersion && !_textLanguages.ContainsKey(versionNumber))
        {
            throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                $"version {versionNumber} has no complete text record, so its text is unknown rather than empty"));
        }

        ContentVersionTextSnapshot text = TextSnapshotAt(versionNumber);
        if (text.Languages.Count > 0 || text.Revisions.Count > 0)
        {
            throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                $"version {versionNumber} records {text.Languages.Count} language(s) and {text.Revisions.Count} value(s)"));
        }
    }

    /// <summary>
    /// The complete text of one version: an empty baseline at 0, and the recorded languages plus every live
    /// revision at a committed version. The caller already holds the gate.
    /// </summary>
    ContentVersionTextSnapshot TextSnapshotAt(int versionNumber)
    {
        if (versionNumber == NoActiveVersion)
        {
            return ContentVersionTextSnapshot.EmptyBaseline(_storeEpoch);
        }

        if (!_textLanguages.TryGetValue(versionNumber, out ContentTextLanguage[]? languages))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Version {versionNumber} has no complete text record, so its text is unknown rather than empty."),
                default,
                0,
                ContentAuthoringException.TextProvenanceUnknownReason);
        }

        return new ContentVersionTextSnapshot(
            _storeEpoch, versionNumber, _textRevisions.FindAll(revision => revision.IsLiveAt(versionNumber)), languages);
    }

    /// <summary>
    /// One text intent applied onto the working lists, after its eligibility, row and language checks. It
    /// returns the row's id, 0 for a pending row, for an intent that changed the draft, and null for an
    /// idempotent Remove that changed nothing. The caller already holds the gate.
    /// </summary>
    int? ApplyText(
        ContentTextEdit edit,
        HashSet<string> declared,
        HashSet<(ushort, ContentKey)> pending,
        List<ContentTextEdit> edits,
        List<ContentTextLanguageDeclaration> introductions)
    {
        ContentTextTarget target = edit.Target;
        ContentFieldEntry field = ContentTextTargetEligibility.Require(_registry, target);
        int id = LiveRowId(target.Type, target.Key);
        if (id == 0 && !pending.Contains((target.Type.Value, target.Key)))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text for type {target.Type.Value} row '{target.Key}' names a row neither the base version nor the open draft holds."),
                target.Type,
                0,
                ContentAuthoringException.UnknownRowReason);
        }

        bool isDeclared = declared.Contains(target.Language)
            || introductions.Exists(introduction => introduction.Language == target.Language);
        int standing = edits.FindIndex(held => held.Target.Equals(target));
        if (edit.Operation == ContentTextEditOperation.Set)
        {
            if (!isDeclared)
            {
                introductions.Add(ContentTextLanguageDeclaration.Introduce(target.Language));
            }
        }
        else if (!isDeclared)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Language '{target.Language}' is neither declared by version {_activeVersion} nor introduced by the open draft, so there is nothing to remove in it. A Set is what declares a language."),
                target.Type,
                id,
                ContentAuthoringException.TextLanguageUndeclaredReason);
        }
        else if (standing < 0 && (id == 0 || !HoldsValue(target.Type, id, field.Name, target.Language)))
        {
            return null;
        }

        if (standing >= 0)
        {
            edits[standing] = edit;
        }
        else
        {
            edits.Add(edit);
        }

        return id;
    }

    /// <summary>The canonical languages a committed version declares. The caller already holds the gate.</summary>
    HashSet<string> DeclaredAt(int versionNumber)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in TextSnapshotAt(versionNumber).Languages)
        {
            declared.Add(language.Language);
        }

        return declared;
    }

    /// <summary>The rows a draft adds or forks into, by key, which text may name before they have ids.</summary>
    static HashSet<(ushort, ContentKey)> PendingKeys(ContentChangeSet changes)
    {
        var keys = new HashSet<(ushort, ContentKey)>();
        foreach (ContentEdit edit in changes.Edits)
        {
            if (edit.Operation == ContentEditOperation.Add)
            {
                keys.Add((edit.Type.Value, edit.Key));
            }
            else if (edit.Operation == ContentEditOperation.Fork)
            {
                keys.Add((edit.Type.Value, edit.ForkKey));
            }
        }

        return keys;
    }

    /// <summary>The id of the row live at the active version under one key, retired included, or 0.</summary>
    int LiveRowId(ContentTypeId type, ContentKey key)
    {
        foreach (ContentRowRevision revision in _rows)
        {
            if (revision.Row.Type == type && revision.Row.Key.Equals(key) && IsLiveAt(revision, _activeVersion))
            {
                return revision.Row.Id;
            }
        }

        return 0;
    }

    bool HoldsValue(ContentTypeId type, int definitionId, string fieldName, string language)
        => _textRevisions.Exists(revision => revision.Type == type
            && revision.DefinitionId == definitionId
            && revision.FieldName == fieldName
            && revision.Language == language
            && revision.IsLiveAt(_activeVersion));

    bool HoldsTextAt(ContentTypeId type, int definitionId, int versionNumber)
        => _textRevisions.Exists(revision => revision.Type == type
            && revision.DefinitionId == definitionId
            && revision.IsLiveAt(versionNumber));

    void StageDiscard(List<ContentAuditEntry> staged, string actor, string operatorId, string fieldName, int count)
        => _audit.Stage(
            staged,
            ContentAuditActions.DraftDiscard,
            actor,
            operatorId,
            default,
            0,
            default,
            fieldName,
            InMemoryContentAuditLog.Render(count),
            null,
            0,
            string.Empty);

    static ContentAuthoringException Unavailable(string member)
        => new(
            FormattableString.Invariant(
                $"{nameof(InMemoryContentAuthoringStore)}.{member} is not available in this build, so it is refused whole rather than run without its text."),
            default,
            0,
            ContentAuthoringException.TextOperationUnavailableReason);
}
