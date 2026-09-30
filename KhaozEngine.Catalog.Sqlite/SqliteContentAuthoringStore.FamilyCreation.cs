using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>The first family reservation inside the transaction that creates and audits the family.</summary>
public sealed partial class SqliteContentAuthoringStore
{
    async Task<ContentFamilyBlock> ReserveInitialBlockAsync(
        long familyId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ContentFamily family = await RequireFamilyAsync(familyId, transaction, cancellationToken)
            .ConfigureAwait(false);
        ContentIdHighWater mark = await ReadHighWaterAsync(family.Type, transaction, cancellationToken)
            .ConfigureAwait(false);
        long ceiling = RequireType(family.Type).MaxDefinitionId ?? int.MaxValue;
        ContentFamilyBlock block = ContentFamilyReservation.Plan(family, mark, ceiling, family.CreatedInVersion);
        long now = Millis(_clock());
        await InsertFamilyBlockAsync(block, now, transaction, cancellationToken).ConfigureAwait(false);
        int top = block.BaseId + (block.BlockSize - 1);
        await WriteHighWaterAsync(
            family.Type, new ContentIdHighWater(top, top), now, transaction, cancellationToken)
            .ConfigureAwait(false);
        return block;
    }

    async Task InsertFamilyBlockAsync(
        ContentFamilyBlock block,
        long now,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand insert = Command(
            """
            INSERT INTO catalog_family_block(
                family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version,
                created_at_utc, updated_at_utc)
            VALUES ($family, $ordinal, $base, $size, $next, $version, $now, $now);
            """,
            transaction);
        Bind(insert, "$now", now);
        Bind(insert, "$family", block.FamilyId);
        Bind(insert, "$ordinal", (long)block.BlockOrdinal);
        Bind(insert, "$base", (long)block.BaseId);
        Bind(insert, "$size", (long)block.BlockSize);
        Bind(insert, "$next", (long)block.NextFreeId);
        Bind(insert, "$version", (long)block.ReservedInVersion);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
