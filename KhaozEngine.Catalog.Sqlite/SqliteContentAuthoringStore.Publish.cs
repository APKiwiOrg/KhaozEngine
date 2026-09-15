using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The published half: the baseline a publish is prepared against, the ONE transaction that commits it, the
/// whole publish, the snapshot load and the rollback draft.
/// <para>
/// <b>Step 10 is one explicit transaction and it is the only commit point.</b> Inside it, in order: confirm
/// the version number, insert the version row, close and insert every temporal row, append every remap rule,
/// insert every chunk row one per side, insert every audit row, delete the draft, and move the active
/// pointer LAST. A reader that sees the new active version is guaranteed to see everything of it.
/// </para>
/// <para>
/// <b>The number is CONFIRMED rather than trusted.</b> This store leases its connection per call, so nothing
/// holds a lock across steps 1 to 10 and another publish really can land underneath a prepared plan. The
/// plan digested its version number into both manifest hashes at step 8, so a plan whose base moved carries
/// hashes that name a different number and the transaction refuses it.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    readonly object _commitGate = new();
    ContentPublishCommit? _commit;
    IReadOnlyList<RemapRule>? _importRules;

    /// <inheritdoc />
    public async Task<ContentPublishBaseline> ReadPublishBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadBaselineAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        if (!plan.IsValid)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The candidate for version {plan.VersionNumber} carries {plan.Validation.Findings.Count} finding(s), so no transaction opened. A version the validator refused is a version a boot then fails closed on."),
                default,
                0,
                ContentAuthoringException.CandidateInvalidReason,
                plan.Validation.Findings);
        }

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        // 1. CONFIRM the number. The highest published version is re-read HERE, inside the transaction, so a
        // publish that landed while this plan was being prepared is caught rather than overwritten.
        int highest = (int)await ReadLongAsync(
            "SELECT COALESCE(MAX(version_number), 0) FROM catalog_version;", transaction, cancellationToken)
            .ConfigureAwait(false);
        if (plan.VersionNumber != highest + 1)
        {
            throw Moved(FormattableString.Invariant(
                $"The plan publishes version {plan.VersionNumber} and the highest published version is {highest}, so the next number is {highest + 1}. Another publish landed under this plan and its manifest hashes already carry the wrong number."));
        }

        IReadOnlyList<RemapRule> held = await ReadRulesAsync(transaction, cancellationToken).ConfigureAwait(false);
        ContentRulePrefix.Require(held, plan);

        ContentPublishBaseline before = await ReadBaselineAsync(transaction, cancellationToken)
            .ConfigureAwait(false);

        // 2. The version row, which every other insert below has a foreign key to.
        var record = new ContentVersionRecord(
            plan.VersionNumber,
            plan.ServerManifestHash,
            plan.ClientManifestHash,
            plan.MinimumServerBuild,
            plan.MinimumClientBuild,
            plan.FormatGeneration,
            plan.BaseVersion,
            request.Actor,
            request.Note,
            _clock());
        await InsertVersionAsync(record, transaction, cancellationToken).ConfigureAwait(false);

        // 3. Every temporal row change: the closes first, so no insert is mistaken for the revision it
        // replaces while the walk is half done.
        for (int i = 0; i < plan.Closes.Count; i++)
        {
            await CloseRowAsync(plan.Closes[i], transaction, cancellationToken).ConfigureAwait(false);
        }

        for (int i = 0; i < plan.Inserts.Count; i++)
        {
            await InsertRowAsync(plan.Inserts[i], transaction, cancellationToken).ConfigureAwait(false);
        }

        // 4. Every remap rule, appended at the sequence above the highest. Append only: there is no update
        // and no delete of a rule anywhere.
        for (int i = held.Count; i < plan.Rules.Count; i++)
        {
            await InsertRuleAsync(plan.Rules[i], transaction, cancellationToken).ConfigureAwait(false);
        }

        // 5. Every chunk row, ONE PER SIDE, the carried-forward ones included.
        for (int i = 0; i < plan.Chunks.Count; i++)
        {
            await InsertChunkAsync(plan.VersionNumber, plan.Chunks[i], transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        // 6. Every audit row, field level, against the version the rows are leaving.
        await AppendPublishAuditAsync(before, plan, request, transaction, cancellationToken).ConfigureAwait(false);

        // 7. The draft and its edits.
        await DeleteDraftAsync(transaction, cancellationToken).ConfigureAwait(false);

        // 8. The active pointer, LAST. It moves for the NEXT boot: a running server keeps serving the version
        // it loaded.
        using (SqliteCommand pointer = Command(
            """
            UPDATE catalog_metadata SET active_version = $version, updated_at_utc = $now
            WHERE metadata_key = 1;
            """,
            transaction))
        {
            Bind(pointer, "$version", (long)plan.VersionNumber);
            Bind(pointer, "$now", Millis(_clock()));
            await pointer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return record;
    }

    /// <inheritdoc />
    public async Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await RequireCommit().PublishAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// It reads the version's PACK back rather than rebuilding the snapshot from the row table, deliberately:
    /// what a server loads is the pack, so a store that answered from its own rows could report a version
    /// whose bytes are unreadable as healthy.
    /// </remarks>
    public async Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentVersionRecord record = await GetVersionAsync(versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw UnknownVersion(versionNumber);
        IPackStore pack = PackStore ?? throw NoPackStore(nameof(LoadSnapshotAsync));

        ContentManifestRead manifest = await ContentPackReader
            .ReadManifestAsync(pack, record.ServerManifestHash, ContentManifestSide.Server, registry, cancellationToken)
            .ConfigureAwait(false);
        if (!manifest.Success || manifest.Manifest is null)
        {
            throw Unreadable(versionNumber, manifest.Hash, manifest.Reason);
        }

        var reader = new ContentPackReader(pack, registry, manifest.Manifest, manifest.Hash);
        ContentPackRead read = await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return read.Success && read.Snapshot is not null
            ? read.Snapshot
            : throw Unreadable(versionNumber, read.Hash, read.Reason);
    }

    /// <inheritdoc />
    /// <remarks>
    /// It BUILDS A DRAFT and publishes nothing, so an operator reviews the diff first. A row live at the
    /// target and retired since is a flat refusal, because a retire is irreversible for pages already
    /// migrated past it.
    /// </remarks>
    public async Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        ContentRollbackPlan plan;
        int from;
        using (SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await VersionExistsAsync(targetVersion, null, cancellationToken).ConfigureAwait(false))
            {
                throw UnknownVersion(targetVersion);
            }

            from = (int)await ReadLongAsync(
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", null, cancellationToken)
                .ConfigureAwait(false);
            plan = ContentRollback.Prepare(
                targetVersion,
                await ReadRevisionsAsync(default, null, targetVersion, null, cancellationToken).ConfigureAwait(false),
                from,
                await ReadRevisionsAsync(default, null, from, null, cancellationToken).ConfigureAwait(false),
                await ReadRulesAsync(null, cancellationToken).ConfigureAwait(false),
                _registry);
        }

        if (plan.IsBlocked)
        {
            throw ContentRollback.Refusal(plan);
        }

        ContentDraft draft = await ApplyEditsAsync(plan.Edits, actor, operatorId, note, cancellationToken)
            .ConfigureAwait(false);

        using SqliteStoreLease after = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        await AppendAuditAsync(
            transaction,
            ContentAuditActions.Rollback,
            actor,
            operatorId,
            default,
            0,
            default,
            string.Empty,
            Render(from),
            Render(targetVersion),
            0,
            note,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return draft;
    }

    /// <summary>The baseline as it stands. The caller already holds the lease.</summary>
    async Task<ContentPublishBaseline> ReadBaselineAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        int active = (int)await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ContentVersionRecord> record = await ReadVersionsAsync(active, transaction, cancellationToken)
            .ConfigureAwait(false);

        return new ContentPublishBaseline(
            active,
            await ReadRevisionsAsync(default, null, active, transaction, cancellationToken).ConfigureAwait(false),
            _importRules ?? await ReadRulesAsync(transaction, cancellationToken).ConfigureAwait(false),
            await ReadChunksAsync(active, transaction, cancellationToken).ConfigureAwait(false),

            // No table in spec 4.4 holds a version's text chunk list, and nothing in phase 1 produces one, so
            // the carry forward has nothing to carry. When text chunks land, either the schema gains a table
            // or the base version's manifest becomes the source.
            [],
            record.Count == 0 ? 0 : record[0].MinimumServerBuild,
            record.Count == 0 ? 0 : record[0].MinimumClientBuild);
    }

    /// <summary>The full ordered rule list. The caller already holds the lease.</summary>
    async Task<IReadOnlyList<RemapRule>> ReadRulesAsync(
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT sequence, introduced_in, type_id, kind, from_id, to_id, payload
            FROM catalog_remap_rule
            ORDER BY sequence;
            """,
            transaction);

        var rules = new List<RemapRule>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new RemapRule(
                (int)reader.GetInt64(0),
                (int)reader.GetInt64(1),
                new ContentTypeId((ushort)reader.GetInt64(2)),
                (RemapRuleKind)reader.GetInt64(3),
                (int)reader.GetInt64(4),
                (int)reader.GetInt64(5),
                reader.GetFieldValue<byte[]>(6)));
        }

        return rules;
    }

    /// <summary>One version's chunk rows at every side, in the REUSED form a carry forward reads.</summary>
    async Task<IReadOnlyList<ContentChunkRecord>> ReadChunksAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes, visibility
            FROM catalog_chunk
            WHERE version_number = $version
            ORDER BY type_id, chunk_index, visibility;
            """,
            transaction);
        Bind(command, "$version", (long)versionNumber);

        var chunks = new List<ContentChunkRecord>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(new ContentChunkRecord(
                new ContentTypeId((ushort)reader.GetInt64(0)),
                (int)reader.GetInt64(1),
                (ContentVisibility)reader.GetInt64(6),
                reader.GetString(2),
                (int)reader.GetInt64(4),
                (int)reader.GetInt64(5),
                (int)reader.GetInt64(3),
                default,
                true));
        }

        return chunks;
    }

    async Task InsertVersionAsync(
        ContentVersionRecord record,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_version(
                version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
                minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
            VALUES ($version, $server, $client, $minServer, $minClient, $generation, $base, $by, $note, $at);
            """,
            transaction);
        Bind(command, "$version", (long)record.VersionNumber);
        Bind(command, "$server", record.ServerManifestHash);
        Bind(command, "$client", record.ClientManifestHash);
        Bind(command, "$minServer", (long)record.MinimumServerBuild);
        Bind(command, "$minClient", (long)record.MinimumClientBuild);
        Bind(command, "$generation", (long)record.FormatGeneration);
        Bind(command, "$base", (long)record.BaseVersion);
        Bind(command, "$by", record.PublishedBy);
        Bind(command, "$note", record.Note);
        Bind(command, "$at", Millis(record.PublishedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task InsertRuleAsync(RemapRule rule, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_remap_rule(sequence, introduced_in, type_id, kind, from_id, to_id, payload)
            VALUES ($sequence, $introduced, $type, $kind, $from, $to, $payload);
            """,
            transaction);
        Bind(command, "$sequence", (long)rule.Sequence);
        Bind(command, "$introduced", (long)rule.IntroducedIn);
        Bind(command, "$type", (long)rule.Type.Value);
        Bind(command, "$kind", (long)rule.Kind);
        Bind(command, "$from", (long)rule.FromId);
        Bind(command, "$to", (long)rule.ToId);
        Bind(command, "$payload", rule.Payload.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task InsertChunkAsync(
        int versionNumber,
        ContentChunkRecord chunk,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_chunk(
                version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes,
                stored_bytes, visibility)
            VALUES ($version, $type, $index, $hash, $rows, $uncompressed, $stored, $visibility);
            """,
            transaction);
        Bind(command, "$version", (long)versionNumber);
        Bind(command, "$type", (long)chunk.Type.Value);
        Bind(command, "$index", (long)chunk.ChunkIndex);
        Bind(command, "$hash", chunk.Hash);
        Bind(command, "$rows", (long)chunk.RowCount);
        Bind(command, "$uncompressed", (long)chunk.UncompressedBytes);
        Bind(command, "$stored", (long)chunk.StoredBytes);
        Bind(command, "$visibility", (long)chunk.Side);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The publish audit, one row per CHANGED FIELD, which is spec 4.6's unit. A change that moved no field
    /// value, a retire, writes one row-level entry naming the operation instead, so it still leaves a trace.
    /// </summary>
    async Task AppendPublishAuditAsync(
        ContentPublishBaseline before,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ContentDiff diff = ContentDiff.Between(
            before.VersionNumber, before.Rows, plan.VersionNumber, plan.LiveRows, _registry);

        for (int i = 0; i < diff.Changes.Count; i++)
        {
            ContentDiffEntry entry = diff.Changes[i];
            if (entry.Fields.Count == 0)
            {
                await AppendAuditAsync(
                    transaction,
                    ContentAuditActions.Publish,
                    request.Actor,
                    request.Operator,
                    entry.Type,
                    entry.Id,
                    entry.Key,
                    string.Empty,
                    null,
                    entry.Operation.ToString(),
                    plan.VersionNumber,
                    request.Note,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            for (int f = 0; f < entry.Fields.Count; f++)
            {
                ContentDiffField field = entry.Fields[f];
                await AppendAuditAsync(
                    transaction,
                    ContentAuditActions.Publish,
                    request.Actor,
                    request.Operator,
                    entry.Type,
                    entry.Id,
                    entry.Key,
                    field.Field,
                    field.Before,
                    field.After,
                    plan.VersionNumber,
                    request.Note,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The commit half, built once over the pack target this store was handed.</summary>
    ContentPublishCommit RequireCommit()
    {
        lock (_commitGate)
        {
            if (_commit is not null)
            {
                return _commit;
            }

            IPackStore pack = PackStore ?? throw NoPackStore(nameof(PublishAsync));
            _commit = new ContentPublishCommit(this, pack, new ContentPublisher(this, this, _registry));
            return _commit;
        }
    }

    static ContentAuthoringException Moved(string message)
        => new(message, default, 0, ContentAuthoringException.BaseVersionMovedReason);

    static ContentAuthoringException Unreadable(int versionNumber, string? hash, string? reason)
        => new(
            FormattableString.Invariant(
                $"Version {versionNumber}'s pack could not be read at object '{hash}': {reason}."),
            default,
            0,
            ContentAuthoringException.PackUnreadableReason);
}
