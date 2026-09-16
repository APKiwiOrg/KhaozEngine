using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The id half: the two high-water marks per type, the families and their aligned blocks, and the four
/// durable writes the reserve-before-issue rule spends (spec 4.7, contracts 5.2 and 6.2).
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
public sealed partial class SqlServerContentAuthoringStore
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
    public Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => ReadAsync((scope, token) => ReadFamiliesAsync(scope, type, null, token), cancellationToken);

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

        long familyId = await WriteAsync(
            async (scope, token) =>
            {
                if (await FamilyKeyTakenAsync(scope, type, familyKey, token).ConfigureAwait(false))
                {
                    throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"Content type {type.Value} already carries a family keyed '{familyKey}', and a family key is unique within its type."),
                        type,
                        0,
                        ContentAuthoringException.FamilyDeclarationReason);
                }

                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);

                await using SqlCommand insert = Command(
                    scope,
                    """
                    INSERT INTO dbo.catalog_family(type_id, family_key, block_size, retired, created_in_version)
                    VALUES (@type, @key, @size, 0, @created);
                    SELECT CAST(SCOPE_IDENTITY() AS bigint);
                    """);
                BindInt(insert, "@type", (int)type.Value);
                BindText(insert, "@key", familyKey);
                BindInt(insert, "@size", blockSize);

                // The version the family will FIRST APPEAR IN, which is the active version plus one and never
                // the active version: creating a family is an immediate action while the active version is
                // still 0 on a database that has published nothing.
                BindInt(insert, "@created", active + 1);
                object? raw = await insert.ExecuteScalarAsync(token).ConfigureAwait(false);
                return raw is long identity
                    ? identity
                    : throw new ContentAuthoringException(
                        FormattableString.Invariant(
                            $"The insert of family '{familyKey}' returned no identity, so nothing can reserve a block for it."),
                        type,
                        0,
                        ContentAuthoringException.SchemaMismatchReason);
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await _allocator.ReserveBlockAsync(familyId, cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException)
        {
            await DeleteFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return await WriteAsync(
            async (scope, token) =>
            {
                ContentFamily created = await RequireFamilyAsync(scope, familyId, token).ConfigureAwait(false);
                await AppendAuditAsync(
                    scope,
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
                    token).ConfigureAwait(false);
                return created;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ContentIdHighWater> ReadHighWaterAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default)
        => ReadAsync((scope, token) => ReadHighWaterAsync(scope, type, token), cancellationToken);

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
    public Task CommitReservedThroughAsync(
        ContentTypeId type,
        int reservedThrough,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            async (scope, token) =>
            {
                ContentIdHighWater mark = await ReadHighWaterAsync(scope, type, token).ConfigureAwait(false);
                if (reservedThrough < mark.ReservedThrough)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(reservedThrough),
                        reservedThrough,
                        FormattableString.Invariant(
                            $"Content type {type.Value} has reserved through {mark.ReservedThrough}, and a durable promise is never taken back."));
                }

                await WriteHighWaterAsync(
                    scope, type, mark with { ReservedThrough = reservedThrough }, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task CommitIssuedThroughAsync(
        ContentTypeId type,
        int issuedThrough,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            async (scope, token) =>
            {
                ContentIdHighWater mark = await ReadHighWaterAsync(scope, type, token).ConfigureAwait(false);
                await WriteHighWaterAsync(scope, type, WithIssued(type, mark, issuedThrough), token)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<ContentFamily?> ReadFamilyAsync(
        long familyId,
        CancellationToken cancellationToken = default)
        => ReadAsync((scope, token) => ReadFamilyAsync(scope, familyId, token), cancellationToken);

    /// <inheritdoc />
    public Task<ContentFamilyBlock> CommitFamilyBlockAsync(
        long familyId,
        int baseId,
        int issuedThrough,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            async (scope, token) =>
            {
                ContentFamily family = await RequireFamilyAsync(scope, familyId, token).ConfigureAwait(false);
                int active = await ReadActiveVersionAsync(scope, token).ConfigureAwait(false);

                var block = new ContentFamilyBlock(
                    familyId, family.Blocks.Count, baseId, family.BlockSize, baseId, active + 1);

                await using (SqlCommand insert = Command(
                    scope,
                    """
                    INSERT INTO dbo.catalog_family_block(
                        family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
                    VALUES (@family, @blockOrdinal, @base, @size, @next, @version);
                    """))
                {
                    BindBigInt(insert, "@family", familyId);
                    BindInt(insert, "@blockOrdinal", block.BlockOrdinal);
                    BindInt(insert, "@base", block.BaseId);
                    BindInt(insert, "@size", block.BlockSize);
                    BindInt(insert, "@next", block.NextFreeId);
                    BindInt(insert, "@version", block.ReservedInVersion);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // The advance rides with the insert, because a block row written without it leaves the plain
                // counter walking into the new block.
                ContentIdHighWater mark = await ReadHighWaterAsync(scope, family.Type, token)
                    .ConfigureAwait(false);
                await WriteHighWaterAsync(
                    scope, family.Type, WithIssued(family.Type, mark, issuedThrough), token)
                    .ConfigureAwait(false);

                return block;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task CommitFamilyNextFreeIdAsync(
        long familyId,
        int blockOrdinal,
        int nextFreeId,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            async (scope, token) =>
            {
                ContentFamily family = await RequireFamilyAsync(scope, familyId, token).ConfigureAwait(false);
                ContentFamilyBlock block = family.Blocks[blockOrdinal];
                if (nextFreeId < block.NextFreeId || nextFreeId > block.TopExclusive)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(nextFreeId),
                        nextFreeId,
                        FormattableString.Invariant(
                            $"Block {blockOrdinal} of family {familyId} spans [{block.BaseId}, {block.TopExclusive}) and stands at {block.NextFreeId}."));
                }

                await using SqlCommand update = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_family_block SET next_free_id = @next
                    WHERE family_id = @family AND block_ordinal = @blockOrdinal;
                    """);
                BindInt(update, "@next", nextFreeId);
                BindBigInt(update, "@family", familyId);
                BindInt(update, "@blockOrdinal", blockOrdinal);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>One family with its blocks, or null. The caller owns the scope.</summary>
    static async Task<ContentFamily?> ReadFamilyAsync(
        SqlServerCatalogScope scope,
        long familyId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ContentFamily> found = await ReadFamiliesAsync(
            scope, default, familyId, cancellationToken).ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    static async Task<ContentFamily> RequireFamilyAsync(
        SqlServerCatalogScope scope,
        long familyId,
        CancellationToken cancellationToken)
        => await ReadFamilyAsync(scope, familyId, cancellationToken).ConfigureAwait(false)
            ?? throw new ContentAuthoringException(
                FormattableString.Invariant($"Family {familyId} is not in the store."),
                default,
                0,
                ContentAuthoringException.UnknownFamilyReason);

    /// <summary>
    /// Families with their blocks in ordinal order, filtered to one type (id 0 means all) or to one family.
    /// The caller owns the scope.
    /// </summary>
    static async Task<IReadOnlyList<ContentFamily>> ReadFamiliesAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        long? familyId,
        CancellationToken cancellationToken)
    {
        var blocks = new Dictionary<long, List<ContentFamilyBlock>>();
        await using (SqlCommand read = Command(
            scope,
            """
            SELECT b.family_id, b.block_ordinal, b.base_id, b.block_size, b.next_free_id, b.reserved_in_version
            FROM dbo.catalog_family_block b
            JOIN dbo.catalog_family f ON f.family_id = b.family_id
            WHERE (@type = 0 OR f.type_id = @type)
              AND (@family IS NULL OR b.family_id = @family)
            ORDER BY b.family_id, b.block_ordinal;
            """))
        {
            BindInt(read, "@type", (int)type.Value);
            BindBigInt(read, "@family", familyId);
            await using SqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
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
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }
        }

        await using SqlCommand command = Command(
            scope,
            """
            SELECT family_id, type_id, family_key, block_size, retired, created_in_version
            FROM dbo.catalog_family
            WHERE (@type = 0 OR type_id = @type)
              AND (@family IS NULL OR family_id = @family)
            ORDER BY family_id;
            """);
        BindInt(command, "@type", (int)type.Value);
        BindBigInt(command, "@family", familyId);

        var families = new List<ContentFamily>();
        await using SqlDataReader rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long owner = rows.GetInt64(0);
            families.Add(new ContentFamily(
                owner,
                new ContentTypeId((ushort)rows.GetInt32(1)),
                rows.GetString(2),
                rows.GetInt32(3),
                rows.GetInt32(4) != 0,
                rows.GetInt32(5),
                blocks.TryGetValue(owner, out List<ContentFamilyBlock>? held) ? held : []));
        }

        return families;
    }

    static async Task<bool> FamilyKeyTakenAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        string familyKey,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "SELECT 1 FROM dbo.catalog_family WHERE type_id = @type AND family_key = @key;");
        BindInt(command, "@type", (int)type.Value);
        BindText(command, "@key", familyKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Takes a family row back when its first reservation refused. It is the ONLY delete of a family row
    /// anywhere here: a family is otherwise retired rather than deleted, and this one undoes a creation that
    /// never completed.
    /// </summary>
    Task DeleteFamilyAsync(long familyId, CancellationToken cancellationToken)
        => WriteAsync(
            async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope, "DELETE FROM dbo.catalog_family WHERE family_id = @family;");
                BindBigInt(command, "@family", familyId);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>The type's two marks, <c>(0, 0)</c> for a type that has allocated nothing. Scope held.</summary>
    static async Task<ContentIdHighWater> ReadHighWaterAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            "SELECT reserved_through, issued_through FROM dbo.catalog_id_high_water WHERE type_id = @type;");
        BindInt(command, "@type", (int)type.Value);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ContentIdHighWater(reader.GetInt32(0), reader.GetInt32(1))
            : default;
    }

    static async Task WriteHighWaterAsync(
        SqlServerCatalogScope scope,
        ContentTypeId type,
        ContentIdHighWater mark,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            MERGE dbo.catalog_id_high_water WITH (HOLDLOCK) AS target
            USING (SELECT @type AS type_id) AS source ON target.type_id = source.type_id
            WHEN MATCHED THEN UPDATE SET
                reserved_through = @reserved,
                issued_through = @issued
            WHEN NOT MATCHED THEN INSERT (type_id, reserved_through, issued_through)
                VALUES (@type, @reserved, @issued);
            """);
        BindInt(command, "@type", (int)type.Value);
        BindInt(command, "@reserved", mark.ReservedThrough);
        BindInt(command, "@issued", mark.IssuedThrough);
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
