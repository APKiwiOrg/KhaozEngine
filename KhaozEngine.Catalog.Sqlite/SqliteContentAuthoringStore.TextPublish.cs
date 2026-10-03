using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The TEXT COMMIT of the SQLite store: the confirmation a supplied <see cref="ContentTextPublishPlan"/> meets
/// against this store's actual state, the text a commit writes, and the row-only commit's carry of the base
/// version's text.
/// <para>
/// <b>One commit core serves both routes.</b> A text plan and a row-only plan differ only in the text they
/// stage before <c>CommitCoreAsync</c> runs, so the version, the rows, the text revisions, every language
/// mapping, the audit, the upgrade ledger and the draft land in one transaction, the version is recorded
/// text complete, and the active pointer moves last.
/// </para>
/// <para>
/// <b>Nothing is deleted before the complete frozen draft is confirmed.</b> The epoch, the version number,
/// the base, the frozen rows, text intents and introductions, the base's text and every chunk are compared
/// with the store's own state first, inside the commit's transaction.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public async Task<ContentVersionRecord> CommitTextPublishAsync(
        ContentTextPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (!string.Equals(
            plan.StoreEpoch, await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
        {
            throw TextMismatch("it was frozen in another store");
        }

        IReadOnlyList<RemapRule> held = await ConfirmNumberAsync(plan.RowPlan, transaction, cancellationToken)
            .ConfigureAwait(false);
        int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (plan.BaseVersion != active)
        {
            throw Moved(FormattableString.Invariant(
                $"The text plan stands on version {plan.BaseVersion} and the store stands at {active}."));
        }

        if (await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false) is not ContentDraft open
            || open.FrozenForBaseVersion != active
            || !ContentTextCompatibility.SameDraft(plan.Snapshot.Draft, open))
        {
            throw TextMismatch("the open draft is not the complete draft the plan froze");
        }

        ContentVersionTextSnapshot baseline = await ReadTextSnapshotAtAsync(active, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (!SameText(baseline, plan.Snapshot.BaselineText))
        {
            throw TextMismatch("the base version's text or languages are not the ones the plan read");
        }

        // Every chunk is regenerated from the plan's own values, never trusted.
        ContentTextChunkConfirmation.Require(_registry, plan);
        foreach (ContentTextRevision insert in plan.TextInserts)
        {
            _ = RequireTextTarget(new ContentTextTarget(
                insert.Type, KeyOf(plan.RowPlan, insert), insert.FieldName, insert.Language));
        }

        var text = new ContentTextCommit(plan.Languages, plan.TextCloses, plan.TextInserts, plan.FrozenText);
        ContentVersionRecord record = await CommitCoreAsync(
            plan.RowPlan, request, pointers, text, held, transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return record;
    }

    /// <summary>
    /// The text a ROW-ONLY commit carries: the base version's languages unchanged, after refusing a draft or a
    /// frozen fork that needs text, a base whose text is unknown, and a plan whose manifests name other text
    /// chunks than the base recorded. The caller owns the transaction.
    /// </summary>
    async Task<ContentTextCommit> StageRowOnlyTextAsync(
        ContentPublishPlan plan,
        string member,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await RequireRowOnlyRepresentableAsync(
            await ReadDraftAsync(transaction, cancellationToken).ConfigureAwait(false), member, transaction, cancellationToken)
            .ConfigureAwait(false);
        await RequireNoTextForkAsync(plan.FrozenEdits, member, transaction, cancellationToken).ConfigureAwait(false);

        int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentTextLanguage> languages = (await ReadTextSnapshotAtAsync(
            active, transaction, cancellationToken).ConfigureAwait(false)).Languages;
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
    async Task WriteTextAsync(
        int versionNumber,
        ContentTextCommit text,
        long at,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (ContentTextRevision close in text.Closes)
        {
            using SqliteCommand command = Command(
                """
                UPDATE catalog_text SET replaced_in_version = $version, updated_at_utc = $at
                WHERE type_id = $type AND definition_id = $id AND field_name = $field AND language_tag = $language
                  AND valid_from_version = $from AND replaced_in_version IS NULL AND text_value = $value;
                """,
                transaction);
            BindRevision(command, close);
            Bind(command, "$version", (long)versionNumber);
            Bind(command, "$at", at);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw TextMismatch("a text close names a revision this store does not hold live");
            }
        }

        foreach (ContentTextRevision insert in text.Inserts)
        {
            using SqliteCommand command = Command(
                """
                INSERT INTO catalog_text(
                    type_id, definition_id, field_name, language_tag, valid_from_version, replaced_in_version,
                    text_value, created_at_utc, updated_at_utc)
                VALUES ($type, $id, $field, $language, $from, NULL, $value, $at, $at);
                """,
                transaction);
            BindRevision(command, insert);
            Bind(command, "$at", at);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ContentTextLanguage language in text.Languages)
        {
            using SqliteCommand command = Command(
                """
                INSERT INTO catalog_text_chunk(version_number, language_tag, wire_tag, chunk_hash, created_at_utc)
                VALUES ($version, $language, $wire, $hash, $at);
                """,
                transaction);
            Bind(command, "$version", (long)versionNumber);
            Bind(command, "$language", language.Language);
            Bind(command, "$wire", language.WireTag);
            Bind(command, "$hash", language.Hash);
            Bind(command, "$at", at);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The text half of the publish audit: one entry per string that changed, carrying its language, its old
    /// and new values abbreviated for the column, and the version. Full values stay in text history.
    /// </summary>
    async Task AppendTextPublishAuditAsync(
        ContentTextCommit text,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        SqliteTransaction transaction,
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

            await AppendTextChangeAsync(plan, request, close, close.Value, replacement?.Value, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (ContentTextRevision insert in inserted)
        {
            await AppendTextChangeAsync(plan, request, insert, null, insert.Value, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    Task AppendTextChangeAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        ContentTextRevision revision,
        string? before,
        string? after,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
        => AppendAuditAsync(
            transaction,
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
    async Task DeletePublishedTextAsync(
        ContentDraftTextState published,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (ContentTextEdit edit in published.Edits)
        {
            using SqliteCommand command = Command(
                """
                DELETE FROM catalog_draft_text_edit
                WHERE type_id = $type AND content_key = $key AND field_name = $field AND language_tag = $language
                  AND operation = $operation AND text_value IS $value;
                """,
                transaction);
            BindTarget(command, edit.Target);
            Bind(command, "$operation", (long)edit.Operation);
            Bind(command, "$value", edit.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (ContentTextLanguageDeclaration introduction in published.Introductions)
        {
            using SqliteCommand command = Command(
                "DELETE FROM catalog_draft_text_language WHERE language_tag = $language AND wire_tag = $wire;",
                transaction);
            Bind(command, "$language", introduction.Language);
            Bind(command, "$wire", introduction.WireTag);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static void BindRevision(SqliteCommand command, ContentTextRevision revision)
    {
        Bind(command, "$type", (long)revision.Type.Value);
        Bind(command, "$id", (long)revision.DefinitionId);
        Bind(command, "$field", revision.FieldName);
        Bind(command, "$language", revision.Language);
        Bind(command, "$from", (long)revision.ValidFromVersion);
        Bind(command, "$value", revision.Value);
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
