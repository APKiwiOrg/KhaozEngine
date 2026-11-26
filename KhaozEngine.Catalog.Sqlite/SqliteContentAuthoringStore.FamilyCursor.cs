using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

public sealed partial class SqliteContentAuthoringStore
{
    /// <inheritdoc />
    public async Task CommitFamilyCarriedThroughAsync(
        ContentTypeId type,
        long familyId,
        int blockOrdinal,
        int carriedId,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand update = Command(
            """
            UPDATE catalog_family_block
            SET next_free_id = $carried + 1, updated_at_utc = $now
            WHERE family_id = $family AND block_ordinal = $ordinal
              AND base_id + block_size <= 2147483647
              AND base_id <= $carried AND $carried < base_id + block_size AND next_free_id <= $carried
              AND EXISTS (SELECT 1 FROM catalog_family WHERE family_id = $family AND type_id = $type);
            """,
            transaction);
        Bind(update, "$carried", (long)carriedId);
        Bind(update, "$now", Millis(_clock()));
        Bind(update, "$family", familyId);
        Bind(update, "$ordinal", (long)blockOrdinal);
        Bind(update, "$type", (long)type.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    async Task<int> ReadNextUnoccupiedFamilyIdAsync(
        ContentTypeId type,
        ContentFamilyBlock block,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Older direct imports could leave published ids above the cursor. The primary key supports this
        // lookup over only the unissued part of the type's block, including retired rows.
        using SqliteCommand read = Command(
            """
            SELECT COALESCE(MAX(definition_id) + 1, $next)
            FROM catalog_row
            WHERE type_id = $type AND definition_id >= $next AND definition_id < $top;
            """,
            transaction);
        Bind(read, "$type", (long)type.Value);
        Bind(read, "$next", (long)block.NextFreeId);
        Bind(read, "$top", (long)block.TopExclusive);
        object? scalar = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (int)(long)scalar!;
    }
}
