using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The COMPANION IMPORT of the SQL Server store: a complete bundle, rows and text together, into an empty
/// database. It is the only route a text-bearing bundle lands through, and it never runs the row-only import
/// and appends text afterwards, because a failure between the two would leave a version without its text.
/// <para>
/// <b>It shares the row import's one core</b> in <c>SqlServerContentAuthoringStore.Bundle.cs</c>. The bundle's
/// format is resolved first, a format 1 bundle converting explicitly to empty text, and every value's target
/// is checked against the bundle's rows and this registry before anything is staged. The row edits, one Set
/// per value with its audit row, and every declared language with its exact wire spelling, empty ones
/// included, then land in ONE Serializable draft transaction, which the ordinary text publish commits as a
/// complete version 1. Any refusal after staging began resets the database to empty.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    /// <remarks>
    /// A format 1 bundle imports as empty text. A format 2 bundle that lost its section, a later format, an
    /// open draft holding any work, a value naming a row the bundle does not carry and an ineligible target are
    /// refused before anything is staged. Any refusal after staging began resets this database to empty,
    /// exactly as the row import does.
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
    /// The import's draft inside the caller's scope, the one it checked pending work and staged in: the row
    /// edits through the ordinary apply body, then, on the companion route, the bundle's text. A failure rolls
    /// the whole scope back, and the caller's reset takes back the cached rules.
    /// </summary>
    /// <param name="scope">The caller's connection and Serializable transaction.</param>
    /// <param name="edits">One import edit per bundle row.</param>
    /// <param name="text">The bundle's complete text, or null on the row-only route.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    async Task ApplyImportDraftAsync(
        SqlServerCatalogScope scope,
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

        await ApplyEditsInAsync(scope, edits, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
        if (text is not null)
        {
            await ApplyImportedTextAsync(scope, text, actor, operatorId, note, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The bundle's text onto the draft the import's row edits just opened: one Set per value, audited with its
    /// language, and every declared language as an introduction with its exact wire spelling, so a historical
    /// <c>en-US</c> publishes as <c>en-US</c> and an empty language still publishes its empty chunk. The targets
    /// were checked before staging, and the rows they name are the draft's own adds, so a value carries no id
    /// yet. The caller owns the transaction.
    /// </summary>
    async Task ApplyImportedTextAsync(
        SqlServerCatalogScope scope,
        ContentBundleTextState text,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken)
    {
        DateTimeOffset at = _clock();
        foreach (ContentBundleTextValue value in text.Values)
        {
            await using (SqlCommand insert = Command(
                scope,
                """
                INSERT INTO dbo.catalog_draft_text_edit(
                    type_id, content_key, field_name, language_tag, operation, string_value, edited_by,
                    created_at_utc, updated_at_utc)
                VALUES (@type, @key, @field, @language, @operation, @textValue, @actor, @at, @at);
                """))
            {
                BindTarget(insert, value.Target);
                BindInt(insert, "@operation", (int)ContentTextEditOperation.Set);
                BindLargeText(insert, "@textValue", value.Value);
                BindText(insert, "@actor", actor);
                BindTime(insert, "@at", at);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await AppendAuditAsync(
                scope,
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
            await using SqlCommand introduce = Command(
                scope,
                """
                INSERT INTO dbo.catalog_draft_text_language(language_tag, wire_tag, created_at_utc)
                VALUES (@language, @wire, @at);
                """);
            BindText(introduce, "@language", language.Language);
            BindText(introduce, "@wire", language.WireTag);
            BindTime(introduce, "@at", at);
            await introduce.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
