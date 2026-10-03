using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The TEXT COMMIT of the SQL Server store: the confirmation a supplied <see cref="ContentTextPublishPlan"/>
/// meets against this store's actual state, the text a commit writes, and the row-only commit's carry of the
/// base version's text.
/// <para>
/// <b>One commit core serves both routes.</b> A text plan and a row-only plan differ only in the text they
/// stage before <c>CommitCoreAsync</c> runs, so the version, the rows, the text revisions, every language
/// mapping, the audit, the upgrade ledger and the draft land in one Serializable transaction, the version is
/// recorded text complete, and the active pointer moves last.
/// </para>
/// <para>
/// <b>Nothing is deleted before the complete frozen draft is confirmed.</b> The epoch, the version number,
/// the base, the frozen rows, text intents and introductions, the base's text and every chunk are compared
/// with the store's own state first, inside the commit's transaction.
/// </para>
/// <para>
/// <b>A value compares by its length too.</b> SQL Server pads the shorter string before an <c>=</c> compares,
/// so two values differing only in trailing blanks would read as equal. Every statement that matches a value
/// also matches its <c>DATALENGTH</c>, which makes the match exact. That is an equality of two stored
/// strings, not a measure of a value's UTF-8 bound, which the domain enforces before any statement.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        return WriteAsync(
            async (scope, token) =>
            {
                if (!string.Equals(
                    plan.StoreEpoch, await ReadEpochAsync(scope, token).ConfigureAwait(false), StringComparison.Ordinal))
                {
                    throw TextMismatch("it was frozen in another store");
                }

                IReadOnlyList<RemapRule> held = await ConfirmNumberAsync(scope, plan.RowPlan, token).ConfigureAwait(false);
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                if (plan.BaseVersion != active)
                {
                    throw Moved(FormattableString.Invariant(
                        $"The text plan stands on version {plan.BaseVersion} and the store stands at {active}."));
                }

                if (await ReadDraftAsync(scope, token).ConfigureAwait(false) is not ContentDraft open
                    || open.FrozenForBaseVersion != active
                    || !ContentTextCompatibility.SameDraft(plan.Snapshot.Draft, open))
                {
                    throw TextMismatch("the open draft is not the complete draft the plan froze");
                }

                ContentVersionTextSnapshot baseline = await ReadTextSnapshotAtAsync(scope, active, token)
                    .ConfigureAwait(false);
                if (!SameText(baseline, plan.Snapshot.BaselineText))
                {
                    throw TextMismatch("the base version's text or languages are not the ones the plan read");
                }

                // Every chunk is regenerated from the plan's own values, never trusted.
                ContentTextChunkConfirmation.Require(_registry, plan);
                foreach (ContentTextRevision insert in plan.TextInserts)
                {
                    _ = ContentTextTargetEligibility.Require(_registry, new ContentTextTarget(
                        insert.Type, KeyOf(plan.RowPlan, insert), insert.FieldName, insert.Language));
                }

                var text = new ContentTextCommit(plan.Languages, plan.TextCloses, plan.TextInserts, plan.FrozenText);
                return await CommitCoreAsync(scope, plan.RowPlan, request, pointers, text, held, token)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// The text a ROW-ONLY commit carries: the base version's languages unchanged, after refusing a draft or a
    /// frozen fork that needs text, a base whose text is unknown, and a plan whose manifests name other text
    /// chunks than the base recorded. The caller owns the transaction.
    /// </summary>
    async Task<ContentTextCommit> StageRowOnlyTextAsync(
        SqlServerCatalogScope scope,
        ContentPublishPlan plan,
        string member,
        CancellationToken cancellationToken)
    {
        await RequireRowOnlyRepresentableAsync(
            scope, await ReadDraftAsync(scope, cancellationToken).ConfigureAwait(false), member, cancellationToken)
            .ConfigureAwait(false);
        await RequireNoTextForkAsync(scope, plan.FrozenEdits, member, cancellationToken).ConfigureAwait(false);

        int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentTextLanguage> languages = (await ReadTextSnapshotAtAsync(
            scope, active, cancellationToken).ConfigureAwait(false)).Languages;
        bool agrees = plan.Languages.Count == languages.Count;
        for (int i = 0; agrees && i < languages.Count; i++)
        {
            agrees = string.Equals(plan.Languages[i].Tag, languages[i].WireTag, StringComparison.Ordinal)
                && string.Equals(plan.Languages[i].TextHash, languages[i].Hash, StringComparison.Ordinal);
        }

        if (!agrees)
        {
            throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                $"the plan names {plan.Languages.Count} text chunk(s) that are not the {languages.Count} version {active} recorded"));
        }

        return new ContentTextCommit(languages, [], [], null);
    }

    /// <summary>
    /// A commit's text: every close at the new version, every insert, and the new version's complete language
    /// record, each at the version's publish time. The caller owns the transaction.
    /// </summary>
    static async Task WriteTextAsync(
        SqlServerCatalogScope scope,
        int versionNumber,
        ContentTextCommit text,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        foreach (ContentTextRevision close in text.Closes)
        {
            await using SqlCommand command = Command(
                scope,
                """
                UPDATE dbo.catalog_text SET replaced_in_version = @replaced, updated_at_utc = @at
                WHERE type_id = @type AND definition_id = @id AND field_name = @field AND language_tag = @language
                  AND valid_from_version = @from AND replaced_in_version IS NULL
                  AND string_value = @textValue AND DATALENGTH(string_value) = DATALENGTH(@textValue);
                """);
            BindRevision(command, close);
            BindInt(command, "@replaced", versionNumber);
            BindTime(command, "@at", at);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw TextMismatch("a text close names a revision this store does not hold live");
            }
        }

        foreach (ContentTextRevision insert in text.Inserts)
        {
            await using SqlCommand command = Command(
                scope,
                """
                INSERT INTO dbo.catalog_text(
                    type_id, definition_id, field_name, language_tag, valid_from_version, replaced_in_version,
                    string_value, created_at_utc, updated_at_utc)
                VALUES (@type, @id, @field, @language, @from, NULL, @textValue, @at, @at);
                """);
            BindRevision(command, insert);
            BindTime(command, "@at", at);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ContentTextLanguage language in text.Languages)
        {
            await using SqlCommand command = Command(
                scope,
                """
                INSERT INTO dbo.catalog_text_chunk(version_number, language_tag, wire_tag, chunk_hash, created_at_utc)
                VALUES (@version, @language, @wire, @hash, @at);
                """);
            BindInt(command, "@version", versionNumber);
            BindText(command, "@language", language.Language);
            BindText(command, "@wire", language.WireTag);
            BindText(command, "@hash", language.Hash);
            BindTime(command, "@at", at);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The text half of the publish audit: one entry per string that changed, carrying its language, its old
    /// and new values abbreviated for the column, and the version. Full values stay in text history.
    /// </summary>
    async Task AppendTextPublishAuditAsync(
        SqlServerCatalogScope scope,
        ContentTextCommit text,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken)
    {
        var inserted = new List<ContentTextRevision>(text.Inserts);
        foreach (ContentTextRevision close in text.Closes)
        {
            int index = inserted.FindIndex(insert => insert.IsSameString(close));
            ContentTextRevision? replacement = index < 0 ? null : inserted[index];
            if (index >= 0)
            {
                inserted.RemoveAt(index);
            }

            await AppendTextChangeAsync(scope, plan, request, close, close.Value, replacement?.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (ContentTextRevision insert in inserted)
        {
            await AppendTextChangeAsync(scope, plan, request, insert, null, insert.Value, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    Task AppendTextChangeAsync(
        SqlServerCatalogScope scope,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        ContentTextRevision revision,
        string? before,
        string? after,
        CancellationToken cancellationToken)
        => AppendAuditAsync(
            scope,
            ContentAuditActions.Publish,
            request.Actor,
            request.Operator,
            revision.Type,
            revision.DefinitionId,
            KeyOf(plan, revision),
            revision.FieldName,
            ContentTextAuditRendering.Render(before),
            ContentTextAuditRendering.Render(after),
            plan.VersionNumber,
            request.Note,
            revision.Language,
            cancellationToken);

    /// <summary>
    /// The text intents and introductions a commit published, deleted by exact match, so an intent the commit
    /// did not freeze survives by the same rule the rows follow. The caller owns the transaction.
    /// </summary>
    static async Task DeletePublishedTextAsync(
        SqlServerCatalogScope scope,
        ContentDraftTextState published,
        CancellationToken cancellationToken)
    {
        foreach (ContentTextEdit edit in published.Edits)
        {
            await using SqlCommand command = Command(
                scope,
                """
                DELETE FROM dbo.catalog_draft_text_edit
                WHERE type_id = @type AND content_key = @key AND field_name = @field AND language_tag = @language
                  AND operation = @operation
                  AND ((string_value IS NULL AND @textValue IS NULL)
                       OR (string_value = @textValue AND DATALENGTH(string_value) = DATALENGTH(@textValue)));
                """);
            BindTarget(command, edit.Target);
            BindInt(command, "@operation", (int)edit.Operation);
            BindLargeText(command, "@textValue", edit.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ContentTextLanguageDeclaration introduction in published.Introductions)
        {
            await using SqlCommand command = Command(
                scope,
                "DELETE FROM dbo.catalog_draft_text_language WHERE language_tag = @language AND wire_tag = @wire;");
            BindText(command, "@language", introduction.Language);
            BindText(command, "@wire", introduction.WireTag);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static void BindRevision(SqlCommand command, ContentTextRevision revision)
    {
        BindInt(command, "@type", (int)revision.Type.Value);
        BindInt(command, "@id", revision.DefinitionId);
        BindText(command, "@field", revision.FieldName);
        BindText(command, "@language", revision.Language);
        BindInt(command, "@from", revision.ValidFromVersion);
        BindLargeText(command, "@textValue", revision.Value);
    }

    /// <summary>The key of the row a revision belongs to, as the plan's live rows name it.</summary>
    static ContentKey KeyOf(ContentPublishPlan plan, ContentTextRevision revision)
    {
        foreach (ContentRowRevision live in plan.LiveRows)
        {
            if (live.Row.Type == revision.Type && live.Row.Id == revision.DefinitionId)
            {
                return live.Row.Key;
            }
        }

        throw TextMismatch(FormattableString.Invariant(
            $"text names type {revision.Type.Value} row {revision.DefinitionId}, which the plan holds no live row of"));
    }

    /// <summary>Whether two snapshots record the same version, languages in order and revisions as a set.</summary>
    static bool SameText(ContentVersionTextSnapshot actual, ContentVersionTextSnapshot expected)
    {
        if (actual.VersionNumber != expected.VersionNumber
            || actual.Languages.Count != expected.Languages.Count
            || actual.Revisions.Count != expected.Revisions.Count)
        {
            return false;
        }

        for (int i = 0; i < actual.Languages.Count; i++)
        {
            if (!actual.Languages[i].Equals(expected.Languages[i]))
            {
                return false;
            }
        }

        var held = new List<ContentTextRevision>(actual.Revisions);
        foreach (ContentTextRevision revision in expected.Revisions)
        {
            if (!held.Remove(revision))
            {
                return false;
            }
        }

        return true;
    }

    static ContentAuthoringException TextMismatch(string detail)
        => new(
            FormattableString.Invariant(
                $"The text commit is refused because {detail}. Nothing was written, and the plan is rebuilt from a fresh freeze."),
            default,
            0,
            ContentAuthoringException.TextStateMismatchReason);

    /// <summary>What one commit writes for text beside its rows.</summary>
    /// <param name="Languages">The new version's complete language record, in manifest order.</param>
    /// <param name="Closes">The baseline revisions the version replaces.</param>
    /// <param name="Inserts">The revisions the version adds.</param>
    /// <param name="PublishedText">The frozen text state the commit consumes from the draft, or null on a row-only commit.</param>
    sealed record ContentTextCommit(
        IReadOnlyList<ContentTextLanguage> Languages,
        IReadOnlyList<ContentTextRevision> Closes,
        IReadOnlyList<ContentTextRevision> Inserts,
        ContentDraftTextState? PublishedText);
}
