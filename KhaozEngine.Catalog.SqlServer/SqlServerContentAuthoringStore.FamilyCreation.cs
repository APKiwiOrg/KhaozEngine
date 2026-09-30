using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>The first family reservation inside the transaction that creates and audits the family.</summary>
public sealed partial class SqlServerContentAuthoringStore
{
    async Task<ContentFamilyBlock> ReserveInitialBlockAsync(
        SqlServerCatalogScope scope,
        long familyId,
        ContentIdHighWater mark,
        CancellationToken cancellationToken)
    {
        ContentFamily family = await RequireFamilyAsync(scope, familyId, cancellationToken).ConfigureAwait(false);
        long ceiling = RequireType(family.Type).MaxDefinitionId ?? int.MaxValue;
        ContentFamilyBlock block = ContentFamilyReservation.Plan(family, mark, ceiling, family.CreatedInVersion);
        DateTimeOffset now = _clock();
        await InsertFamilyBlockAsync(scope, block, now, cancellationToken).ConfigureAwait(false);
        int top = block.BaseId + (block.BlockSize - 1);
        await WriteHighWaterAsync(scope, family.Type, new ContentIdHighWater(top, top), now, cancellationToken)
            .ConfigureAwait(false);
        return block;
    }

    static async Task InsertFamilyBlockAsync(
        SqlServerCatalogScope scope,
        ContentFamilyBlock block,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqlCommand insert = Command(
            scope,
            """
            INSERT INTO dbo.catalog_family_block(
                family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version,
                created_at_utc, updated_at_utc)
            VALUES (@family, @blockOrdinal, @base, @size, @next, @version, @now, @now);
            """);
        BindTime(insert, "@now", now);
        BindBigInt(insert, "@family", block.FamilyId);
        BindInt(insert, "@blockOrdinal", block.BlockOrdinal);
        BindInt(insert, "@base", block.BaseId);
        BindInt(insert, "@size", block.BlockSize);
        BindInt(insert, "@next", block.NextFreeId);
        BindInt(insert, "@version", block.ReservedInVersion);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
