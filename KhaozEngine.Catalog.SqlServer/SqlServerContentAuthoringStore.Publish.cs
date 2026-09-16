using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The published half: the baseline a publish is prepared against, the ONE transaction that commits it, the
/// whole publish, the snapshot load and the rollback draft.
/// <para>
/// <b>Step 10 is one Serializable transaction and it is the only commit point.</b> Inside it, in order:
/// confirm the version number, insert the version row, close and insert every temporal row, append every
/// remap rule, insert every chunk row one per side, insert every audit row, delete the draft, and move the
/// active pointer LAST. A reader that sees the new active version is guaranteed to see everything of it.
/// </para>
/// <para>
/// <b>The number is CONFIRMED rather than trusted, and on this backend the confirmation is load bearing.</b>
/// Nothing holds a lock across steps 1 to 10, and the second console is in another process, so another
/// publish really can land underneath a prepared plan. The plan digested its version number into both
/// manifest hashes at step 8, so a plan whose base moved carries hashes that name a different number and the
/// transaction refuses it. Serializable is what makes the re-read and the writes that follow it one decision
/// rather than two.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    readonly object _commitGate = new();
    ContentPublishCommit? _commit;
    IReadOnlyList<RemapRule>? _importRules;

    /// <inheritdoc />
    public async Task<ContentPublishBaseline> ReadPublishBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        await ClearStaleFreezeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync((scope, token) => ReadBaselineAsync(scope, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ContentVersionRecord> CommitPublishAsync(
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

        return WriteAsync(
            async (scope, token) =>
            {
                // 1. CONFIRM the number. The highest published version is re-read HERE, inside the
                // transaction, so a publish that landed while this plan was being prepared is caught rather
                // than overwritten.
                int highest = await ReadIntAsync(
                    scope, "SELECT COALESCE(MAX(version_number), 0) FROM dbo.catalog_version;", token)
                    .ConfigureAwait(false);
                if (plan.VersionNumber != highest + 1)
                {
                    throw Moved(FormattableString.Invariant(
                        $"The plan publishes version {plan.VersionNumber} and the highest published version is {highest}, so the next number is {highest + 1}. Another publish landed under this plan and its manifest hashes already carry the wrong number."));
                }

                IReadOnlyList<RemapRule> held = await ReadRulesAsync(scope, token).ConfigureAwait(false);
                ContentRulePrefix.Require(held, plan);

                ContentPublishBaseline before = await ReadBaselineAsync(scope, token).ConfigureAwait(false);

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
                await InsertVersionAsync(scope, record, token).ConfigureAwait(false);

                // 3. Every temporal row change: the closes first, so no insert is mistaken for the revision
                // it replaces while the walk is half done.
                for (int i = 0; i < plan.Closes.Count; i++)
                {
                    await CloseRowAsync(scope, plan.Closes[i], token).ConfigureAwait(false);
                }

                for (int i = 0; i < plan.Inserts.Count; i++)
                {
                    await InsertRowAsync(scope, plan.Inserts[i], token).ConfigureAwait(false);
                }

                // 4. Every remap rule, appended at the sequence above the highest. Append only: there is no
                // update and no delete of a rule anywhere.
                for (int i = held.Count; i < plan.Rules.Count; i++)
                {
                    await InsertRuleAsync(scope, plan.Rules[i], token).ConfigureAwait(false);
                }

                // 5. Every chunk row, ONE PER SIDE, the carried-forward ones included.
                for (int i = 0; i < plan.Chunks.Count; i++)
                {
                    await InsertChunkAsync(scope, plan.VersionNumber, plan.Chunks[i], token)
                        .ConfigureAwait(false);
                }

                // 6. Every audit row, field level, against the version the rows are leaving.
                await AppendPublishAuditAsync(scope, before, plan, request, token).ConfigureAwait(false);

                // 7. The draft, scoped to the edits this plan FROZE. The freeze is what makes that the whole
                // draft, so anything else here survives rather than being deleted unpublished.
                await DeleteFrozenEditsAsync(scope, plan, token).ConfigureAwait(false);

                // 8. The active pointer, LAST. It moves for the NEXT boot: a running server keeps serving the
                // version it loaded.
                await using (SqlCommand pointer = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_metadata SET active_version = @version, updated_at_utc = @now
                    WHERE metadata_key = 1;
                    """))
                {
                    BindInt(pointer, "@version", plan.VersionNumber);
                    BindTime(pointer, "@now", _clock());
                    await pointer.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return record;
            },
            cancellationToken);
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

        (ContentRollbackPlan plan, int from) = await ReadAsync(
            async (scope, token) =>
            {
                if (!await VersionExistsAsync(scope, targetVersion, token).ConfigureAwait(false))
                {
                    throw UnknownVersion(targetVersion);
                }

                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                ContentRollbackPlan prepared = ContentRollback.Prepare(
                    targetVersion,
                    await ReadRevisionsAsync(scope, default, null, targetVersion, token).ConfigureAwait(false),
                    active,
                    await ReadRevisionsAsync(scope, default, null, active, token).ConfigureAwait(false),
                    await ReadRulesAsync(scope, token).ConfigureAwait(false),
                    _registry);
                return (prepared, active);
            },
            cancellationToken).ConfigureAwait(false);

        if (plan.IsBlocked)
        {
            throw ContentRollback.Refusal(plan);
        }

        ContentDraft draft = await ApplyEditsAsync(plan.Edits, actor, operatorId, note, cancellationToken)
            .ConfigureAwait(false);

        await WriteAsync(
            (scope, token) => AppendAuditAsync(
                scope,
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
                token),
            cancellationToken).ConfigureAwait(false);
        return draft;
    }

    /// <summary>The baseline as it stands. The caller owns the scope.</summary>
    async Task<ContentPublishBaseline> ReadBaselineAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentVersionRecord> record = await ReadVersionsAsync(scope, active, cancellationToken)
            .ConfigureAwait(false);

        return new ContentPublishBaseline(
            active,
            await ReadRevisionsAsync(scope, default, null, active, cancellationToken).ConfigureAwait(false),
            _importRules ?? await ReadRulesAsync(scope, cancellationToken).ConfigureAwait(false),
            await ReadChunksAsync(scope, active, cancellationToken).ConfigureAwait(false),

            // No table in spec 4.4 holds a version's text chunk list, and nothing in phase 1 produces one, so
            // the carry forward has nothing to carry. When text chunks land, either the schema gains a table
            // or the base version's manifest becomes the source
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/919, item 3).
            [],
            record.Count == 0 ? 0 : record[0].MinimumServerBuild,
            record.Count == 0 ? 0 : record[0].MinimumClientBuild);
    }

    /// <summary>The full ordered rule list. The caller owns the scope.</summary>
    static async Task<IReadOnlyList<RemapRule>> ReadRulesAsync(
        SqlServerCatalogScope scope,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT [sequence], introduced_in, type_id, kind, from_id, to_id, payload
            FROM dbo.catalog_remap_rule
            ORDER BY [sequence];
            """);

        var rules = new List<RemapRule>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new RemapRule(
                reader.GetInt32(0),
                reader.GetInt32(1),
                new ContentTypeId((ushort)reader.GetInt32(2)),
                (RemapRuleKind)reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetFieldValue<byte[]>(6)));
        }

        return rules;
    }

    /// <summary>One version's chunk rows at every side, in the REUSED form a carry forward reads.</summary>
    static async Task<IReadOnlyList<ContentChunkRecord>> ReadChunksAsync(
        SqlServerCatalogScope scope,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes, visibility
            FROM dbo.catalog_chunk
            WHERE version_number = @version
            ORDER BY type_id, chunk_index, visibility;
            """);
        BindInt(command, "@version", versionNumber);

        var chunks = new List<ContentChunkRecord>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(new ContentChunkRecord(
                new ContentTypeId((ushort)reader.GetInt32(0)),
                reader.GetInt32(1),
                (ContentVisibility)reader.GetInt32(6),
                reader.GetString(2),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(3),
                default,
                true));
        }

        return chunks;
    }

    static async Task InsertVersionAsync(
        SqlServerCatalogScope scope,
        ContentVersionRecord record,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_version(
                version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
                minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
            VALUES (@version, @server, @client, @minServer, @minClient, @generation, @base, @by, @note, @at);
            """);
        BindInt(command, "@version", record.VersionNumber);
        BindText(command, "@server", record.ServerManifestHash);
        BindText(command, "@client", record.ClientManifestHash);
        BindInt(command, "@minServer", record.MinimumServerBuild);
        BindInt(command, "@minClient", record.MinimumClientBuild);
        BindInt(command, "@generation", record.FormatGeneration);
        BindInt(command, "@base", record.BaseVersion);
        BindText(command, "@by", record.PublishedBy);
        BindText(command, "@note", record.Note);
        BindTime(command, "@at", record.PublishedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task InsertRuleAsync(
        SqlServerCatalogScope scope,
        RemapRule rule,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_remap_rule([sequence], introduced_in, type_id, kind, from_id, to_id, payload)
            VALUES (@sequence, @introduced, @type, @kind, @from, @to, @payload);
            """);
        BindInt(command, "@sequence", rule.Sequence);
        BindInt(command, "@introduced", rule.IntroducedIn);
        BindInt(command, "@type", (int)rule.Type.Value);
        BindInt(command, "@kind", (int)rule.Kind);
        BindInt(command, "@from", rule.FromId);
        BindInt(command, "@to", rule.ToId);
        BindBlob(command, "@payload", rule.Payload.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task InsertChunkAsync(
        SqlServerCatalogScope scope,
        int versionNumber,
        ContentChunkRecord chunk,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_chunk(
                version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes,
                stored_bytes, visibility)
            VALUES (@version, @type, @index, @hash, @rows, @uncompressed, @stored, @visibility);
            """);
        BindInt(command, "@version", versionNumber);
        BindInt(command, "@type", (int)chunk.Type.Value);
        BindInt(command, "@index", chunk.ChunkIndex);
        BindText(command, "@hash", chunk.Hash);
        BindInt(command, "@rows", chunk.RowCount);
        BindInt(command, "@uncompressed", chunk.UncompressedBytes);
        BindInt(command, "@stored", chunk.StoredBytes);
        BindInt(command, "@visibility", (int)chunk.Side);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The publish audit, one row per CHANGED FIELD, which is spec 4.6's unit. A change that moved no field
    /// value, a retire, writes one row-level entry naming the operation instead, so it still leaves a trace.
    /// </summary>
    async Task AppendPublishAuditAsync(
        SqlServerCatalogScope scope,
        ContentPublishBaseline before,
        ContentPublishPlan plan,
        ContentPublishRequest request,
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
                    scope,
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
                    scope,
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
