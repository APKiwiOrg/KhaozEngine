using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The id half: the two high-water marks per type, the families and their aligned blocks, the four durable
/// writes the reserve-before-issue rule spends (spec 4.7, contracts 5.2 and 6.2), and the seeding raise a
/// carried id needs.
/// <para>
/// <b>Every <c>Commit</c> member here commits ON ITS OWN</b>, in its own transaction, and that is the whole
/// of the rule rather than an implementation detail. The allocator waits for
/// <see cref="CommitReservedThroughAsync"/> before any id below the new mark leaves it, so a crash between
/// the two writes skips ids that were never issued instead of reissuing one. Batching the pair into one
/// transaction would leave the same two numbers behind afterwards and invert the guarantee.
/// </para>
/// <para>
/// <b>Reserving a block advances the type's issued mark to the block top in the SAME transaction as the
/// block insert.</b> A block row written without the advance would leave the plain counter walking under the
/// block and handing out an id inside it a second time, to a row that is not in the family.
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public Task<int> AllocateAsync(
        ContentTypeId type,
        int count,
        CancellationToken cancellationToken = default)
    {
        RequireType(type);
        return _allocator.AllocateAsync(type, count, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default)
        => _allocator.AllocateInFamilyAsync(familyId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFamiliesAsync(type, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The family row and its first block are two commits, because the block goes through the allocator's
    /// reserve-then-issue path like every other one. A reservation that refuses takes the family row back
    /// with it, so a caller is never left holding a family that can never issue an id.
    /// </remarks>
    public async Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(familyKey);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        RequireType(type);

        if (!ContentFamily.IsLegalBlockSize(blockSize))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Family '{familyKey}' declares a block size of {blockSize}, and a block size is a power of two between {ContentFamily.MinBlockSize} and {ContentFamily.MaxBlockSize}. It is fixed at creation, because changing it would move every id in the family."),
                type,
                0,
                ContentAuthoringException.FamilyDeclarationReason);
        }

        long familyId;
        using (SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            if (await FamilyKeyTakenAsync(type, familyKey, transaction, cancellationToken).ConfigureAwait(false))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Content type {type.Value} already carries a family keyed '{familyKey}', and a family key is unique within its type."),
                    type,
                    0,
                    ContentAuthoringException.FamilyDeclarationReason);
            }

            long active = await ReadLongAsync(
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
                .ConfigureAwait(false);

            using (SqliteCommand insert = Command(
                """
                INSERT INTO catalog_family(type_id, family_key, block_size, retired, created_in_version)
                VALUES ($type, $key, $size, 0, $created);
                """,
                transaction))
            {
                Bind(insert, "$type", (long)type.Value);
                Bind(insert, "$key", familyKey);
                Bind(insert, "$size", (long)blockSize);

                // The version the family will FIRST APPEAR IN, which is the active version plus one and never
                // the active version: creating a family is an immediate action while the active version is
                // still 0 on a database that has published nothing.
                Bind(insert, "$created", active + 1);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            familyId = await ReadLongAsync("SELECT last_insert_rowid();", transaction, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
        }

        try
        {
            await _allocator.ReserveBlockAsync(familyId, cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException)
        {
            await DeleteFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        using SqliteStoreLease after = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction auditing = _connection.BeginTransaction();
        ContentFamily created = await RequireFamilyAsync(familyId, auditing, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            auditing,
            ContentAuditActions.FamilyCreate,
            actor,
            operatorId,
            type,
            0,
            new ContentKey(familyKey),
            string.Empty,
            null,
            Render(created.Blocks[0].BaseId),
            0,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        auditing.Commit();
        return created;
    }

    /// <inheritdoc />
    public async Task<ContentIdHighWater> ReadHighWaterAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHighWaterAsync(type, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The ceiling is a DECLARATION rather than a stored fact, so it is read from the registry the
    /// same way every other backend reads it, and <c>catalog_type.max_definition_id</c> is the record of what
    /// this process declared rather than a second source of truth.</remarks>
    public Task<int?> ReadMaxDefinitionIdAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => Task.FromResult(
            _registry.TryGet(type, out ContentTypeRegistration? registration)
                ? registration.MaxDefinitionId
                : null);

    /// <inheritdoc />
    public async Task CommitReservedThroughAsync(
        ContentTypeId type,
        int reservedThrough,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ContentIdHighWater mark = await ReadHighWaterAsync(type, transaction, cancellationToken).ConfigureAwait(false);
        if (reservedThrough < mark.ReservedThrough)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reservedThrough),
                reservedThrough,
                FormattableString.Invariant(
                    $"Content type {type.Value} has reserved through {mark.ReservedThrough}, and a durable promise is never taken back."));
        }

        await WriteHighWaterAsync(
            type, mark with { ReservedThrough = reservedThrough }, transaction, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task CommitIssuedThroughAsync(
        ContentTypeId type,
        int issuedThrough,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ContentIdHighWater mark = await ReadHighWaterAsync(type, transaction, cancellationToken).ConfigureAwait(false);
        await WriteHighWaterAsync(type, WithIssued(type, mark, issuedThrough), transaction, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<bool> CommitCarriedThroughAsync(
        ContentTypeId type,
        int carriedThrough,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(carriedThrough);
        bool reserved = await RaiseToCarriedAsync(type, carriedThrough, issued: false, cancellationToken)
            .ConfigureAwait(false);
        bool issued = await RaiseToCarriedAsync(type, carriedThrough, issued: true, cancellationToken)
            .ConfigureAwait(false);
        return reserved || issued;
    }

    /// <summary>
    /// One mark raised to at least a carried id, compared and written in ONE transaction. The issued mark
    /// cannot pass the reserved one here, because the reserved mark already covers the id and never falls.
    /// </summary>
    async Task<bool> RaiseToCarriedAsync(
        ContentTypeId type,
        int carriedThrough,
        bool issued,
        CancellationToken cancellationToken)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ContentIdHighWater mark = await ReadHighWaterAsync(type, transaction, cancellationToken).ConfigureAwait(false);
        ContentIdHighWater raised = issued
            ? mark with { IssuedThrough = Math.Max(mark.IssuedThrough, carriedThrough) }
            : mark with { ReservedThrough = Math.Max(mark.ReservedThrough, carriedThrough) };
        if (raised == mark)
        {
            return false;
        }

        await WriteHighWaterAsync(type, raised, transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async Task<ContentFamily?> ReadFamilyAsync(
        long familyId,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFamilyAsync(familyId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentFamilyBlock> CommitFamilyBlockAsync(
        long familyId,
        int baseId,
        int issuedThrough,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ContentFamily family = await RequireFamilyAsync(familyId, transaction, cancellationToken)
            .ConfigureAwait(false);
        long active = await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);

        var block = new ContentFamilyBlock(
            familyId, family.Blocks.Count, baseId, family.BlockSize, baseId, (int)active + 1);

        using (SqliteCommand insert = Command(
            """
            INSERT INTO catalog_family_block(
                family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
            VALUES ($family, $ordinal, $base, $size, $next, $version);
            """,
            transaction))
        {
            Bind(insert, "$family", familyId);
            Bind(insert, "$ordinal", (long)block.BlockOrdinal);
            Bind(insert, "$base", (long)block.BaseId);
            Bind(insert, "$size", (long)block.BlockSize);
            Bind(insert, "$next", (long)block.NextFreeId);
            Bind(insert, "$version", (long)block.ReservedInVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The advance rides with the insert, because a block row written without it leaves the plain counter
        // walking into the new block.
        ContentIdHighWater mark = await ReadHighWaterAsync(family.Type, transaction, cancellationToken)
            .ConfigureAwait(false);
        await WriteHighWaterAsync(
            family.Type, WithIssued(family.Type, mark, issuedThrough), transaction, cancellationToken)
            .ConfigureAwait(false);

        transaction.Commit();
        return block;
    }

    /// <inheritdoc />
    public async Task CommitFamilyNextFreeIdAsync(
        long familyId,
        int blockOrdinal,
        int nextFreeId,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ContentFamily family = await RequireFamilyAsync(familyId, transaction, cancellationToken)
            .ConfigureAwait(false);
        ContentFamilyBlock block = family.Blocks[blockOrdinal];
        if (nextFreeId < block.NextFreeId || nextFreeId > block.TopExclusive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextFreeId),
                nextFreeId,
                FormattableString.Invariant(
                    $"Block {blockOrdinal} of family {familyId} spans [{block.BaseId}, {block.TopExclusive}) and stands at {block.NextFreeId}."));
        }

        using (SqliteCommand update = Command(
            """
            UPDATE catalog_family_block SET next_free_id = $next
            WHERE family_id = $family AND block_ordinal = $ordinal;
            """,
            transaction))
        {
            Bind(update, "$next", (long)nextFreeId);
            Bind(update, "$family", familyId);
            Bind(update, "$ordinal", (long)blockOrdinal);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    /// <summary>One family with its blocks, or null. The caller already holds the lease.</summary>
    async Task<ContentFamily?> ReadFamilyAsync(
        long familyId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentFamily> found = await ReadFamiliesAsync(
            default, familyId, transaction, cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    async Task<ContentFamily> RequireFamilyAsync(
        long familyId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
        => await ReadFamilyAsync(familyId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant($"Family {familyId} is not in the store."),
                default,
                0,
                ContentAuthoringException.UnknownFamilyReason);

    /// <summary>
    /// Families with their blocks in ordinal order, filtered to one type (id 0 means all) or to one family.
    /// The caller already holds the lease.
    /// </summary>
    async Task<IReadOnlyList<ContentFamily>> ReadFamiliesAsync(
        ContentTypeId type,
        long? familyId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var blocks = new Dictionary<long, List<ContentFamilyBlock>>();
        using (SqliteCommand read = Command(
            """
            SELECT b.family_id, b.block_ordinal, b.base_id, b.block_size, b.next_free_id, b.reserved_in_version
            FROM catalog_family_block b
            JOIN catalog_family f ON f.family_id = b.family_id
            WHERE ($type = 0 OR f.type_id = $type)
              AND ($family IS NULL OR b.family_id = $family)
            ORDER BY b.family_id, b.block_ordinal;
            """,
            transaction))
        {
            Bind(read, "$type", (long)type.Value);
            Bind(read, "$family", familyId);
            using SqliteDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long owner = reader.GetInt64(0);
                if (!blocks.TryGetValue(owner, out List<ContentFamilyBlock>? held))
                {
                    held = [];
                    blocks.Add(owner, held);
                }

                held.Add(new ContentFamilyBlock(
                    owner,
                    (int)reader.GetInt64(1),
                    (int)reader.GetInt64(2),
                    (int)reader.GetInt64(3),
                    (int)reader.GetInt64(4),
                    (int)reader.GetInt64(5)));
            }
        }

        using SqliteCommand command = Command(
            """
            SELECT family_id, type_id, family_key, block_size, retired, created_in_version
            FROM catalog_family
            WHERE ($type = 0 OR type_id = $type)
              AND ($family IS NULL OR family_id = $family)
            ORDER BY family_id;
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$family", familyId);

        var families = new List<ContentFamily>();
        using SqliteDataReader rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long owner = rows.GetInt64(0);
            families.Add(new ContentFamily(
                owner,
                new ContentTypeId((ushort)rows.GetInt64(1)),
                rows.GetString(2),
                (int)rows.GetInt64(3),
                rows.GetInt64(4) != 0,
                (int)rows.GetInt64(5),
                blocks.TryGetValue(owner, out List<ContentFamilyBlock>? held) ? held : []));
        }

        return families;
    }

    async Task<bool> FamilyKeyTakenAsync(
        ContentTypeId type,
        string familyKey,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT 1 FROM catalog_family WHERE type_id = $type AND family_key = $key;", transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$key", familyKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Takes a family row back when its first reservation refused. It is the ONLY delete of a family row
    /// anywhere here: a family is otherwise retired rather than deleted, and this one undoes a creation that
    /// never completed.
    /// </summary>
    async Task DeleteFamilyAsync(long familyId, CancellationToken cancellationToken)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = Command("DELETE FROM catalog_family WHERE family_id = $family;");
        Bind(command, "$family", familyId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The type's two marks, <c>(0, 0)</c> for a type that has allocated nothing. Lease held.</summary>
    async Task<ContentIdHighWater> ReadHighWaterAsync(
        ContentTypeId type,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT reserved_through, issued_through FROM catalog_id_high_water WHERE type_id = $type;",
            transaction);
        Bind(command, "$type", (long)type.Value);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ContentIdHighWater((int)reader.GetInt64(0), (int)reader.GetInt64(1))
            : default;
    }

    async Task WriteHighWaterAsync(
        ContentTypeId type,
        ContentIdHighWater mark,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            INSERT INTO catalog_id_high_water(type_id, reserved_through, issued_through)
            VALUES ($type, $reserved, $issued)
            ON CONFLICT(type_id) DO UPDATE SET
                reserved_through = excluded.reserved_through,
                issued_through = excluded.issued_through;
            """,
            transaction);
        Bind(command, "$type", (long)type.Value);
        Bind(command, "$reserved", (long)mark.ReservedThrough);
        Bind(command, "$issued", (long)mark.IssuedThrough);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The two invariants the column <c>CHECK</c>s also carry, surfaced as an argument refusal so a defect
    /// names the caller rather than arriving as a constraint error.
    /// </summary>
    static ContentIdHighWater WithIssued(ContentTypeId type, ContentIdHighWater mark, int issuedThrough)
    {
        if (issuedThrough < mark.IssuedThrough)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issuedThrough),
                issuedThrough,
                FormattableString.Invariant(
                    $"Content type {type.Value} has issued through {mark.IssuedThrough}, and an issued mark never moves backwards, because an id is never reused."));
        }

        if (issuedThrough > mark.ReservedThrough)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issuedThrough),
                issuedThrough,
                FormattableString.Invariant(
                    $"Content type {type.Value} has reserved through {mark.ReservedThrough}, so issuing through {issuedThrough} would hand out an id nothing has promised to keep. Reserve before issue."));
        }

        return mark with { IssuedThrough = issuedThrough };
    }
}
