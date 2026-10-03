using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The TEXT AUTHORING half of the SQL Server store: the atomic mixed apply, the complete freeze, the
/// exact-version snapshot read and the atomic expected-draft discard of <see cref="IContentTextAuthoringStore"/>,
/// mirroring the in-memory reference and the SQLite provider.
/// <para>
/// <b>Every member is one Serializable transaction on its own connection.</b> A batch's row intents, text
/// intents, language introductions and audit rows land together or not at all, so a refused intent, an audit
/// fault or a clock failure leaves the draft and the audit exactly as they were.
/// </para>
/// <para>
/// <b>Intents keep their first ordinal.</b> <c>catalog_draft_text_edit</c> holds one row per canonical target
/// in first-applied order, and a later intent for the target rewrites that row in place, keeping its creation
/// time. An introduction is a row of its own in <c>catalog_draft_text_language</c>, independent of the
/// intents, so a Set replaced by a Remove still publishes its language.
/// </para>
/// <para>
/// <b>Every value is bound as <c>nvarchar(max)</c> and every nullable one through a typed binder</b>, so a
/// Remove's NULL value is an <c>nvarchar(max)</c> NULL rather than whatever SqlClient would infer. A value's
/// strict UTF-8 bound is the domain's to enforce before any statement, never the column's.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore : IContentTextAuthoringStore
{
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

        for (int i = 0; i < changes.RowEdits.Count; i++)
        {
            CheckAgainstSchema(changes.RowEdits[i]);
        }

        return WriteAsync(
            (scope, token) => ApplyChangesInAsync(scope, changes, actor, operatorId, note, token),
            cancellationToken);
    }

    /// <summary>
    /// The body of <see cref="ApplyChangesAsync"/> inside the caller's transaction, which is what lets the
    /// companion rollback read both versions, plan and apply its changes in one step. The row edits are already
    /// schema checked.
    /// </summary>
    async Task<ContentDraft> ApplyChangesInAsync(
        SqlServerCatalogScope scope,
        ContentAuthoringChanges changes,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        await RequireNotFrozenAsync(scope, nameof(ApplyChangesAsync), cancellationToken).ConfigureAwait(false);
        await OpenDraftAsync(scope, actor, note, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < changes.RowEdits.Count; i++)
        {
            ContentEdit edit = changes.RowEdits[i];
            await ApplyOneAsync(scope, edit, actor, cancellationToken).ConfigureAwait(false);
            await AppendEditAuditAsync(scope, edit, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
        }

        if (changes.TextEdits.Count > 0)
        {
            int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);
            var declared = new HashSet<string>(StringComparer.Ordinal);
            ContentVersionTextSnapshot baseline = await ReadTextSnapshotAtAsync(scope, active, cancellationToken)
                .ConfigureAwait(false);
            foreach (ContentTextLanguage language in baseline.Languages)
            {
                declared.Add(language.Language);
            }

            HashSet<(ushort, ContentKey)> pending = PendingKeys(
                await ReadEditsAsync(scope, cancellationToken).ConfigureAwait(false));
            var context = new TextApply(declared, pending, active, actor, operatorId, note);
            for (int i = 0; i < changes.TextEdits.Count; i++)
            {
                await ApplyTextAsync(scope, changes.TextEdits[i], context, cancellationToken).ConfigureAwait(false);
            }
        }

        return await RequireDraftAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentTextPublishSnapshot> FreezeChangesAsync(
        int expectedBaseVersion,
        CancellationToken cancellationToken = default)
    {
        await ClearStaleFreezeAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(
            async (scope, token) =>
            {
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                if (expectedBaseVersion != active)
                {
                    throw Moved(FormattableString.Invariant(
                        $"The publish expects base version {expectedBaseVersion} and the store stands at {active}. Another publish landed in between, so this draft is against a version that is no longer the base."));
                }

                if (await ReadDraftAsync(scope, token).ConfigureAwait(false) is not ContentDraft open
                    || open.TotalWorkCount == 0)
                {
                    throw new ContentAuthoringException(
                        "There is no open draft with pending row work, text work or language introductions, so there is nothing to publish.",
                        default,
                        0,
                        ContentAuthoringException.NoOpenDraftReason);
                }

                // Read BEFORE the marker, so a base whose text is unknown refuses with nothing written.
                ContentVersionTextSnapshot baselineText = await ReadTextSnapshotAtAsync(scope, active, token)
                    .ConfigureAwait(false);

                await using (SqlCommand freeze = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_draft
                    SET frozen_for_base_version = @base,
                        updated_at_utc = CASE WHEN frozen_for_base_version = @base THEN updated_at_utc ELSE @now END
                    WHERE draft_key = 1;
                    """))
                {
                    BindInt(freeze, "@base", active);
                    BindTime(freeze, "@now", _clock());
                    await freeze.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return new ContentTextPublishSnapshot(
                    await ReadEpochAsync(scope, token).ConfigureAwait(false),
                    await ReadBaselineAsync(scope, token).ConfigureAwait(false),
                    baselineText,
                    await RequireDraftAsync(scope, token).ConfigureAwait(false));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(versionNumber);
        return ReadAsync(
            async (scope, token) =>
            {
                if (versionNumber == NoActiveVersion
                    || !await VersionExistsAsync(scope, versionNumber, token).ConfigureAwait(false))
                {
                    throw UnknownVersion(versionNumber);
                }

                return await ReadTextSnapshotAtAsync(scope, versionNumber, token).ConfigureAwait(false);
            },
            cancellationToken);
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

        // The comparison and the delete share ONE Serializable transaction, so a rival that reads or writes the
        // draft in between waits for it or is chosen as the deadlock victim, and nothing lands between them.
        return WriteAsync(
            async (scope, token) =>
            {
                if (await ReadDraftAsync(scope, token).ConfigureAwait(false) is not ContentDraft open
                    || open.IsFrozen
                    || !ContentTextCompatibility.SameDraft(expected, open))
                {
                    return false;
                }

                await DeleteDraftAsync(scope, token).ConfigureAwait(false);
                await AppendDiscardAuditAsync(scope, actor, operatorId, string.Empty, open.EditCount, token)
                    .ConfigureAwait(false);
                if (open.TextEditCount > 0)
                {
                    await AppendDiscardAuditAsync(scope, actor, operatorId, "text-edits", open.TextEditCount, token)
                        .ConfigureAwait(false);
                }

                if (open.LanguageIntroductionCount > 0)
                {
                    await AppendDiscardAuditAsync(
                        scope, actor, operatorId, "language-introductions", open.LanguageIntroductionCount, token)
                        .ConfigureAwait(false);
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// One text intent applied inside the batch's transaction, after its eligibility, row and language checks.
    /// An idempotent Remove changes nothing and audits nothing. The caller owns the transaction.
    /// </summary>
    async Task ApplyTextAsync(
        SqlServerCatalogScope scope,
        ContentTextEdit edit,
        TextApply context,
        CancellationToken cancellationToken)
    {
        ContentTextTarget target = edit.Target;
        ContentFieldEntry field = ContentTextTargetEligibility.Require(_registry, target);
        int id = await LiveRowIdAsync(scope, target.Type, target.Key, context.Active, cancellationToken)
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
            || await IntroducedAsync(scope, target.Language, cancellationToken).ConfigureAwait(false);
        long? standing = await ReadStandingTextAsync(scope, target, cancellationToken).ConfigureAwait(false);
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
                && (id == 0 || !await HoldsValueAsync(scope, target.Type, id, field.Name, target.Language, context.Active, cancellationToken)
                    .ConfigureAwait(false)))
            {
                return;
            }
        }

        DateTimeOffset at = _clock();
        if (!isDeclared)
        {
            await using SqlCommand introduce = Command(
                scope,
                """
                INSERT INTO dbo.catalog_draft_text_language(language_tag, wire_tag, created_at_utc)
                VALUES (@language, @wire, @at);
                """);
            BindText(introduce, "@language", target.Language);
            BindText(introduce, "@wire", target.Language);
            BindTime(introduce, "@at", at);
            await introduce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqlCommand write = Command(
            scope,
            standing is null
                ? """
                  INSERT INTO dbo.catalog_draft_text_edit(
                      type_id, content_key, field_name, language_tag, operation, string_value, edited_by,
                      created_at_utc, updated_at_utc)
                  VALUES (@type, @key, @field, @language, @operation, @textValue, @actor, @at, @at);
                  """
                : """
                  UPDATE dbo.catalog_draft_text_edit
                  SET operation = @operation, string_value = @textValue, edited_by = @actor, updated_at_utc = @at
                  WHERE edit_ordinal = @ordinal;
                  """))
        {
            BindTarget(write, target);
            BindInt(write, "@operation", (int)edit.Operation);
            BindLargeText(write, "@textValue", edit.Value);
            BindText(write, "@actor", context.Actor);
            BindTime(write, "@at", at);
            BindBigInt(write, "@ordinal", standing);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            scope,
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

    /// <summary>The draft's text intents in first-applied order and its introductions. The caller owns the scope.</summary>
    static async Task<ContentDraftTextState> ReadDraftTextAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        var edits = new List<ContentTextEdit>();
        await using (SqlCommand command = Command(
            scope,
            """
            SELECT type_id, content_key, field_name, language_tag, operation, string_value
            FROM dbo.catalog_draft_text_edit
            ORDER BY edit_ordinal;
            """))
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var target = new ContentTextTarget(
                    new ContentTypeId((ushort)reader.GetInt32(0)),
                    new ContentKey(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3));
                edits.Add((ContentTextEditOperation)reader.GetInt32(4) == ContentTextEditOperation.Set
                    ? ContentTextEdit.Set(target, reader.GetString(5))
                    : ContentTextEdit.Remove(target));
            }
        }

        var introductions = new List<ContentTextLanguageDeclaration>();
        await using (SqlCommand command = Command(
            scope,
            "SELECT language_tag, wire_tag FROM dbo.catalog_draft_text_language ORDER BY language_ordinal;"))
        {
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
    static async Task<long?> ReadStandingTextAsync(
        SqlServerCatalogScope scope,
        ContentTextTarget target,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT edit_ordinal FROM dbo.catalog_draft_text_edit
            WHERE type_id = @type AND content_key = @key AND DATALENGTH(content_key) = DATALENGTH(@key)
              AND field_name = @field AND language_tag = @language;
            """);
        BindTarget(command, target);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long ordinal
            ? ordinal
            : null;
    }

    static async Task<bool> IntroducedAsync(
        SqlServerCatalogScope scope,
        string language,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "SELECT 1 FROM dbo.catalog_draft_text_language WHERE language_tag = @language;");
        BindText(command, "@language", language);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>The id of the row live at a version under one key, retired included, or 0.</summary>
    static async Task<int> LiveRowIdAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        ContentKey key,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT TOP (1) definition_id FROM dbo.catalog_row
            WHERE type_id = @type AND content_key = @key AND DATALENGTH(content_key) = DATALENGTH(@key)
              AND valid_from_version <= @live
              AND (replaced_in_version IS NULL OR replaced_in_version > @live);
            """);
        BindInt(command, "@type", (int)type.Value);
        BindText(command, "@key", key.ToString());
        BindInt(command, "@live", versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int id ? id : 0;
    }

    /// <summary>Whether one string holds a value live at a version.</summary>
    static async Task<bool> HoldsValueAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        int definitionId,
        string fieldName,
        string language,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT 1 FROM dbo.catalog_text
            WHERE type_id = @type AND definition_id = @id AND field_name = @field AND language_tag = @language
              AND valid_from_version <= @live AND (replaced_in_version IS NULL OR replaced_in_version > @live);
            """);
        BindInt(command, "@type", (int)type.Value);
        BindInt(command, "@id", definitionId);
        BindText(command, "@field", fieldName);
        BindText(command, "@language", language);
        BindInt(command, "@live", versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    static async Task<string> ReadEpochAsync(SqlServerCatalogScope scope, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "SELECT store_epoch FROM dbo.catalog_metadata WHERE metadata_key = 1;");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw NoMetadata();
    }

    Task AppendDiscardAuditAsync(
        SqlServerCatalogScope scope,
        string actor,
        string operatorId,
        string fieldName,
        int count,
        CancellationToken cancellationToken)
        => AppendAuditAsync(
            scope, ContentAuditActions.DraftDiscard, actor, operatorId, default, 0, default, fieldName,
            Render(count), null, 0, string.Empty, cancellationToken);

    static void BindTarget(SqlCommand command, ContentTextTarget target)
    {
        BindInt(command, "@type", (int)target.Type.Value);
        BindText(command, "@key", target.Key.ToString());
        BindText(command, "@field", target.FieldName);
        BindText(command, "@language", target.Language);
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
