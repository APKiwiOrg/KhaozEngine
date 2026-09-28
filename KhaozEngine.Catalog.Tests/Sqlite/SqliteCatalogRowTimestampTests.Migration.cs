using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The version 2 to 3 migration against a POPULATED catalog built from frozen DDL: version 1 as it shipped,
/// plus the ledger table version 2 added. A legacy row takes a time only where the database proves it.
/// </summary>
public sealed partial class SqliteCatalogRowTimestampTests
{
    /// <summary>The first published version's time in the legacy catalog.</summary>
    const long FirstPublished = 1700000000000;

    /// <summary>The second published version's time in the legacy catalog.</summary>
    const long SecondPublished = 1700000001000;

    /// <summary>The draft's open time and its one edit's time in the legacy catalog.</summary>
    const long DraftOpened = 1700000002000;

    /// <summary>The one table version 2 added, as it shipped. Frozen on purpose.</summary>
    const string VersionTwoLedgerDdl = """
        CREATE TABLE IF NOT EXISTS catalog_content_upgrade (
            upgrade_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY CHECK (length(upgrade_id) BETWEEN 1 AND 128),
            upgrade_order INTEGER NOT NULL CHECK (upgrade_order >= 1),
            disposition TEXT COLLATE BINARY NOT NULL CHECK (disposition IN ('applied', 'adopted', 'baseline')),
            version_number INTEGER NOT NULL CHECK (version_number >= 0),
            actor TEXT COLLATE BINARY NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            operator TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(operator) <= 128),
            recorded_at_utc INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_catalog_content_upgrade_order
            ON catalog_content_upgrade(upgrade_order, upgrade_id);
        """;

    /// <summary>
    /// Two published versions, row 1 revised by version 2, a closed row whose replacing version is not in the
    /// table, a family and its block, a remap rule, the id marks, an open draft and audit rows.
    /// </summary>
    const string PopulateLegacy = """
        INSERT INTO catalog_type(
            type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
        VALUES (1024, 'thing', 256, 0, NULL, 0);

        INSERT INTO catalog_version(
            version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
            minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
        VALUES (1, '1111111111111111111111111111111111111111111111111111111111111111',
                   '1111111111111111111111111111111111111111111111111111111111111111',
                   0, 0, 1, 0, 'seed', 'first', 1700000000000),
               (2, '2222222222222222222222222222222222222222222222222222222222222222',
                   '2222222222222222222222222222222222222222222222222222222222222222',
                   0, 0, 1, 1, 'seed', 'second', 1700000001000);

        INSERT INTO catalog_family(family_id, type_id, family_key, block_size, retired, created_in_version)
        VALUES (1, 1024, 'swords', 16, 0, 1);

        INSERT INTO catalog_family_block(
            family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
        VALUES (1, 0, 16, 16, 17, 1);

        INSERT INTO catalog_row(
            type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
            family_id, retired)
        VALUES (1024, 1, 1, 2, 'one', 0, NULL, 0),
               (1024, 1, 2, NULL, 'one', 0, NULL, 0),
               (1024, 2, 1, NULL, 'two', 0, NULL, 0),
               (1024, 3, 1, 9, 'three', 0, NULL, 0);

        INSERT INTO catalog_row_field(
            type_id, definition_id, valid_from_version, field_name, field_kind, int_value, text_value,
            blob_value)
        VALUES (1024, 1, 1, 'value', 0, 11, NULL, NULL),
               (1024, 1, 2, 'value', 0, 12, NULL, NULL),
               (1024, 2, 1, 'value', 0, 22, NULL, NULL);

        INSERT INTO catalog_chunk(
            version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes,
            visibility)
        VALUES (1, 1024, 0, '1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 0),
               (1, 1024, 0, '1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 1),
               (2, 1024, 0, '2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 0),
               (2, 1024, 0, '2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 1);

        INSERT INTO catalog_remap_rule(sequence, introduced_in, type_id, kind, from_id, to_id, payload)
        VALUES (1, 2, 1024, 1, 2, 0, x'');

        INSERT INTO catalog_id_high_water(type_id, reserved_through, issued_through)
        VALUES (1024, 31, 31);

        INSERT INTO catalog_draft(draft_key, base_version, opened_by, opened_at_utc, note, frozen_for_base_version)
        VALUES (1, 2, 'operator', 1700000002000, 'an open draft', NULL);

        INSERT INTO catalog_draft_edit(
            edit_ordinal, type_id, definition_id, content_key, operation, retire_policy, replacement_id,
            fork_key, fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc)
        VALUES (1, 1024, 2, 'two', 2, 0, 0, NULL, NULL, NULL, 0, 'operator', 1700000002000);

        INSERT INTO catalog_draft_edit_field(edit_ordinal, field_name, field_kind, int_value, text_value, blob_value)
        VALUES (1, 'value', 0, 33, NULL, NULL);

        INSERT INTO catalog_audit(
            occurred_at_utc, actor, operator, action, type_id, definition_id, content_key, field_name,
            before_value, after_value, version_number, note)
        VALUES (1700000000000, 'seed', '', 'publish', 1024, 1, 'one', 'value', NULL, '11', 1, 'first');

        UPDATE catalog_metadata
        SET store_epoch = '0123456789abcdef0123456789abcdef', active_version = 2, pinned_version = 1
        WHERE metadata_key = 1;
        """;

