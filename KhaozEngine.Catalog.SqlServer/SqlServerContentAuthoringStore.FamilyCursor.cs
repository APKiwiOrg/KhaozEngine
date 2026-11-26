using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

public sealed partial class SqlServerContentAuthoringStore
{
    /// <inheritdoc />
    public Task CommitFamilyCarriedThroughAsync(
        ContentTypeId type,
        long familyId,
        int blockOrdinal,
        int carriedId,
        CancellationToken cancellationToken = default)
        => WriteAsync(
            async (scope, token) =>
            {
                await using SqlCommand update = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_family_block
                    SET next_free_id = @carried + 1, updated_at_utc = @now
                    WHERE family_id = @family AND block_ordinal = @blockOrdinal
                      AND base_id <= 2147483647 - block_size
                      AND base_id <= @carried AND @carried < CAST(base_id AS bigint) + block_size
                      AND next_free_id <= @carried
                      AND EXISTS (SELECT 1 FROM dbo.catalog_family WHERE family_id = @family AND type_id = @type);
                    """);
                BindTime(update, "@now", _clock());
                BindInt(update, "@carried", carriedId);
                BindBigInt(update, "@family", familyId);
                BindInt(update, "@blockOrdinal", blockOrdinal);
                BindInt(update, "@type", type.Value);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    async Task<int?> TakeFamilyIdAsync(
        SqlServerCatalogScope scope,
        long familyId,
        int blockOrdinal,
        CancellationToken cancellationToken)
    {
        // The update lock serializes takes on this block. The primary key on catalog_row bounds the
        // compatibility lookup for published ids left above the cursor by older direct imports.
        await using SqlCommand take = Command(
            scope,
            """
            DECLARE @next int, @top int, @type int, @issued int = 0;
            SELECT @next = b.next_free_id, @top = b.base_id + b.block_size, @type = f.type_id
            FROM dbo.catalog_family_block AS b WITH (UPDLOCK)
            JOIN dbo.catalog_family AS f ON f.family_id = b.family_id
            WHERE b.family_id = @family AND b.block_ordinal = @blockOrdinal;

            IF @next < @top
            BEGIN
                SELECT @next = COALESCE(MAX(definition_id) + 1, @next)
                FROM dbo.catalog_row
                WHERE type_id = @type AND definition_id >= @next AND definition_id < @top;

                IF @next < @top
                BEGIN
                    SET @issued = @next;
                    SET @next = @next + 1;
                END;

                UPDATE dbo.catalog_family_block
                SET next_free_id = @next, updated_at_utc = @now
                OUTPUT @issued
                WHERE family_id = @family AND block_ordinal = @blockOrdinal;
            END;
            """);
        BindTime(take, "@now", _clock());
        BindBigInt(take, "@family", familyId);
        BindInt(take, "@blockOrdinal", blockOrdinal);
        return await take.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as int?;
    }
}
