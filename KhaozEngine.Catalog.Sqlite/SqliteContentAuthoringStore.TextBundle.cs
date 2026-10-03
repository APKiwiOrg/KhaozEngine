using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The COMPANION IMPORT of the SQLite store: a complete bundle, rows and text together, into an empty
/// database. It is the only route a text-bearing bundle lands through, and it never runs the row-only import
/// and appends text afterwards, because a failure between the two would leave a version without its text.
/// <para>
/// <b>It shares the row import's one core</b> in <c>SqliteContentAuthoringStore.Bundle.cs</c>. The bundle's
/// format is resolved first, a format 1 bundle converting explicitly to empty text, and every value's target
/// is checked against the bundle's rows and this registry before anything is staged. The row edits, one Set
/// per value with its audit row, and every declared language with its exact wire spelling, empty ones
/// included, then land in ONE draft transaction, which the ordinary text publish commits as a complete
/// version 1. Any refusal after staging began resets the database to empty.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// A format 1 bundle imports as empty text. A format 2 bundle that lost its section, a later format, an
    /// open draft holding any work, a value naming a row the bundle does not carry and an ineligible target are
    /// refused before anything is staged. Any refusal after staging began resets this database to empty, exactly as the row import does.
    /// </remarks>
    public async Task<ContentPublishResult> ImportTextBundleAsync(
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
        return await ImportAsync(bundle, text, actor, operatorId, note, nameof(ImportTextBundleAsync), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The import's draft in ONE transaction: the row edits through the ordinary apply body, then, on the
    /// companion route, the bundle's text. A failure rolls the whole draft back, and the caller's reset takes
    /// back the staging. The caller holds the lease it checked pending work and staged under.
    /// </summary>
    /// <param name="edits">One import edit per bundle row.</param>
    /// <param name="text">The bundle's complete text, or null on the row-only route.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    async Task ApplyImportDraftAsync(
        IReadOnlyList<ContentEdit> edits,
        ContentBundleTextState? text,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            CheckAgainstSchema(edits[i]);
        }

        using SqliteTransaction transaction = _connection.BeginTransaction();
        await ApplyEditsInAsync(edits, actor, operatorId, note, transaction, cancellationToken).ConfigureAwait(false);
        if (text is not null)
        {
            await ApplyImportedTextAsync(text, actor, operatorId, note, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <summary>
    /// The bundle's text onto the draft the import's row edits just opened: one Set per value, audited with its
    /// language, and every declared language as an introduction with its exact wire spelling, so a historical
    /// <c>en-US</c> publishes as <c>en-US</c> and an empty language still publishes its empty chunk. The targets
    /// were checked before staging, and the rows they name are the draft's own adds, so a value carries no id
    /// yet. The caller owns the transaction.
    /// </summary>
    async Task ApplyImportedTextAsync(
        ContentBundleTextState text,
        string actor,
        string operatorId,
        string note,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        long at = Millis(_clock());
        foreach (ContentBundleTextValue value in text.Values)
        {
            using (SqliteCommand insert = Command(
                """
                INSERT INTO catalog_draft_text_edit(
                    type_id, content_key, field_name, language_tag, operation, text_value, edited_by,
                    created_at_utc, updated_at_utc)
                VALUES ($type, $key, $field, $language, $operation, $value, $actor, $at, $at);
                """,
                transaction))
            {
                BindTarget(insert, value.Target);
                Bind(insert, "$operation", (long)ContentTextEditOperation.Set);
                Bind(insert, "$value", value.Value);
                Bind(insert, "$actor", actor);
                Bind(insert, "$at", at);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendAuditAsync(
                transaction,
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
                value.Target.Language,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (ContentTextLanguageDeclaration language in text.Languages)
        {
            using SqliteCommand introduce = Command(
                """
                INSERT INTO catalog_draft_text_language(language_tag, wire_tag, created_at_utc)
                VALUES ($language, $wire, $at);
                """,
                transaction);
            Bind(introduce, "$language", language.Language);
            Bind(introduce, "$wire", language.WireTag);
            Bind(introduce, "$at", at);
            await introduce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
