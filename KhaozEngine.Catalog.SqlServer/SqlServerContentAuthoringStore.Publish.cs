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
/// remap rule, insert every chunk row one per side, close and insert every text revision, record every
/// language, insert every audit row, delete the draft, and move the active pointer LAST. A reader that sees
/// the new active version is guaranteed to see everything of it. The row-only and the text route share that
/// core and differ only in the text they stage, so every version is recorded text complete.
/// </para>
/// <para>
/// <b>Every row, field row, close, rule, chunk, text revision, language record and draft rebase this commit
/// writes carries the version's own publish time</b>, the value <c>catalog_version.published_at_utc</c> holds,
/// which is also what the version 3 migration fills a legacy row of those tables with. The active pointer and
/// the audit rows read the clock as they always have.
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
        IPackVersionPointerStore? pointers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        // The row half of a text plan is refused before any transaction rather than committed without its
        // text.
        ContentTextCompatibility.RequireRowOnlyPlan(plan, nameof(CommitPublishAsync));

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
                IReadOnlyList<RemapRule> held = await ConfirmNumberAsync(scope, plan, token).ConfigureAwait(false);

                // The row-only route carries the base version's text forward untouched, and refuses when the
                // draft or the frozen rows need text it cannot carry, or the base's text is unknown.
                ContentTextCommit text = await StageRowOnlyTextAsync(scope, plan, nameof(CommitPublishAsync), token)
                    .ConfigureAwait(false);
                return await CommitCoreAsync(scope, plan, request, pointers, text, held, token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <summary>
    /// Step 1 of a commit: CONFIRM the number rather than trusting it, and the rule list the plan extends. The
    /// highest published version is re-read HERE, inside the transaction, so a publish that landed while this
    /// plan was being prepared is caught rather than overwritten.
    /// </summary>
    /// <returns>The rules the store holds, which the commit appends above.</returns>
    static async Task<IReadOnlyList<RemapRule>> ConfirmNumberAsync(
        SqlServerCatalogScope scope,
        ContentPublishPlan plan,
        CancellationToken cancellationToken)
    {
        int highest = await ReadIntAsync(
            scope, "SELECT COALESCE(MAX(version_number), 0) FROM dbo.catalog_version;", cancellationToken)
            .ConfigureAwait(false);
        if (plan.VersionNumber != highest + 1)
        {
            throw Moved(FormattableString.Invariant(
                $"The plan publishes version {plan.VersionNumber} and the highest published version is {highest}, so the next number is {highest + 1}. Another publish landed under this plan and its manifest hashes already carry the wrong number."));
        }

        IReadOnlyList<RemapRule> held = await ReadRulesAsync(scope, cancellationToken).ConfigureAwait(false);
        ContentRulePrefix.Require(held, plan);
        return held;
    }

    /// <summary>
    /// Steps 2 to 9 of a confirmed commit, shared by the row-only and the text route, which differ only in the
    /// text they stage. The caller owns the transaction, which commits when the caller's body returns.
    /// </summary>
    async Task<ContentVersionRecord> CommitCoreAsync(
        SqlServerCatalogScope scope,
        ContentPublishPlan plan,
        ContentPublishRequest request,
        IPackVersionPointerStore? pointers,
        ContentTextCommit text,
        IReadOnlyList<RemapRule> held,
        CancellationToken cancellationToken)
    {
        ContentPublishBaseline before = await ReadBaselineAsync(scope, cancellationToken).ConfigureAwait(false);

        // 2. The version row, which every other insert below has a foreign key to. It is recorded text
        // complete, a version publishing no language included.
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
        await InsertVersionAsync(scope, record, cancellationToken).ConfigureAwait(false);
        DateTimeOffset at = record.PublishedAtUtc;

        // 3. Every temporal row change: the closes first, so no insert is mistaken for the revision it
        // replaces while the walk is half done.
        for (int i = 0; i < plan.Closes.Count; i++)
        {
            await CloseRowAsync(scope, plan.Closes[i], at, cancellationToken).ConfigureAwait(false);
        }

        for (int i = 0; i < plan.Inserts.Count; i++)
        {
            await InsertRowAsync(scope, plan.Inserts[i], at, cancellationToken).ConfigureAwait(false);
        }

        // 4. Every remap rule, appended at the sequence above the highest. Append only: there is no update and
        // no delete of a rule anywhere.
        for (int i = held.Count; i < plan.Rules.Count; i++)
        {
            await InsertRuleAsync(scope, plan.Rules[i], at, cancellationToken).ConfigureAwait(false);
        }

        // 5. Every chunk row, ONE PER SIDE, the carried-forward ones included.
        for (int i = 0; i < plan.Chunks.Count; i++)
        {
            await InsertChunkAsync(scope, plan.VersionNumber, plan.Chunks[i], at, cancellationToken)
                .ConfigureAwait(false);
        }

        // 5b. Every text revision closed and inserted, and the version's complete language record.
        await WriteTextAsync(scope, plan.VersionNumber, text, at, cancellationToken).ConfigureAwait(false);

        // 6. Every audit row, field level, rows then text, against the version the rows are leaving.
        await AppendPublishAuditAsync(scope, before, plan, request, cancellationToken).ConfigureAwait(false);
        await AppendTextPublishAuditAsync(scope, text, plan, request, cancellationToken).ConfigureAwait(false);

        // 6b. The applied ledger row, when this publish carries an upgrade. It is INSIDE this transaction, so
        // the version and the history entry naming the upgrade that produced it land together, and an id the
        // ledger already holds refuses the whole commit.
        await InsertAppliedUpgradeAsync(scope, request, plan.VersionNumber, cancellationToken).ConfigureAwait(false);

        // 7. The draft, scoped to the rows and text this plan FROZE. The freeze is what makes that the whole
        // draft, so anything else here survives rather than being deleted unpublished.
        await DeleteFrozenEditsAsync(scope, plan, text.PublishedText, at, cancellationToken).ConfigureAwait(false);

        // 8. The active pointer, LAST. It moves for the NEXT boot: a running server keeps serving the version
        // it loaded.
        await using (SqlCommand pointer = Command(
            scope,
            """
            UPDATE dbo.catalog_metadata SET active_version = @version, updated_at_utc = @now
            WHERE metadata_key = 1;
            """))
        {
            BindInt(pointer, "@version", plan.VersionNumber);
            BindTime(pointer, "@now", _clock());
            await pointer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 9. The pack's version pointer, after every statement above and before the commit. The update just
        // above holds the metadata row exclusively, so a rival publishing the same number is blocked or chosen
        // as the deadlock victim before it gets here, and only the winner writes the pointer. A failure here
        // rolls the whole version back.
        if (pointers is not null)
        {
            await pointers.PutVersionPointerAsync(
                plan.VersionNumber, plan.ServerManifestHash, plan.ClientManifestHash, cancellationToken)
                .ConfigureAwait(false);
        }

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

        (ContentRollbackPlan plan, int from) = await ReadAsync(
            async (scope, token) =>
            {
                if (!await VersionExistsAsync(scope, targetVersion, token).ConfigureAwait(false))
                {
                    throw UnknownVersion(targetVersion);
                }

                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                await RequireRowOnlyRollbackAsync(scope, active, targetVersion, token).ConfigureAwait(false);
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

        // ONE transaction for the re-check, the edits and the audit. The plan was computed under the read
        // above, and a publish landing in between could have moved the active version or given it text, so
        // both are confirmed again here, where the rollback edits are applied, rather than trusted.
        for (int i = 0; i < plan.Edits.Count; i++)
        {
            CheckAgainstSchema(plan.Edits[i]);
        }

        return await WriteAsync(
            async (scope, token) =>
            {
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);
                if (active != from)
                {
                    throw Moved(FormattableString.Invariant(
                        $"The rollback to version {targetVersion} was planned against version {from} and the store now stands at {active}. Nothing was written, and the rollback is planned again from the current version."));
                }

                await RequireRowOnlyRollbackAsync(scope, from, targetVersion, token).ConfigureAwait(false);
                ContentDraft draft = await ApplyEditsInAsync(scope, plan.Edits, actor, operatorId, note, token)
                    .ConfigureAwait(false);
                await AppendAuditAsync(
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
                    token).ConfigureAwait(false);
                return draft;
            },
            cancellationToken).ConfigureAwait(false);
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

            // The base version's recorded text chunks, in manifest order, which a row-only publish carries.
            await ReadManifestLanguagesAsync(scope, active, cancellationToken).ConfigureAwait(false),
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
                minimum_client_build, format_generation, base_version, published_by, note, published_at_utc,
                text_snapshot_complete)
            VALUES (@version, @server, @client, @minServer, @minClient, @generation, @base, @by, @note, @at, 1);
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
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_remap_rule(
                [sequence], introduced_in, type_id, kind, from_id, to_id, payload, created_at_utc)
            VALUES (@sequence, @introduced, @type, @kind, @from, @to, @payload, @at);
            """);
        BindTime(command, "@at", at);
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
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            INSERT INTO dbo.catalog_chunk(
                version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes,
                stored_bytes, visibility, created_at_utc)
            VALUES (@version, @type, @index, @hash, @rows, @uncompressed, @stored, @visibility, @at);
            """);
        BindTime(command, "@at", at);
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
