using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// Schema version 4, <c>catalog-v4-text-authoring</c>: verified migration from every older version through the
/// existing chain, the NULL completeness every legacy version keeps, the read-only empty proof that lets a
/// migrated text-free catalog keep publishing, the refusal of a base whose manifests named a language, and the
/// one operation clock every text table is stamped from.
/// </summary>
public sealed class SqliteTextSchemaMigrationTests
{
    /// <summary>
    /// Version 4 undone on a raw connection: the text tables dropped, the two columns dropped, and the metadata
    /// row back at 3. What is left is a version 3 catalog this build validates exactly before migrating it.
    /// </summary>
    internal const string UndoVersionFour = """
        DROP TABLE catalog_draft_text_edit;
        DROP TABLE catalog_draft_text_language;
        DROP TABLE catalog_text;
        DROP TABLE catalog_text_chunk;
        ALTER TABLE catalog_version DROP COLUMN text_snapshot_complete;
        ALTER TABLE catalog_audit DROP COLUMN language_tag;
        UPDATE catalog_metadata SET schema_version = 3 WHERE metadata_key = 1;
        """;

    const string MigrationName = "catalog-v4-text-authoring";

    /// <summary>The family, block and rule the frozen version 1 content does not carry, in version 1 columns.</summary>
    const string PopulateFamiliesAndRules = """
        INSERT INTO catalog_family(family_id, type_id, family_key, block_size, retired, created_in_version)
        VALUES (1, 1024, 'swords', 16, 0, 1);
        INSERT INTO catalog_family_block(
            family_id, block_ordinal, base_id, block_size, next_free_id, reserved_in_version)
        VALUES (1, 0, 16, 16, 17, 1);
        INSERT INTO catalog_remap_rule(sequence, introduced_in, type_id, kind, from_id, to_id, payload)
        VALUES (1, 2, 1024, 1, 2, 0, x'01');
        """;

    /// <summary>Row times a version 3 catalog already proved, which the version 4 migration may not touch.</summary>
    const string StampVersionThree = """
        UPDATE catalog_metadata SET created_at_utc = 1690000000000;
        UPDATE catalog_type SET created_at_utc = 1690000000100, updated_at_utc = 1690000000200;
        UPDATE catalog_family SET created_at_utc = 1690000000300;
        UPDATE catalog_family_block SET created_at_utc = 1690000000400, updated_at_utc = 1690000000500;
        UPDATE catalog_row SET created_at_utc = 1700000000000, updated_at_utc = 1700000001000;
        UPDATE catalog_draft SET updated_at_utc = 1700000002500;
        UPDATE catalog_draft_edit SET created_at_utc = 1700000002000;
        UPDATE catalog_id_high_water SET created_at_utc = 1690000000600, updated_at_utc = 1690000000700;
        INSERT INTO catalog_content_upgrade(
            upgrade_id, upgrade_order, disposition, version_number, actor, operator, recorded_at_utc)
        VALUES ('harvest-profiles', 1, 'baseline', 2, 'seed', '', 1700000001500);
        """;

    static readonly DateTimeOffset T0 = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    static ContentTypeRegistry ThingRegistry() => PublishFixtures.Registry(PublishFixtures.Thing);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Every_older_version_migrates_to_four_keeping_every_row_and_adding_only_empty_text(int version)
    {
        using var database = new TemporaryCatalogDatabase();
        WriteLegacy(database, version);
        IReadOnlyDictionary<string, IReadOnlyList<string>> legacyColumns = Columns(database, version);
        IReadOnlyDictionary<string, IReadOnlyList<string>> before = Dump(database, legacyColumns);

        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, ThingRegistry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            Assert.Equal(4, await store.GetSchemaVersionAsync());
            Assert.Equal("0123456789abcdef0123456789abcdef", await store.GetStoreEpochAsync());
            Assert.Equal(2, await store.GetActiveVersionAsync());
            Assert.Equal(1, await store.GetPinnedVersionAsync());
            Assert.Equal(2, (await store.ListVersionsAsync()).Count);
            Assert.Equal(1L, (await store.ListFamiliesAsync(default)).Single().FamilyId);
            ContentDraft draft = (await store.GetOpenDraftAsync())!;
            Assert.Equal((2, 1, "an open draft"), (draft.BaseVersion, draft.EditCount, draft.Note));
            Assert.Same(ContentDraftTextState.Empty, draft.TextState);
            Assert.All(await store.ListAuditAsync(default, 0, 0, 500), entry => Assert.Null(entry.LanguageTag));
        }

        // Every legacy value of every legacy column, the earlier migrations' backfill included, is where it was.
        IReadOnlyDictionary<string, IReadOnlyList<string>> after = Dump(database, legacyColumns);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach ((string table, IReadOnlyList<string> rows) in before)
        {
            Assert.Equal(rows, after[table]);
        }

