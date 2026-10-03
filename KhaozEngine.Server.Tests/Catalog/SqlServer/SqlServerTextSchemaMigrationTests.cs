using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;
using static KhaozEngine.Tests.Catalog.SqlServer.SqlServerTextFixtures;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Schema version 4 on SQL Server, <c>catalog-v4-text-authoring</c>: verified migration from every older
/// version through the existing chain, fresh and migrated parity, the NULL completeness every legacy version
/// keeps, the read-only empty proof that lets a migrated text-free catalog keep publishing and rolling back,
/// the refusal of a base whose manifests named a language, and the one operation clock every text table is
/// stamped from. The live facts are ENV GATED on <c>KE_CATALOG_SQLSERVER</c>, and the offline script pins are
/// in the other half of this class.
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed partial class SqlServerTextSchemaMigrationTests
{
    /// <summary>
    /// Version 4 undone on a raw connection: the text tables dropped, the two columns and their checks dropped,
    /// and the metadata row back at 3. What is left is a version 3 catalog this build validates before migrating.
    /// </summary>
    internal const string UndoVersionFour = """
        DROP TABLE dbo.catalog_draft_text_edit;
        DROP TABLE dbo.catalog_draft_text_language;
        DROP TABLE dbo.catalog_text;
        DROP TABLE dbo.catalog_text_chunk;
        ALTER TABLE dbo.catalog_version DROP CONSTRAINT ck_catalog_version_text_complete;
        ALTER TABLE dbo.catalog_version DROP COLUMN text_snapshot_complete;
        ALTER TABLE dbo.catalog_audit DROP CONSTRAINT ck_catalog_audit_language;
        ALTER TABLE dbo.catalog_audit DROP COLUMN language_tag;
        UPDATE dbo.catalog_metadata SET schema_version = 3 WHERE metadata_key = 1;
        """;

    const string MigrationName = "catalog-v4-text-authoring";

    const string UnknownVersions = "SELECT COUNT(*) FROM dbo.catalog_version WHERE text_snapshot_complete IS NULL;";

    static readonly DateTimeOffset T0 = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    static ContentTypeRegistry ThingRegistry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    [CatalogSqlServerFact]
    public Task A_version_1_catalog_migrates_to_four_keeping_every_row_and_adding_only_empty_text()
        => OlderVersionMigratesAsync(1);

    [CatalogSqlServerFact]
    public Task A_version_2_catalog_migrates_to_four_keeping_every_row_and_adding_only_empty_text()
        => OlderVersionMigratesAsync(2);

    [CatalogSqlServerFact]
    public Task A_version_3_catalog_migrates_to_four_keeping_every_row_and_adding_only_empty_text()
        => OlderVersionMigratesAsync(3);

    [CatalogSqlServerFact]
    public async Task ValidateOnly_refuses_version_3_naming_the_migration_and_writes_nothing()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteLegacy(database, 3);
        string before = Describe(database) + Dump(database, Columns(database, 3));

        foreach (ContentAuthoringSchemaMode mode in new[]
            { ContentAuthoringSchemaMode.ValidateOnly, ContentAuthoringSchemaMode.ValidateOnlyWithoutTypeSync })
        {
            var store = new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry());
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
                () => store.InitializeAsync(mode));
            Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
            Assert.Contains("at unsupported version '3'", refused.Message, StringComparison.Ordinal);
            Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, Describe(database) + Dump(database, Columns(database, 3)));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM sys.tables WHERE name = N'catalog_text';"));
    }

    /// <summary>
    /// The version 2 check an open runs after it read version 2. A genuine version 2 answers 2. Version 4
    /// objects under a version a rival host has since moved past 2 answer the version it moved to. The same
    /// objects under a version still at 2 are a mismatch.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task The_version_2_check_accepts_a_mismatch_only_when_another_host_already_moved_the_version_past_2()
    {
        using var database = new SqlServerCatalogDatabase();
        database.Execute(SqlServerCatalogSchema.VersionTwoSchemaSql);
        Assert.Equal(2, await ValidateVersionTwoAsync(database));

        database.DropSchema();
        await new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry())
            .InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        Assert.Equal(4, await ValidateVersionTwoAsync(database));

        database.Execute("UPDATE dbo.catalog_metadata SET schema_version = 2 WHERE metadata_key = 1;");
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => ValidateVersionTwoAsync(database));
        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task A_fresh_and_a_migrated_catalog_carry_the_same_columns_collations_indexes_and_checks()
    {
        using var database = new SqlServerCatalogDatabase();
        await new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry())
            .InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        string fresh = Describe(database);

        database.DropSchema();
        database.Execute(SqlServerCatalogSchema.VersionThreeSchemaSql);
        await new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry())
            .InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        Assert.Equal(4, database.Scalar("SELECT schema_version FROM dbo.catalog_metadata;"));

        Assert.Equal(fresh, Describe(database));
        Assert.Contains("catalog_text.string_value|nvarchar|-1|", fresh, StringComparison.Ordinal);
        Assert.Contains("|Latin1_General_100_BIN2", fresh, StringComparison.Ordinal);
        Assert.Contains("catalog_text.ux_catalog_text_live|unique|([replaced_in_version] IS NULL)", fresh, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task A_migrated_text_free_catalog_publishes_row_only_then_text_and_each_new_version_is_complete()
    {
        using var database = new SqlServerCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);

        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        Assert.Equal(2, database.Scalar(UnknownVersions));

        // The proof reads, it never records: the legacy versions stay unknown after being proven empty.
        ContentVersionTextSnapshot legacy = await text.ReadTextSnapshotAsync(2);
        Assert.Empty(legacy.Languages);
        Assert.Empty(legacy.Revisions);
        Assert.NotNull(await store.ExportBundleAsync(2));
        Assert.Equal(2, database.Scalar(UnknownVersions));

        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(7) }) }, Actor, Operator, "row only");
        Assert.Empty(await store.ListUpgradesAsync());

        // A stamped publish on the migrated store commits and writes its applied ledger row in that commit.
        ContentPublishRequest stamped = Request(2) with { Upgrade = new ContentUpgradeStamp("harvest-profiles", 1) };
        Assert.Equal(3, (await store.PublishAsync(stamped)).VersionNumber);
        ContentUpgradeRecord applied = Assert.Single(await store.ListUpgradesAsync());
        Assert.Equal(
            ("harvest-profiles", 1, ContentUpgradeDisposition.Applied, 3),
            (applied.Id, applied.Order, applied.Disposition, applied.VersionNumber));

        // A complete zero-language commit: recorded complete, and no language row fabricated for it.
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 3;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_text_chunk;"));
        ContentVersionTextSnapshot zero = await text.ReadTextSnapshotAsync(3);
        Assert.Empty(zero.Languages);
        Assert.Empty(zero.Revisions);

        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal(4, (await store.PublishAsync(Request(3))).VersionNumber);
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 4;"));
        Assert.Equal("Sword", Assert.Single((await text.ReadTextSnapshotAsync(4)).Revisions).Value);
        Assert.Equal(2, database.Scalar(UnknownVersions));
    }

    [CatalogSqlServerFact]
    public async Task Recorded_chunks_prove_an_empty_base_when_the_pack_lost_its_manifests_until_the_registry_moves()
    {
        using var database = new SqlServerCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);
        Directory.Delete(database.PackRoot, recursive: true);

        // A registry that gained a type no longer rebuilds the recorded manifests, so nothing proves the base.
        ContentTypeRegistry moved = TextRegistry();
        CatalogFixtures.Register(moved, CatalogFixtures.OtherSpec);
        SqlServerContentAuthoringStore drifted = await OpenAsync(database, registry: moved);
        var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ((IContentTextAuthoringStore)drifted).ReadTextSnapshotAsync(2));
        Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, unknown.Reason);

        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await ApplyAsync(store, null, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal(3, (await store.PublishAsync(Request(2))).VersionNumber);
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 3;"));
    }

    [CatalogSqlServerFact]
    public async Task An_unknown_base_whose_stored_manifests_name_a_language_refuses_before_any_write()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore seeded = await OpenAsync(database);
        await ApplyAsync(seeded, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await seeded.PublishAsync(Request(0));

        database.Execute(UndoVersionFour);
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        IContentTextAuthoringStore text = store;
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(7) }) }, Actor, Operator, "row only");
        int objects = Directory.GetFiles(database.PackRoot, "*", SearchOption.AllDirectories).Length;
        int audits = await AuditCountAsync(store);

        // No fabricated empty snapshot: the publish, the read, the export, a text apply and a legacy rollback
        // all refuse, and none of them records anything.
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
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"),
            () => text.RollbackTextToAsync(1, Actor, Operator, "rollback"),
        })
        {
            var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(read);
            Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, unknown.Reason);
        }

        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal(1, database.Scalar(UnknownVersions));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_text;"));
    }

    [CatalogSqlServerFact]
    public async Task A_migrated_text_free_catalog_rolls_back_to_a_pre_migration_version_and_publishes_complete()
    {
        using var database = new SqlServerCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);
        SqlServerContentAuthoringStore store = await OpenAsync(database);

        ContentDraft draft = await store.RollbackToAsync(1, Actor, Operator, "rollback");
        Assert.Equal(ContentEditOperation.Update, Assert.Single(draft.Changes.Edits).Operation);
        Assert.Equal(ContentAuditActions.Rollback, (await store.ListAuditAsync(default, 0, 0, 500))[0].Action);
        Assert.Equal(2, database.Scalar(UnknownVersions));

        Assert.Equal(3, (await store.PublishAsync(Request(2))).VersionNumber);
        Assert.Equal(1, database.Scalar("SELECT text_snapshot_complete FROM dbo.catalog_version WHERE version_number = 3;"));
        Assert.Equal(2, database.Scalar(UnknownVersions));
    }

    [CatalogSqlServerFact]
    public async Task A_migrated_catalog_whose_legacy_versions_are_not_provable_refuses_rollback_with_nothing_written()
    {
        using var database = new SqlServerCatalogDatabase();
        await WriteMigratedTextFreeAsync(database);
        Directory.Delete(database.PackRoot, recursive: true);
        ContentTypeRegistry moved = TextRegistry();
        CatalogFixtures.Register(moved, CatalogFixtures.OtherSpec);
        SqlServerContentAuthoringStore store = await OpenAsync(database, registry: moved);
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal(2, database.Scalar(UnknownVersions));
    }

    [CatalogSqlServerFact]
    public async Task Every_text_table_takes_its_times_from_the_one_operation_clock()
    {
        using var database = new SqlServerCatalogDatabase();
        DateTimeOffset now = T0;
        SqlServerContentAuthoringStore store = await OpenAsync(database, () => now);
        IContentTextAuthoringStore text = store;

        DateTimeOffset t1 = now = now.AddMinutes(1);
        await ApplyAsync(text, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        Assert.Equal((t1, t1), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_draft_text_edit;"));
        Assert.Equal(t1, Time(database, "SELECT created_at_utc FROM dbo.catalog_draft_text_language;"));

        DateTimeOffset t2 = now = now.AddMinutes(1);
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Blade"));
        Assert.Equal((t1, t2), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_draft_text_edit;"));

        DateTimeOffset t3 = now = now.AddMinutes(1);
        await store.PublishAsync(Request(0));
        Assert.Equal(t3, Time(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 1;"));
        Assert.Equal((t3, t3), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_text;"));
        Assert.Equal(t3, Time(database, "SELECT created_at_utc FROM dbo.catalog_text_chunk;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_draft_text_edit;"));

        DateTimeOffset t4 = now = now.AddMinutes(1);
        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "en"), "Sabre"));
        DateTimeOffset t5 = now = now.AddMinutes(1);
        await store.PublishAsync(Request(1));
        Assert.Equal((t3, t5), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_text WHERE valid_from_version = 1;"));
        Assert.Equal((t5, t5), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_text WHERE valid_from_version = 2;"));
        Assert.Equal(t5, Time(database, "SELECT created_at_utc FROM dbo.catalog_text_chunk WHERE version_number = 2;"));
        Assert.Equal(t4, Time(database, "SELECT occurred_at_utc FROM dbo.catalog_audit WHERE action = N'draft-edit' AND after_value = N'Sabre';"));
    }

    [CatalogSqlServerFact]
    public async Task A_whole_version_3_catalog_is_reset_without_force_and_comes_back_at_four()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await OpenAsync(database, registry: ThingRegistry());
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
        database.Execute(UndoVersionFour);

        ContentCatalogResetResult reset = await SqlServerCatalogReset.ResetAsync(
            database.ConnectionString, Actor, Operator, "content release");

        Assert.Equal(ContentCatalogPriorState.Read, reset.PriorState);
        Assert.Equal(3, reset.PriorSchemaVersion);
        Assert.Equal(4, reset.SchemaVersion);
        Assert.Equal(2, reset.RowsDropped);
        await new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry())
            .InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    async Task OlderVersionMigratesAsync(int version)
    {
        using var database = new SqlServerCatalogDatabase();
        WriteLegacy(database, version);
        IReadOnlyDictionary<string, IReadOnlyList<string>> legacyColumns = Columns(database, version);
        string before = Dump(database, legacyColumns);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry(), database.Pack());
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

        // Every legacy value of every legacy column, the earlier migrations' backfill included, is where it was.
        // The type row is the one exception: AutoCreate syncs the registry's declaration into it after migrating.
        Assert.Equal(before, Dump(database, legacyColumns));

        AssertNewTablesEmptyAndLegacyUnknown(database);
        await new SqlServerContentAuthoringStore(database.ConnectionString, ThingRegistry())
            .InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    /// <summary>
    /// A catalog this build published two row-only versions into, taken back to version 3 and left for the next
    /// AutoCreate open to migrate: exactly the shape of a deployed text-free catalog.
    /// </summary>
    internal static async Task WriteMigratedTextFreeAsync(SqlServerCatalogDatabase database)
    {
        SqlServerContentAuthoringStore store = await OpenAsync(database);
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "first");
        await store.PublishAsync(Request(0));
        await RepriceAndPublishAsync(store, 5);

        database.Execute(UndoVersionFour);
        SqlServerContentAuthoringStore migrated = await OpenAsync(database);
        Assert.Equal(4, await migrated.GetSchemaVersionAsync());
        AssertNewTablesEmptyAndLegacyUnknown(database);
    }

    /// <summary>The version 2 check on a connection of its own, as an open that read version 2 runs it.</summary>
    static async Task<int> ValidateVersionTwoAsync(SqlServerCatalogDatabase database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        return await SqlServerCatalogSchemaValidation.ValidateVersionTwoAsync(connection, default);
    }

    /// <summary>One older version's embedded script and the legacy content written into it.</summary>
    static void WriteLegacy(SqlServerCatalogDatabase database, int version)
    {
        database.Execute(version switch
        {
            1 => SqlServerCatalogSchema.VersionOneSchemaSql,
            2 => SqlServerCatalogSchema.VersionTwoSchemaSql,
            _ => SqlServerCatalogSchema.VersionThreeSchemaSql,
        });
        database.Execute(SqlServerCatalogRowTimestampTests.PopulateLegacy);
        if (version >= 2)
        {
            database.Execute(SqlServerCatalogRowTimestampTests.PopulateLedger);
        }

        if (version == 3)
        {
            // Times a version 3 catalog already proved, which the version 4 migration may not touch.
            database.Execute(
                """
                UPDATE dbo.catalog_metadata SET created_at_utc = '2025-12-01T00:00:00+00:00';
                UPDATE dbo.catalog_family SET created_at_utc = '2025-12-02T00:00:00+00:00';
                UPDATE dbo.catalog_row SET created_at_utc = '2026-01-01T00:00:00+00:00';
                UPDATE dbo.catalog_draft_edit SET created_at_utc = '2026-01-03T00:00:00+00:00';
                """);
        }

        Assert.Equal(version, database.Scalar("SELECT schema_version FROM dbo.catalog_metadata;"));
    }

    static void AssertNewTablesEmptyAndLegacyUnknown(SqlServerCatalogDatabase database)
    {
        foreach (string table in SqlServerCatalogSchema.VersionFourTables)
        {
            Assert.Equal(0, database.Scalar($"SELECT COUNT(*) FROM dbo.{table};"));
        }

        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_version WHERE text_snapshot_complete IS NOT NULL;"));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_audit WHERE language_tag IS NOT NULL;"));
    }

    /// <summary>
    /// The columns of every table the given version declared, in column order. The metadata row's version and
    /// update time are the migration's to change, and the type row is the registry sync's, so they are left out.
    /// </summary>
    static IReadOnlyDictionary<string, IReadOnlyList<string>> Columns(SqlServerCatalogDatabase database, int version)
    {
        var tables = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (string table in SqlServerCatalogSchemaExpectations.TablesFor(version))
        {
            if (table == "catalog_type")
            {
                continue;
            }

            var columns = new List<string>();
            foreach (string column in Lines(database, $"SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') ORDER BY column_id;"))
            {
                if (table != "catalog_metadata" || column is not ("schema_version" or "updated_at_utc"))
                {
                    columns.Add(column);
                }
            }

            tables.Add(table, columns);
        }

        return tables;
    }

    /// <summary>Every row of every given table over the given columns, as sorted text lines.</summary>
    static string Dump(SqlServerCatalogDatabase database, IReadOnlyDictionary<string, IReadOnlyList<string>> columns)
    {
        var dump = new List<string>();
        foreach ((string table, IReadOnlyList<string> held) in columns)
        {
            using var connection = new SqlConnection(database.ConnectionString);
            connection.Open();
            using SqlCommand select = connection.CreateCommand();
            select.CommandText = $"SELECT {string.Join(", ", held.Select(static column => "[" + column + "]"))} FROM dbo.{table};";
            var rows = new List<string>();
            using SqlDataReader reader = select.ExecuteReader();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i)
                        ? "NULL"
                        : reader.GetValue(i) switch
                        {
                            byte[] bytes => Convert.ToHexString(bytes),
                            DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
                            object value => Convert.ToString(value, CultureInfo.InvariantCulture)!,
                        };
                }

                rows.Add(string.Join("|", values));
            }

            rows.Sort(StringComparer.Ordinal);
            dump.Add(table + ":\n" + string.Join("\n", rows));
        }

        return string.Join("\n", dump);
    }

    /// <summary>
    /// Every catalog column with its type, length, scale, nullability, collation and identity in column order,
    /// every named index with its uniqueness, filter and key columns, every check with its definition, and every
    /// foreign key and default with its definition: the schema as the database holds it.
    /// </summary>
    static string Describe(SqlServerCatalogDatabase database)
        => string.Join("\n", Lines(
            database,
            """
            SELECT t.name + N'.' + c.name + N'|' + ty.name + N'|' + CONVERT(nvarchar(8), c.max_length) + N'|'
                + CONVERT(nvarchar(4), c.scale) + N'|' + CASE c.is_nullable WHEN 1 THEN N'NULL' ELSE N'NOT NULL' END
                + N'|' + COALESCE(c.collation_name, N'') + N'|' + CONVERT(nvarchar(1), c.is_identity)
                + N'|' + CONVERT(nvarchar(4), c.column_id)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%'
            UNION ALL
            SELECT t.name + N'.' + i.name + N'|' + CASE i.is_unique WHEN 1 THEN N'unique' ELSE N'plain' END + N'|'
                + COALESCE(i.filter_definition, N'') + N'|'
                + STUFF((SELECT N',' + col.name FROM sys.index_columns ic
                         JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                         ORDER BY ic.key_ordinal FOR XML PATH(N'')), 1, 1, N'')
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%' AND i.name IS NOT NULL
            UNION ALL
            SELECT t.name + N'.' + k.name + N'|' + k.definition
            FROM sys.check_constraints k JOIN sys.tables t ON t.object_id = k.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%'
            UNION ALL
            SELECT t.name + N'.' + f.name + N'|' + OBJECT_NAME(f.referenced_object_id)
            FROM sys.foreign_keys f JOIN sys.tables t ON t.object_id = f.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%'
            UNION ALL
            SELECT t.name + N'.' + d.name + N'|' + d.definition
            FROM sys.default_constraints d JOIN sys.tables t ON t.object_id = d.parent_object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo') AND t.name LIKE N'catalog[_]%';
            """).Order(StringComparer.Ordinal));

    static List<string> Lines(SqlServerCatalogDatabase database, string sql)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var lines = new List<string>();
        using SqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            lines.Add(reader.GetString(0));
        }

        return lines;
    }

    static DateTimeOffset Time(SqlServerCatalogDatabase database, string sql) => Pair(database, sql, 1).Item1;

    static (DateTimeOffset, DateTimeOffset) Pair(SqlServerCatalogDatabase database, string sql, int columns = 2)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqlDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        (DateTimeOffset, DateTimeOffset) pair = (
            reader.GetDateTimeOffset(0), columns < 2 ? default : reader.GetDateTimeOffset(1));
        Assert.False(reader.Read());
        return pair;
    }
}