    /// <summary>The version 2 half of the legacy content: one ledger row.</summary>
    const string PopulateLedger = """
        INSERT INTO catalog_content_upgrade(
            upgrade_id, upgrade_order, disposition, version_number, actor, operator, recorded_at_utc)
        VALUES ('harvest-profiles', 1, 'baseline', 2, 'seed', '', 1700000001500);
        """;

    [Fact]
    public async Task Version_2_catalog_migrates_and_backfills_from_publish_time()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionTwo(database);
        Assert.Equal(0L, NewColumnCount(database));

        var clock = new ManualClock(T0);
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        AssertLegacyBackfill(database);
        Assert.Equal(1700000001500, Value(database, "SELECT recorded_at_utc FROM catalog_content_upgrade;"));

        // A migrated catalog is written like a fresh one, and the legacy rows stay as the migration left them.
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(13))], Actor, Operator, "later");
        long now = clock.Millis;
        Assert.Equal((now, now), EditTimes(database, "one"));
        Assert.Equal((null, DraftOpened), EditTimes(database, "two"));
        Assert.Equal((DraftOpened, now), DraftTimes(database));
    }

    [Fact]
    public async Task Version_1_catalog_chains_to_version_3()
    {
        using var database = new TemporaryCatalogDatabase();
        database.Execute(SqliteCatalogSchemaMigrationTests.VersionOneDdl);
        database.Execute(PopulateLegacy);

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), new ManualClock(T0).Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        Assert.Empty(await store.ListUpgradesAsync());
        Assert.Equal(NewColumns.Length, (int)NewColumnCount(database));
        AssertLegacyBackfill(database);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    public async Task Half_migrated_catalog_finishes_on_reopen(int applied)
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionTwo(database);
        for (int i = 0; i < applied; i++)
        {
            database.Execute(SqliteCatalogSchema.VersionThreeColumns[i].AddColumn);
        }

        Assert.Equal(2L, database.Scalar("SELECT schema_version FROM catalog_metadata;"));
        Assert.Equal(applied, (int)NewColumnCount(database));

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), new ManualClock(T0).Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(3, await store.GetSchemaVersionAsync());
        Assert.Equal(NewColumns.Length, (int)NewColumnCount(database));
        AssertLegacyBackfill(database);
    }

    [Fact]
    public async Task ValidateOnly_refuses_version_2_naming_catalog_v3_row_timestamps()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionTwo(database);
        byte[] before = File.ReadAllBytes(database.DatabasePath);

        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

            Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
            Assert.Contains("at unsupported version '2'", refused.Message, StringComparison.Ordinal);
            Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(database.DatabasePath));
        Assert.Equal(2L, database.Scalar("SELECT schema_version FROM catalog_metadata;"));
        Assert.Equal(0L, NewColumnCount(database));
    }

    /// <summary>
    /// The backfill: a row, its fields, its chunks and a rule take the publish time of the version that wrote
    /// them, a closed row's update time is the publish time of the version that closed it, and every other new
    /// column on a legacy row is NULL. The times that were already there are untouched.
    /// </summary>
    static void AssertLegacyBackfill(TemporaryCatalogDatabase database)
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

        Assert.Null(Value(database, "SELECT created_at_utc FROM catalog_metadata;"));
        Assert.Equal((null, null), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_type;"));
        Assert.Null(Value(database, "SELECT created_at_utc FROM catalog_family;"));
        Assert.Equal((null, null), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_family_block;"));
        Assert.Equal((null, null), HighWaterTimes(database));
        Assert.Equal((DraftOpened, null), DraftTimes(database));
        Assert.Equal((null, DraftOpened), EditTimes(database, "two"));
        Assert.Null(Value(database, "SELECT created_at_utc FROM catalog_draft_edit_field;"));

        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_version;"));
        Assert.Equal(SecondPublished, Value(database, "SELECT published_at_utc FROM catalog_version WHERE version_number = 2;"));
        Assert.Equal(FirstPublished, Value(database, "SELECT occurred_at_utc FROM catalog_audit;"));
    }

    /// <summary>The frozen version 1 DDL moved to version 2 as it shipped, then the legacy content.</summary>
    static void WriteVersionTwo(TemporaryCatalogDatabase database)
    {
        string versionOne = SqliteCatalogSchemaMigrationTests.VersionOneDdl;
        string versionTwo = versionOne.Replace("VALUES (1, 1, lower", "VALUES (1, 2, lower", StringComparison.Ordinal);
        Assert.NotEqual(versionOne, versionTwo);
        database.Execute(versionTwo + "\n" + VersionTwoLedgerDdl);
        database.Execute(PopulateLegacy);
        database.Execute(PopulateLedger);
        Assert.Equal(2L, database.Scalar("SELECT schema_version FROM catalog_metadata;"));
    }

    static long NewColumnCount(TemporaryCatalogDatabase database)
    {
        long count = 0;
        foreach ((string table, string column) in NewColumns)
        {
            count += database.Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';");
        }

        return count;
    }

    static (long?, long?) RowTimes(TemporaryCatalogDatabase database, int definitionId, int validFrom)
        => Pair(database, FormattableString.Invariant(
            $"SELECT created_at_utc, updated_at_utc FROM catalog_row WHERE definition_id = {definitionId} AND valid_from_version = {validFrom};"));
}
