using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The version 2 to 3 migration against a POPULATED catalog built from the embedded version 2 script, and the
/// version 1 chain built from the embedded version 1 script. A legacy row takes a time only where the database
/// proves it.
/// </summary>
public sealed partial class SqlServerCatalogRowTimestampTests
{
    /// <summary>The first published version's time in the legacy catalog.</summary>
    static readonly DateTimeOffset FirstPublished = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The second published version's time in the legacy catalog.</summary>
    static readonly DateTimeOffset SecondPublished = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The draft's open time and its one edit's time in the legacy catalog.</summary>
    static readonly DateTimeOffset DraftOpened = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The ledger row's time in the legacy catalog.</summary>
    static readonly DateTimeOffset LedgerRecorded = new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a race fact waits for both opens to queue on the schema lock.</summary>
    static readonly TimeSpan WaiterBound = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Two published versions, row 1 revised by version 2, a closed row whose replacing version is not in the
    /// table, a family and its block, a remap rule, the id marks, an open draft and an audit row. Every statement
    /// is valid against both the version 1 and the version 2 script.
    /// </summary>
    const string PopulateLegacy = """
        INSERT INTO dbo.catalog_type(
            type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
        VALUES (1024, N'thing', 256, 0, NULL, 0);

        INSERT INTO dbo.catalog_version(
            version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
            minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
        VALUES (1, N'1111111111111111111111111111111111111111111111111111111111111111',
                   N'1111111111111111111111111111111111111111111111111111111111111111',
                   0, 0, 1, 0, N'seed', N'first', '2026-01-01T00:00:00+00:00'),
               (2, N'2222222222222222222222222222222222222222222222222222222222222222',
                   N'2222222222222222222222222222222222222222222222222222222222222222',
                   0, 0, 1, 1, N'seed', N'second', '2026-01-02T00:00:00+00:00');

        SET IDENTITY_INSERT dbo.catalog_family ON;
        INSERT INTO dbo.catalog_family(family_id, type_id, family_key, block_size, retired, created_in_version)
        VALUES (1, 1024, N'swords', 16, 0, 1);
        SET IDENTITY_INSERT dbo.catalog_family OFF;

        INSERT INTO dbo.catalog_family_block(
            family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
        VALUES (1, 0, 16, 16, 17, 1);

        INSERT INTO dbo.catalog_row(
            type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
            family_id, retired)
        VALUES (1024, 1, 1, 2, N'one', 0, NULL, 0),
               (1024, 1, 2, NULL, N'one', 0, NULL, 0),
               (1024, 2, 1, NULL, N'two', 0, NULL, 0),
               (1024, 3, 1, 9, N'three', 0, NULL, 0);

        INSERT INTO dbo.catalog_row_field(
            type_id, definition_id, valid_from_version, field_name, field_kind, int_value, text_value,
            blob_value)
        VALUES (1024, 1, 1, N'value', 0, 11, NULL, NULL),
               (1024, 1, 2, N'value', 0, 12, NULL, NULL),
               (1024, 2, 1, N'value', 0, 22, NULL, NULL);

        INSERT INTO dbo.catalog_chunk(
            version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes,
            visibility)
        VALUES (1, 1024, 0, N'1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 0),
               (1, 1024, 0, N'1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 1),
               (2, 1024, 0, N'2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 0),
               (2, 1024, 0, N'2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 1);

        INSERT INTO dbo.catalog_remap_rule([sequence], introduced_in, type_id, kind, from_id, to_id, payload)
        VALUES (1, 2, 1024, 1, 2, 0, 0x);

        INSERT INTO dbo.catalog_id_high_water(type_id, reserved_through, issued_through)
        VALUES (1024, 31, 31);

        INSERT INTO dbo.catalog_draft(
            draft_key, base_version, opened_by, opened_at_utc, note, frozen_for_base_version)
        VALUES (1, 2, N'operator', '2026-01-03T00:00:00+00:00', N'an open draft', NULL);

        INSERT INTO dbo.catalog_draft_edit(
            type_id, definition_id, content_key, operation, retire_policy, replacement_id, fork_key,
            fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc)
        VALUES (1024, 2, N'two', 2, 0, 0, NULL, NULL, NULL, 0, N'operator', '2026-01-03T00:00:00+00:00');

        INSERT INTO dbo.catalog_draft_edit_field(
            edit_ordinal, field_name, field_kind, int_value, text_value, blob_value)
        SELECT edit_ordinal, N'value', 0, 33, NULL, NULL FROM dbo.catalog_draft_edit;

        INSERT INTO dbo.catalog_audit(
            occurred_at_utc, actor, [operator], action, type_id, definition_id, content_key, field_name,
            before_value, after_value, version_number, note)
        VALUES ('2026-01-01T00:00:00+00:00', N'seed', N'', N'publish', 1024, 1, N'one', N'value',
                NULL, N'11', 1, N'first');

        UPDATE dbo.catalog_metadata
        SET store_epoch = N'0123456789abcdef0123456789abcdef', active_version = 2, pinned_version = 1
        WHERE metadata_key = 1;
        """;