        AssertNewTablesEmptyAndLegacyUnknown(database);
        using var validated = new SqliteContentAuthoringStore(database.ConnectionString, ThingRegistry());
        await validated.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    [Fact]
    public async Task ValidateOnly_refuses_version_3_naming_the_migration_and_writes_nothing()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteLegacy(database, 3);
        SqliteConnection.ClearAllPools();
        byte[] file = File.ReadAllBytes(database.DatabasePath);

        foreach (ContentAuthoringSchemaMode mode in new[]
            { ContentAuthoringSchemaMode.ValidateOnly, ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync })
        {
            using var store = new SqliteContentAuthoringStore(database.ConnectionString, ThingRegistry());
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => store.InitializeAsync(mode));
            Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
            Assert.Contains("at unsupported version '3'", refused.Message, StringComparison.Ordinal);
            Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(file, File.ReadAllBytes(database.DatabasePath));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'catalog_text';"));
    }

    [Fact]
    public async Task A_migrated_text_free_catalog_publishes_row_only_then_text_and_each_new_version_is_complete()
    {
        using var database = new TemporaryCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        IContentTextAuthoringStore text = store;
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NULL;"));

        // The proof reads, it never records: the legacy versions stay unknown after being proven empty.
        ContentVersionTextSnapshot legacy = await text.ReadTextSnapshotAsync(2);
        Assert.Empty(legacy.Languages);
        Assert.Empty(legacy.Revisions);
        Assert.NotNull(await store.ExportBundleAsync(2));
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NULL;"));

        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(7) }) }, Actor, Operator, "row only");
        Assert.Equal(3, (await store.PublishAsync(Request(2))).VersionNumber);
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 3;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_text_chunk;"));

        // The row-only rollback cannot prove a legacy target text free, so it is refused rather than guessed,
        // even from a complete text-free version.
        var rollback = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rollback.Reason);
        Assert.Null(await store.GetOpenDraftAsync());

        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal(4, (await store.PublishAsync(Request(3))).VersionNumber);
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 4;"));
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(4)).Revisions).Value);
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NULL;"));
    }

    [Fact]
    public async Task Recorded_chunks_prove_an_empty_base_when_the_pack_lost_its_manifests_until_the_registry_moves()
    {
        using var database = new TemporaryCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);
        Directory.Delete(database.PackRoot, recursive: true);

        // A registry that gained a type no longer rebuilds the recorded manifests, so nothing proves the base.
        ContentTypeRegistry moved = TextRegistry();
        PublishFixtures.Register(moved, PublishFixtures.Other);
        using (var drifted = new SqliteContentAuthoringStore(database.ConnectionString, moved, database.Pack()))
        {
            await drifted.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => ((IContentTextAuthoringStore)drifted).ReadTextSnapshotAsync(2));
            Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, unknown.Reason);
        }

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal(3, (await store.PublishAsync(Request(2))).VersionNumber);
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 3;"));
    }

    [Fact]
    public async Task An_unknown_base_whose_stored_manifests_name_a_language_refuses_before_any_write()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var seeded = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack()))
        {
            await seeded.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await ApplyAsync(seeded, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
            await seeded.PublishAsync(Request(0));
        }

        database.Execute(UndoVersionFour);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        IContentTextAuthoringStore text = store;
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(7) }) }, Actor, Operator, "row only");
        int objects = Directory.GetFiles(database.PackRoot, "*", SearchOption.AllDirectories).Length;
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.PublishAsync(Request(1)));
        Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, refused.Reason);
        Assert.Single(await store.ListVersionsAsync());
        Assert.False((await store.GetOpenDraftAsync())!.IsFrozen);
        Assert.Equal(objects, Directory.GetFiles(database.PackRoot, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(audits, await AuditCountAsync(store));

        foreach (Func<Task> read in new Func<Task>[]
        {
            () => text.ReadTextSnapshotAsync(1),
            () => store.ExportBundleAsync(1),
            () => ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Blade")),
        })
        {
            var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(read);
            Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, unknown.Reason);
        }

        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NULL;"));
    }

    [Fact]
    public async Task Every_text_table_takes_its_times_from_the_one_operation_clock()
    {
        using var database = new TemporaryCatalogDatabase();
        DateTimeOffset now = T0;
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, TextRegistry(), database.Pack(), () => now);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        IContentTextAuthoringStore text = store;

        long t1 = Tick(ref now);
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal((t1, t1), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_draft_text_edit;"));
        Assert.Equal(t1, database.Scalar("SELECT created_at_utc FROM catalog_draft_text_language;"));

        long t2 = Tick(ref now);
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        Assert.Equal((t1, t2), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_draft_text_edit;"));

        long t3 = Tick(ref now);
        await store.PublishAsync(Request(0));
        Assert.Equal(t3, database.Scalar("SELECT published_at_utc FROM catalog_version WHERE version_number = 1;"));
        Assert.Equal((t3, t3), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_text;"));
        Assert.Equal(t3, database.Scalar("SELECT created_at_utc FROM catalog_text_chunk;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_draft_text_edit;"));

        long t4 = Tick(ref now);
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sabre"));
        long t5 = Tick(ref now);
        await store.PublishAsync(Request(1));
        Assert.Equal((t3, t5), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_text WHERE valid_from_version = 1;"));
        Assert.Equal((t5, t5), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_text WHERE valid_from_version = 2;"));
        Assert.Equal(t5, database.Scalar("SELECT created_at_utc FROM catalog_text_chunk WHERE version_number = 2;"));
        Assert.Equal(t4, database.Scalar(
            "SELECT occurred_at_utc FROM catalog_audit WHERE action = 'draft-edit' AND after_value = 'Sabre';"));
        Assert.Equal(0L, database.Scalar(
            "SELECT COUNT(*) FROM catalog_text WHERE created_at_utc IS NULL OR updated_at_utc IS NULL;"));
    }

    /// <summary>
    /// A catalog this build published two row-only versions into, taken back to version 3 and left for the
    /// next AutoCreate open to migrate: exactly the shape of a deployed text-free catalog.
    /// </summary>
    static async Task WriteMigratedTextFreeAsync(TemporaryCatalogDatabase database)
    {
        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "first");
            await store.PublishAsync(Request(0));
            int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
            await store.ApplyEditsAsync(
                new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(5) }) }, Actor, Operator, "second");
            await store.PublishAsync(Request(1));
        }

        database.Execute(UndoVersionFour);
        using var migrated = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await migrated.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        Assert.Equal(4, await migrated.GetSchemaVersionAsync());
        AssertNewTablesEmptyAndLegacyUnknown(database);
    }

    /// <summary>One older version's frozen shape and the legacy content written into it.</summary>
    static void WriteLegacy(TemporaryCatalogDatabase database, int version)
    {
        database.Execute(version switch
        {
            1 => SqliteCatalogSchemaMigrationTests.VersionOneDdl,
            2 => SqliteCatalogSchema.VersionTwoTables,
            _ => SqliteCatalogSchema.VersionThreeTables,
        });
        database.Execute(SqliteCatalogSchemaMigrationTests.PopulateVersionOne);
        database.Execute(PopulateFamiliesAndRules);
        if (version == 3)
        {
            database.Execute(StampVersionThree);
        }

        Assert.Equal((long)version, database.Scalar("SELECT schema_version FROM catalog_metadata;"));
    }

    static void AssertNewTablesEmptyAndLegacyUnknown(TemporaryCatalogDatabase database)
    {
        foreach (string table in new[]
            { "catalog_draft_text_edit", "catalog_draft_text_language", "catalog_text", "catalog_text_chunk" })
        {
            Assert.Equal(0L, database.Scalar($"SELECT COUNT(*) FROM {table};"));
        }

        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NOT NULL;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_audit WHERE language_tag IS NOT NULL;"));
    }

    /// <summary>
    /// The columns of every table the given version declared, as the file holds them now. The metadata row's
    /// version and update time are the migration's to change, so they are left out.
    /// </summary>
    static IReadOnlyDictionary<string, IReadOnlyList<string>> Columns(TemporaryCatalogDatabase database, int version)
    {
        using var connection = new SqliteConnection(database.ConnectionString + ";Pooling=False");
        connection.Open();
        var tables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string table in SqliteCatalogSchemaInventory.TablesAt(version)!)
        {
            var columns = new List<string>();
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                string column = reader.GetString(0);
                if (table != "catalog_metadata" || column is not ("schema_version" or "updated_at_utc"))
                {
                    columns.Add(column);
                }
            }

            tables.Add(table, columns);
        }

        return tables;
    }

    /// <summary>Every row of every given table in rowid order, over the given columns, as text.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<string>> Dump(
        TemporaryCatalogDatabase database,
        IReadOnlyDictionary<string, IReadOnlyList<string>> columns)
    {
        using var connection = new SqliteConnection(database.ConnectionString + ";Pooling=False");
        connection.Open();
        var dump = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach ((string table, IReadOnlyList<string> held) in columns)
        {
            var rows = new List<string>();
            using (SqliteCommand select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT {string.Join(", ", held)} FROM {table} ORDER BY rowid;";
                using SqliteDataReader reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = reader.IsDBNull(i)
                            ? "NULL"
                            : reader.GetValue(i) is byte[] bytes
                                ? Convert.ToHexString(bytes)
                                : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
                    }

                    rows.Add(string.Join("|", values));
                }
            }

            dump.Add(table, rows);
        }

        return dump;
    }

    static long Tick(ref DateTimeOffset now)
    {
        now = now.AddMinutes(1);
        return now.ToUnixTimeMilliseconds();
    }

    static (long, long) Pair(TemporaryCatalogDatabase database, string sql)
    {
        using var connection = new SqliteConnection(database.ConnectionString + ";Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        (long, long) pair = (reader.GetInt64(0), reader.GetInt64(1));
        Assert.False(reader.Read());
        return pair;
    }
}
