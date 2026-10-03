using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The exact-version TEXT READ of the SQLite store, and the fail-closed gates every row-only route runs
/// against it.
/// <para>
/// <b>Completeness decides what a read may say.</b> A version this build committed records
/// <c>text_snapshot_complete = 1</c>, and its values and language record are read as they stand. A legacy
/// version records NULL, which is unknown: it reads as empty only on the read-only proof of
/// <see cref="ContentTextProvenanceProof"/>, and otherwise refuses with
/// <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/>. A read never writes completeness.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <summary>
    /// The complete text of one version: an empty baseline at 0, the recorded languages and every live revision
    /// at a complete version, and an empty snapshot for a legacy version only when the proof passes. The caller
    /// holds the lease.
    /// </summary>
    /// <exception cref="ContentAuthoringException">The version's text is unknown.</exception>
    async Task<ContentVersionTextSnapshot> ReadTextSnapshotAtAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string epoch = await ReadEpochAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (versionNumber == NoActiveVersion)
        {
            return ContentVersionTextSnapshot.EmptyBaseline(epoch);
        }

        bool complete;
        using (SqliteCommand command = Command(
            "SELECT text_snapshot_complete FROM catalog_version WHERE version_number = $version;", transaction))
        {
            Bind(command, "$version", (long)versionNumber);
            object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw UnknownVersion(versionNumber);
            complete = raw is long flag && flag == 1;
        }

        IReadOnlyList<ContentTextLanguage> languages = await ReadTextLanguagesAsync(
            versionNumber, transaction, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentTextRevision> revisions = await ReadTextRevisionsAsync(
            versionNumber, transaction, cancellationToken).ConfigureAwait(false);
        if (complete)
        {
            return new ContentVersionTextSnapshot(epoch, versionNumber, revisions, languages);
        }

        // Unknown. Mapping rows or values beside a NULL marker are not a record anyone can trust, and an empty
        // pair of lists is not a proof on its own.
        if (languages.Count > 0 || revisions.Count > 0
            || !await ProvesLegacyEmptyAsync(versionNumber, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw ContentTextProvenanceProof.Unknown(versionNumber);
        }

        return new ContentVersionTextSnapshot(epoch, versionNumber, [], []);
    }

    /// <summary>
    /// The read-only proof that a legacy version named no language, over its record, its recorded chunk rows,
    /// its rules and this store's pack. Nothing is written either way.
    /// </summary>
    async Task<bool> ProvesLegacyEmptyAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentVersionRecord> record = await ReadVersionsAsync(versionNumber, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (record.Count == 0)
        {
            return false;
        }

        var rules = new List<RemapRule>();
        foreach (RemapRule rule in await ReadRulesAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            if (rule.IntroducedIn <= versionNumber)
            {
                rules.Add(rule);
            }
        }

        return await ContentTextProvenanceProof.ProveEmptyAsync(
            record[0],
            PackStore,
            _registry,
            await ReadChunksAsync(versionNumber, transaction, cancellationToken).ConfigureAwait(false),
            rules,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One version's recorded languages in manifest order: ordinal wire tag.</summary>
    async Task<IReadOnlyList<ContentTextLanguage>> ReadTextLanguagesAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT language_tag, wire_tag, chunk_hash FROM catalog_text_chunk
            WHERE version_number = $version
            ORDER BY wire_tag;
            """,
            transaction);
        Bind(command, "$version", (long)versionNumber);
        var languages = new List<ContentTextLanguage>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            languages.Add(new ContentTextLanguage(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return languages;
    }

    /// <summary>One version's recorded languages as the manifests name them, which a row-only publish carries.</summary>
    async Task<IReadOnlyList<ManifestLanguageEntry>> ReadManifestLanguagesAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentTextLanguage> recorded = await ReadTextLanguagesAsync(
            versionNumber, transaction, cancellationToken).ConfigureAwait(false);
        var entries = new ManifestLanguageEntry[recorded.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new ManifestLanguageEntry(recorded[i].WireTag, recorded[i].Hash);
        }

        return entries;
    }

    /// <summary>Every text revision live at one version, retired rows included.</summary>
    async Task<IReadOnlyList<ContentTextRevision>> ReadTextRevisionsAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT type_id, definition_id, field_name, language_tag, text_value, valid_from_version,
                   replaced_in_version
            FROM catalog_text
            WHERE valid_from_version <= $version
              AND (replaced_in_version IS NULL OR replaced_in_version > $version)
            ORDER BY type_id, definition_id, field_name, language_tag;
            """,
            transaction);
        Bind(command, "$version", (long)versionNumber);
        var revisions = new List<ContentTextRevision>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(new ContentTextRevision(
                new ContentTypeId((ushort)reader.GetInt64(0)),
                (int)reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                (int)reader.GetInt64(5),
                reader.IsDBNull(6) ? null : (int)reader.GetInt64(6)));
        }

        return revisions;
    }

    /// <summary>
    /// The refusal a row-only publish step runs: no held text, and no fork of a row whose text the row-only
    /// route cannot copy. The caller holds the lease.
    /// </summary>
    async Task RequireRowOnlyRepresentableAsync(
        ContentDraft? draft,
        string member,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ContentTextCompatibility.RequireNoHeldText(draft, member);
        if (draft is not null)
        {
            await RequireNoTextForkAsync(draft.Changes.Edits, member, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Refuses a fork of a row holding text at the active version. The caller holds the lease.</summary>
    async Task RequireNoTextForkAsync(
        IReadOnlyList<ContentEdit> edits,
        string member,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        int active = await ReadActiveAsync(transaction, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < edits.Count; i++)
        {
            ContentEdit edit = edits[i];
            if (edit.Operation != ContentEditOperation.Fork)
            {
                continue;
            }

            using SqliteCommand command = Command(
                """
                SELECT 1 FROM catalog_text
                WHERE type_id = $type AND definition_id = $id AND valid_from_version <= $at
                  AND (replaced_in_version IS NULL OR replaced_in_version > $at);
                """,
                transaction);
            Bind(command, "$type", (long)edit.Type.Value);
            Bind(command, "$id", (long)edit.DefinitionId);
            Bind(command, "$at", (long)active);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw ContentTextCompatibility.Unrepresented(member, FormattableString.Invariant(
                    $"the fork of type {edit.Type.Value} row {edit.DefinitionId} owes its copy text this route cannot write"));
            }
        }
    }

    /// <summary>
    /// Refuses a row-only rollback unless the current and the target version are each complete and text free,
    /// or unknown and proved empty by <see cref="ContentTextProvenanceProof"/>. It reads only, so a proof
    /// records nothing, and it runs again inside the transaction that applies the rollback edits. The caller
    /// holds the lease.
    /// </summary>
    async Task RequireRowOnlyRollbackAsync(
        int from,
        int target,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await RequireTextFreeAsync(from, transaction, cancellationToken).ConfigureAwait(false);
        await RequireTextFreeAsync(target, transaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a version a row-only rollback cannot show text free: an unknown version no proof shows empty
    /// refuses with <see cref="ContentAuthoringException.TextProvenanceUnknownReason"/>, and a version holding
    /// any language mapping or live text revision refuses as unrepresented. The caller holds the lease.
    /// </summary>
    async Task RequireTextFreeAsync(int versionNumber, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        ContentVersionTextSnapshot text = await ReadTextSnapshotAtAsync(versionNumber, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (text.Languages.Count > 0 || text.Revisions.Count > 0)
        {
            throw ContentTextCompatibility.Unrepresented(nameof(RollbackToAsync), FormattableString.Invariant(
                $"version {versionNumber} records {text.Languages.Count} language(s) and {text.Revisions.Count} value(s)"));
        }
    }
}