    /// <summary>The version 2 half of the legacy content: one ledger row.</summary>
    const string PopulateLedger = """
        INSERT INTO dbo.catalog_content_upgrade(
            upgrade_id, upgrade_order, disposition, version_number, actor, [operator], recorded_at_utc)
        VALUES (N'harvest-profiles', 1, N'baseline', 2, N'seed', N'', '2026-01-02T12:00:00+00:00');
        """;

    [CatalogSqlServerFact]
    public async Task Version_2_catalog_migrates_and_backfills_from_publish_time()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionTwo(database);
        Assert.Equal(0, NewColumnCount(database));

        var clock = new ManualClock(T0);
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        AssertLegacyBackfill(database);
        Assert.Equal(LedgerRecorded, Value(database, "SELECT recorded_at_utc FROM dbo.catalog_content_upgrade;"));

        // A migrated catalog is written like a fresh one, and the legacy rows stay as the migration left them.
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(13))], Actor, Operator, "later");
        DateTimeOffset now = clock.Now;
        Assert.Equal((now, now), EditTimes(database, "one"));
        Assert.Equal((null, DraftOpened), EditTimes(database, "two"));
        Assert.Equal((DraftOpened, now), DraftTimes(database));
    }

    [CatalogSqlServerFact]
    public async Task Version_1_catalog_chains_to_version_3()
    {
        using var database = new SqlServerCatalogDatabase();
        database.Execute(SqlServerCatalogSchema.VersionOneSchemaSql);
        database.Execute(PopulateLegacy);

        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), new ManualClock(T0).Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        Assert.Empty(await store.ListUpgradesAsync());
        Assert.Equal(NewColumns.Length, NewColumnCount(database));
        AssertLegacyBackfill(database);
    }

    [CatalogSqlServerFact]
    public Task Half_migrated_catalog_with_one_column_added_finishes_on_reopen() => HalfMigratedFinishesOnReopenAsync(1);

    [CatalogSqlServerFact]
    public Task Half_migrated_catalog_with_seven_columns_added_finishes_on_reopen() => HalfMigratedFinishesOnReopenAsync(7);

    [CatalogSqlServerFact]
    public Task Half_migrated_catalog_with_every_column_added_finishes_on_reopen() => HalfMigratedFinishesOnReopenAsync(16);

    [CatalogSqlServerFact]
    public async Task ValidateOnly_refuses_version_2_naming_catalog_v3_row_timestamps()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionTwo(database);
        string before = Fingerprint(database);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains("at unsupported version '2'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, Fingerprint(database));
        Assert.Equal(2, Count(database, "SELECT schema_version FROM dbo.catalog_metadata;"));
        Assert.Equal(0, NewColumnCount(database));
    }

    /// <summary>
    /// Version 3 is columns and nothing else, so a catalog that says version 3 without one of them is caught by
    /// the column comparison alone, and the refusal names the column.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task A_version_3_catalog_missing_a_row_time_column_is_refused_naming_it()
    {
        using var database = new SqlServerCatalogDatabase();
        var created = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("ALTER TABLE dbo.catalog_row DROP COLUMN updated_at_utc;");

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains("missing row time column 'catalog_row.updated_at_utc", refused.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version 2 catalog may carry a version 3 column only in the exact shape the migration adds it in. One in
    /// any other shape is not a half-finished migration, and it is refused rather than migrated around.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task A_version_2_catalog_with_a_version_3_column_in_another_shape_is_refused()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionTwo(database);
        database.Execute("ALTER TABLE dbo.catalog_row ADD created_at_utc datetimeoffset(3) NULL;");

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate));

        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains("catalog_row.created_at_utc|datetimeoffset|3|NULL", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, Count(database, "SELECT schema_version FROM dbo.catalog_metadata;"));
        Assert.Equal(1, NewColumnCount(database));
    }

    /// <summary>
    /// Two hosts opening one version 2 catalog at once are one migration and one host that finds the work done.
    /// The test holds the schema lock until both opens are queued on it, so the race is certain rather than
    /// likely, and the host that gets the lock second re-reads the version inside it.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task Two_opens_race_one_migration()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionTwo(database);
        Assert.Equal(1, Count(database, "SELECT COUNT(*) FROM dbo.catalog_row WHERE definition_id = 2 AND valid_from_version = 1;"));
        using var probe = new SqlServerMigrationBackfillProbe(
            database.ConnectionString, "catalog_row", "definition_id = 2 AND valid_from_version = 1");
        var clock = new ManualClock(T0);

        bool bothWaited;
        Task first;
        Task second;
        await using (var holder = new SqlConnection(database.ConnectionString))
        {
            await holder.OpenAsync();
            await using var held = (SqlTransaction)await holder.BeginTransactionAsync();
            await SqlServerCatalogSchemaValidation.TakeSchemaLockAsync(holder, held, CancellationToken.None);

            first = Task.Run(() => OpenAsync(database, clock));
            second = Task.Run(() => OpenAsync(database, clock));
            bothWaited = await WaitForSchemaLockWaitersAsync(database, 2);
            await held.RollbackAsync();
        }

        await Task.WhenAll(first, second);

        Assert.True(bothWaited, "The two opens never both queued on the schema lock the test held.");
        Assert.Equal(3, Count(database, "SELECT schema_version FROM dbo.catalog_metadata;"));
        Assert.Equal(NewColumns.Length, NewColumnCount(database));
        AssertLegacyBackfill(database);
        Assert.Equal(1, probe.Updates);
    }

    async Task HalfMigratedFinishesOnReopenAsync(int applied)
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionTwo(database);
        foreach (string add in SqlServerCatalogSchema.VersionTwoMigrationSql.Take(applied))
        {
            database.Execute(add);
        }

        Assert.Equal(2, Count(database, "SELECT schema_version FROM dbo.catalog_metadata;"));
        Assert.Equal(applied, NewColumnCount(database));

        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), new ManualClock(T0).Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        Assert.Equal(NewColumns.Length, NewColumnCount(database));
        AssertLegacyBackfill(database);
    }

    /// <summary>
    /// The backfill: a row, its fields, its chunks and a rule take the publish time of the version that wrote
    /// them, a closed row's update time is the publish time of the version that closed it, and every other new
    /// column on a legacy row is NULL. The times that were already there are untouched.
    /// </summary>
    static void AssertLegacyBackfill(SqlServerCatalogDatabase database)
    {
        Assert.Equal((FirstPublished, SecondPublished), RowTimes(database, 1, 1));
        Assert.Equal((SecondPublished, SecondPublished), RowTimes(database, 1, 2));
        Assert.Equal((FirstPublished, FirstPublished), RowTimes(database, 2, 1));

        // Closed by a version the table does not hold, so there is no time to prove and none is guessed.
        Assert.Equal((FirstPublished, null), RowTimes(database, 3, 1));

        AssertEvery(database, "catalog_row_field", "valid_from_version = 1", FirstPublished, "created_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 2", SecondPublished, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 1", FirstPublished, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 2", SecondPublished, "created_at_utc");
        AssertEvery(database, "catalog_remap_rule", "1 = 1", SecondPublished, "created_at_utc");

        Assert.Null(Value(database, "SELECT created_at_utc FROM dbo.catalog_metadata;"));
        Assert.Equal((null, null), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_type;"));
        Assert.Null(Value(database, "SELECT created_at_utc FROM dbo.catalog_family;"));
        Assert.Equal((null, null), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_family_block;"));
        Assert.Equal((null, null), HighWaterTimes(database));
        Assert.Equal((DraftOpened, null), DraftTimes(database));
        Assert.Equal((null, DraftOpened), EditTimes(database, "two"));
        Assert.Null(Value(database, "SELECT created_at_utc FROM dbo.catalog_draft_edit_field;"));

        Assert.Equal(2, Count(database, "SELECT COUNT(*) FROM dbo.catalog_version;"));
        Assert.Equal(SecondPublished, Value(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 2;"));
        Assert.Equal(FirstPublished, Value(database, "SELECT occurred_at_utc FROM dbo.catalog_audit;"));
    }

    /// <summary>The embedded version 2 script as it shipped, then the legacy content.</summary>
    static void WriteVersionTwo(SqlServerCatalogDatabase database)
    {
        database.Execute(SqlServerCatalogSchema.VersionTwoSchemaSql);
        database.Execute(PopulateLegacy);
        database.Execute(PopulateLedger);
        Assert.Equal(2, Count(database, "SELECT schema_version FROM dbo.catalog_metadata;"));
    }

    static async Task OpenAsync(SqlServerCatalogDatabase database, ManualClock clock)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
    }

    /// <summary>Polls until the given number of sessions wait for an exclusive application lock in this database.</summary>
    static async Task<bool> WaitForSchemaLockWaitersAsync(SqlServerCatalogDatabase database, int waiters)
    {
        var bound = Stopwatch.StartNew();
        while (bound.Elapsed < WaiterBound)
        {
            if (Count(database, """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = N'APPLICATION' AND request_mode = N'X' AND request_status = N'WAIT'
                  AND resource_database_id = DB_ID();
                """) >= waiters)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    static int NewColumnCount(SqlServerCatalogDatabase database)
    {
        int count = 0;
        foreach ((string table, string column) in NewColumns)
        {
            count += Count(database, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name = N'{column}';");
        }

        return count;
    }

    /// <summary>Every catalog table's row count and content checksum, which a refused open must leave alone.</summary>
    static string Fingerprint(SqlServerCatalogDatabase database)
    {
        var text = new StringBuilder();
        foreach (string table in SqlServerCatalogSchemaExpectations.Tables.OrderBy(static name => name, StringComparer.Ordinal))
        {
            text.Append(table)
                .Append(':')
                .Append(Count(database, $"SELECT COUNT(*) FROM dbo.{table};"))
                .Append(':')
                .Append(Count(database, $"SELECT COALESCE(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM dbo.{table};"))
                .Append('\n');
        }

        return text.ToString();
    }

    static (DateTimeOffset?, DateTimeOffset?) RowTimes(SqlServerCatalogDatabase database, int definitionId, int validFrom)
        => Pair(database, FormattableString.Invariant(
            $"SELECT created_at_utc, updated_at_utc FROM dbo.catalog_row WHERE definition_id = {definitionId} AND valid_from_version = {validFrom};"));
}
