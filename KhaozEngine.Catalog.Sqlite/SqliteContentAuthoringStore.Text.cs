using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The TEXT AUTHORING half of the SQLite store: the atomic mixed apply, the complete freeze, the exact-version
/// snapshot read and the atomic expected-draft discard of <see cref="IContentTextAuthoringStore"/>, mirroring
/// the in-memory reference.
/// <para>
/// <b>Every member is one transaction under the one lease.</b> A batch's row intents, text intents, language
/// introductions and audit rows land together or not at all, so a refused intent, an audit fault or a clock
/// failure leaves the draft and the audit exactly as they were.
/// </para>
/// <para>
/// <b>Intents keep their first ordinal.</b> <c>catalog_draft_text_edit</c> holds one row per canonical target
/// in first-applied order, and a later intent for the target rewrites that row in place, keeping its creation
/// time. An introduction is a row of its own in <c>catalog_draft_text_language</c>, independent of the
/// intents, so a Set replaced by a Remove still publishes its language.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore : IContentTextAuthoringStore
{
    /// <inheritdoc />
    public async Task<ContentDraft> ApplyChangesAsync(
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

        for (int i = 0; i < changes.RowEdits.Count; i++)
        {
            CheckAgainstSchema(changes.RowEdits[i]);
        }

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        await RequireNotFrozenAsync(nameof(ApplyChangesAsync), transaction, cancellationToken).ConfigureAwait(false);
        await OpenDraftAsync(actor, note, transaction, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < changes.RowEdits.Count; i++)
        {
            ContentEdit edit = changes.RowEdits[i];
            await ApplyOneAsync(edit, actor, transaction, cancellationToken).ConfigureAwait(false);
            await AppendEditAuditAsync(transaction, edit, actor, operatorId, note, cancellationToken)
                .ConfigureAwait(false);
        }

        if (changes.TextEdits.Count > 0)
        {
            int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            ContentVersionTextSnapshot baseline = await ReadTextSnapshotAtAsync(active, transaction, cancellationToken)
                .ConfigureAwait(false);
            foreach (ContentTextLanguage language in baseline.Languages)
            {
                declared.Add(language.Language);
            }

            HashSet<(ushort, ContentKey)> pending = PendingKeys(
                await ReadEditsAsync(transaction, cancellationToken).ConfigureAwait(false));
            var context = new TextApply(declared, pending, active, actor, operatorId, note);
            for (int i = 0; i < changes.TextEdits.Count; i++)
            {
                await ApplyTextAsync(changes.TextEdits[i], context, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        ContentDraft draft = await RequireDraftAsync(transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return draft;
    }

    /// <inheritdoc />
    public async Task<ContentTextPublishSnapshot> FreezeChangesAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        await ClearStaleFreezeAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (expectedBaseVersion != active)
        {
            throw Moved(FormattableString.Invariant(
                $"The publish expects base version {expectedBaseVersion} and the store stands at {active}. Another publish landed in between, so this draft is against a version that is no longer the base."));
        }

        if (await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false) is not ContentDraft open
            || open.TotalWorkCount == 0)
        {
            throw new ContentAuthoringException(
                "There is no open draft with pending row work, text work or language introductions, so there is nothing to publish.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);
        }

        // Read BEFORE the marker, so a base whose text is unknown refuses with nothing written.
        ContentVersionTextSnapshot baselineText = await ReadTextSnapshotAtAsync(active, transaction, cancellationToken)
            .ConfigureAwait(false);

        using (SqliteCommand freeze = Command(
            """
            UPDATE catalog_draft
            SET frozen_for_base_version = $base,
                updated_at_utc = CASE WHEN frozen_for_base_version IS $base THEN updated_at_utc ELSE $now END
            WHERE draft_key = 1;
            """,
            transaction))
        {
            Bind(freeze, "$base", (long)active);
            Bind(freeze, "$now", Millis(_clock()));
            await freeze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = new ContentTextPublishSnapshot(
            await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false),
            await ReadBaselineAsync(transaction, cancellationToken).ConfigureAwait(false),
            baselineText,
            await RequireDraftAsync(transaction, cancellationToken).ConfigureAwait(false));
        transaction.Commit();
        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        if (versionNumber == NoActiveVersion
            || !await VersionExistsAsync(versionNumber, null, cancellationToken).ConfigureAwait(false))
        {
            throw UnknownVersion(versionNumber);
        }

        return await ReadTextSnapshotAtAsync(versionNumber, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryDiscardChangesAsync(
        ContentDraft expected,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        // The comparison and the delete share ONE transaction under the lease, so nothing can land between.
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false) is not ContentDraft open
            || open.IsFrozen
            || !ContentTextCompatibility.SameDraft(expected, open))
        {
            return false;
        }

        await DeleteDraftAsync(transaction, cancellationToken).ConfigureAwait(false);
        await AppendDiscardAuditAsync(transaction, actor, operatorId, string.Empty, open.EditCount, cancellationToken)
            .ConfigureAwait(false);
        if (open.TextEditCount > 0)
        {
            await AppendDiscardAuditAsync(
                transaction, actor, operatorId, "text-edits", open.TextEditCount, cancellationToken).ConfigureAwait(false);
        }

        if (open.LanguageIntroductionCount > 0)
        {
            await AppendDiscardAuditAsync(
                transaction, actor, operatorId, "language-introductions", open.LanguageIntroductionCount, cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
        return true;
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
    /// One text intent applied inside the batch's transaction, after its eligibility, row and language checks.
    /// An idempotent Remove changes nothing and audits nothing. The caller owns the transaction.
    /// </summary>
    async Task ApplyTextAsync(
        ContentTextEdit edit,
        TextApply context,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ContentTextTarget target = edit.Target;
        ContentFieldEntry field = ContentTextTargetEligibility.Require(_registry, target);
        int id = await LiveRowIdAsync(target.Type, target.Key, context.Active, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (id == 0 && !context.Pending.Contains((target.Type.Value, target.Key)))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text for type {target.Type.Value} row '{target.Key}' names a row neither the base version nor the open draft holds."),
                target.Type,
                0,
                ContentAuthoringException.UnknownRowReason);
        }

        bool isDeclared = context.Declared.Contains(target.Language)
            || await IntroducedAsync(target.Language, transaction, cancellationToken).ConfigureAwait(false);
        long? standing = await ReadStandingTextAsync(target, transaction, cancellationToken).ConfigureAwait(false);
        if (edit.Operation == ContentTextEditOperation.Remove)
        {
            if (!isDeclared)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Language '{target.Language}' is neither declared by version {context.Active} nor introduced by the open draft, so there is nothing to remove in it. A Set is what declares a language."),
                    target.Type,
                    id,
                    ContentAuthoringException.TextLanguageUndeclaredReason);
            }

            if (standing is null
                && (id == 0 || !await HoldsValueAsync(target.Type, id, field.Name, target.Language, context.Active, transaction, cancellationToken)
                    .ConfigureAwait(false)))
            {
                return;
            }
        }

        long at = Millis(_clock());
        if (!isDeclared)
        {
            using SqliteCommand introduce = Command(
                """
                INSERT INTO catalog_draft_text_language(language_tag, wire_tag, created_at_utc)
                VALUES ($language, $language, $at);
                """,
                transaction);
            Bind(introduce, "$language", target.Language);
            Bind(introduce, "$at", at);
            await introduce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SqliteCommand write = Command(
            standing is null
                ? """
                  INSERT INTO catalog_draft_text_edit(
                      type_id, content_key, field_name, language_tag, operation, text_value, edited_by,
                      created_at_utc, updated_at_utc)
                  VALUES ($type, $key, $field, $language, $operation, $value, $actor, $at, $at);
                  """
                : """
                  UPDATE catalog_draft_text_edit
                  SET operation = $operation, text_value = $value, edited_by = $actor, updated_at_utc = $at
                  WHERE edit_ordinal = $ordinal;
                  """,
            transaction))
        {
            BindTarget(write, target);
            Bind(write, "$operation", (long)edit.Operation);
            Bind(write, "$value", edit.Value);
            Bind(write, "$actor", context.Actor);
            Bind(write, "$at", at);
            Bind(write, "$ordinal", standing);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            transaction,
            ContentAuditActions.DraftEdit,
            context.Actor,
            context.OperatorId,
            target.Type,
            id,
            target.Key,
            target.FieldName,
            null,
            ContentTextAuditRendering.Render(edit.Value),
            0,
            context.Note,
            target.Language,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The draft's text intents in first-applied order and its introductions. The caller holds the lease.</summary>
    async Task<ContentDraftTextState> ReadDraftTextAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var edits = new List<ContentTextEdit>();
        using (SqliteCommand command = Command(
            """
            SELECT type_id, content_key, field_name, language_tag, operation, text_value
            FROM catalog_draft_text_edit
            ORDER BY edit_ordinal;
            """,
            transaction))
        {
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var target = new ContentTextTarget(
                    new ContentTypeId((ushort)reader.GetInt64(0)),
                    new ContentKey(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3));
                edits.Add((ContentTextEditOperation)reader.GetInt64(4) == ContentTextEditOperation.Set
                    ? ContentTextEdit.Set(target, reader.GetString(5))
                    : ContentTextEdit.Remove(target));
            }
        }

        var introductions = new List<ContentTextLanguageDeclaration>();
        using (SqliteCommand command = Command(
            "SELECT language_tag, wire_tag FROM catalog_draft_text_language ORDER BY language_ordinal;", transaction))
        {
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                introductions.Add(new ContentTextLanguageDeclaration(reader.GetString(0), reader.GetString(1)));
            }
        }

        return edits.Count == 0 && introductions.Count == 0
            ? ContentDraftTextState.Empty
            : new ContentDraftTextState(edits, introductions);
    }

    /// <summary>The standing intent's ordinal for one canonical target, or null.</summary>
    async Task<long?> ReadStandingTextAsync(
        ContentTextTarget target,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT edit_ordinal FROM catalog_draft_text_edit
            WHERE type_id = $type AND content_key = $key AND field_name = $field AND language_tag = $language;
            """,
            transaction);
        BindTarget(command, target);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long ordinal
            ? ordinal
            : null;
    }

    async Task<bool> IntroducedAsync(string language, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT 1 FROM catalog_draft_text_language WHERE language_tag = $language;", transaction);
        Bind(command, "$language", language);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>The id of the row live at a version under one key, retired included, or 0.</summary>
    async Task<int> LiveRowIdAsync(
        ContentTypeId type,
        ContentKey key,
        int versionNumber,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT definition_id FROM catalog_row
            WHERE type_id = $type AND content_key = $key AND valid_from_version <= $at
              AND (replaced_in_version IS NULL OR replaced_in_version > $at)
            LIMIT 1;
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$key", key.ToString());
        Bind(command, "$at", (long)versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long id ? (int)id : 0;
    }

    /// <summary>Whether one string holds a value live at a version.</summary>
    async Task<bool> HoldsValueAsync(
        ContentTypeId type,
        int definitionId,
        string fieldName,
        string language,
        int versionNumber,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT 1 FROM catalog_text
            WHERE type_id = $type AND definition_id = $id AND field_name = $field AND language_tag = $language
              AND valid_from_version <= $at AND (replaced_in_version IS NULL OR replaced_in_version > $at);
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$id", (long)definitionId);
        Bind(command, "$field", fieldName);
        Bind(command, "$language", language);
        Bind(command, "$at", (long)versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    async Task<int> ReadActiveAsync(SqliteTransaction? transaction, CancellationToken cancellationToken)
        => (int)await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);

    async Task<string> ReadEpochAsync(SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT store_epoch FROM catalog_metadata WHERE metadata_key = 1;", transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw NoMetadata();
    }

    Task AppendDiscardAuditAsync(
        SqliteTransaction transaction,
        string actor,
        string operatorId,
        string fieldName,
        int count,
        CancellationToken cancellationToken)
        => AppendAuditAsync(
            transaction, ContentAuditActions.DraftDiscard, actor, operatorId, default, 0, default, fieldName,
            Render(count), null, 0, string.Empty, cancellationToken);

    static void BindTarget(SqliteCommand command, ContentTextTarget target)
    {
        Bind(command, "$type", (long)target.Type.Value);
        Bind(command, "$key", target.Key.ToString());
        Bind(command, "$field", target.FieldName);
        Bind(command, "$language", target.Language);
    }

    /// <summary>The rows a draft adds or forks into, by key, which text may name before they have ids.</summary>
    static HashSet<(ushort, ContentKey)> PendingKeys(IReadOnlyList<ContentEdit> edits)
    {
        var keys = new HashSet<(ushort, ContentKey)>();
        foreach (ContentEdit edit in edits)
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

    static ContentAuthoringException Unavailable(string member)
        => new(
            FormattableString.Invariant(
                $"{nameof(SqliteContentAuthoringStore)}.{member} is not available in this build, so it is refused whole rather than run without its text."),
            default,
            0,
            ContentAuthoringException.TextOperationUnavailableReason);

    /// <summary>What every text intent of one batch is checked against.</summary>
    /// <param name="Declared">The canonical languages the active version declares.</param>
    /// <param name="Pending">The rows the draft adds or forks into, by key.</param>
    /// <param name="Active">The active version.</param>
    /// <param name="Actor">What the engine authenticated.</param>
    /// <param name="OperatorId">The identity the console forwarded.</param>
    /// <param name="Note">The operator's note.</param>
    sealed record TextApply(
        HashSet<string> Declared,
        HashSet<(ushort, ContentKey)> Pending,
        int Active,
        string Actor,
        string OperatorId,
        string Note);
}
