using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Schema version 3 of the SQL Server content catalog, the SQLite facts' twin: every row records when it was
/// created, every row that changes after insert records when it last changed, and the store writes both from its
/// own clock. An update time moves only when a statement changes a value in the row.
/// <para>
/// ENV GATED on <c>KE_CATALOG_SQLSERVER</c> like every other class here, and in the same serialized collection,
/// because each fact starts by dropping the catalog schema of the one test database.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed partial class SqlServerCatalogRowTimestampTests
{
    const string MigrationName = "catalog-v4-text-authoring";
    const string Actor = "sqlserver-row-timestamps";
    const string Operator = "oid:tests";

    static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every column version 3 adds, which is exactly what the migration has to add.</summary>
    static readonly (string Table, string Column)[] NewColumns =
    [
        ("catalog_metadata", "created_at_utc"),
        ("catalog_type", "created_at_utc"),
        ("catalog_type", "updated_at_utc"),
        ("catalog_family", "created_at_utc"),
        ("catalog_family_block", "created_at_utc"),
        ("catalog_family_block", "updated_at_utc"),
        ("catalog_row", "created_at_utc"),
        ("catalog_row", "updated_at_utc"),
        ("catalog_row_field", "created_at_utc"),
        ("catalog_id_high_water", "created_at_utc"),
        ("catalog_id_high_water", "updated_at_utc"),
        ("catalog_draft", "updated_at_utc"),
        ("catalog_draft_edit", "created_at_utc"),
        ("catalog_draft_edit_field", "created_at_utc"),
        ("catalog_remap_rule", "created_at_utc"),
        ("catalog_chunk", "created_at_utc"),
    ];

    /// <summary>Every catalog table and the column that holds its creation time.</summary>
    static readonly (string Table, string Column)[] CreationTimes =
    [
        ("catalog_audit", "occurred_at_utc"),
        ("catalog_chunk", "created_at_utc"),
        ("catalog_content_upgrade", "recorded_at_utc"),
        ("catalog_draft", "opened_at_utc"),
        ("catalog_draft_edit", "created_at_utc"),
        ("catalog_draft_edit_field", "created_at_utc"),
        ("catalog_draft_text_edit", "created_at_utc"),
        ("catalog_draft_text_language", "created_at_utc"),
        ("catalog_family", "created_at_utc"),
        ("catalog_family_block", "created_at_utc"),
        ("catalog_id_high_water", "created_at_utc"),
        ("catalog_metadata", "created_at_utc"),
        ("catalog_remap_rule", "created_at_utc"),
        ("catalog_row", "created_at_utc"),
        ("catalog_row_field", "created_at_utc"),
        ("catalog_text", "created_at_utc"),
        ("catalog_text_chunk", "created_at_utc"),
        ("catalog_type", "created_at_utc"),
        ("catalog_version", "published_at_utc"),
    ];

    /// <summary>Every catalog table whose rows change after insert and the column that holds that time.</summary>
    static readonly (string Table, string Column)[] UpdateTimes =
    [
        ("catalog_draft", "updated_at_utc"),
        ("catalog_draft_edit", "edited_at_utc"),
        ("catalog_draft_text_edit", "updated_at_utc"),
        ("catalog_family_block", "updated_at_utc"),
        ("catalog_id_high_water", "updated_at_utc"),
        ("catalog_metadata", "updated_at_utc"),
        ("catalog_row", "updated_at_utc"),
        ("catalog_text", "updated_at_utc"),
        ("catalog_type", "updated_at_utc"),
    ];

    /// <summary>
    /// The version 4 text tables, which the row-only write paths here leave empty. Every time column of them is
    /// NOT NULL, and <c>SqlServerTextSchemaMigrationTests</c> proves the times every text write path stamps.
    /// </summary>
    static readonly string[] TextTables =
        ["catalog_draft_text_edit", "catalog_draft_text_language", "catalog_text", "catalog_text_chunk"];

    static ContentTypeId Thing => CatalogFixtures.Thing;

    static ContentTypeRegistry Registry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "row timestamps", expectedBaseVersion);

    [CatalogSqlServerFact]
    public async Task Fresh_catalog_carries_every_version_3_nullable_timestamp_column()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(SqlServerCatalogSchema.CurrentVersion, await store.GetSchemaVersionAsync());
        foreach ((string table, string column) in NewColumns)
        {
            Assert.Equal("datetimeoffset(7) NULL", Text(database, $"""
                SELECT CONCAT(ty.name, N'(', c.scale, N') ', CASE c.is_nullable WHEN 1 THEN N'NULL' ELSE N'NOT NULL' END)
                FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'dbo.{table}') AND c.name = N'{column}';
                """));
        }

        // The migration adds exactly these columns and no others.
        Assert.Equal(NewColumns, SqlServerCatalogSchema.VersionThreeColumns);
    }

    [CatalogSqlServerFact]
    public async Task Every_catalog_write_path_stamps_its_rows()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentBundle bundle = await SourceBundleAsync(database, withFamily: true);
        var clock = new ManualClock(T0);
        ContentTypeRegistry registry = Registry();
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, registry, database.Pack(), clock.Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        DateTimeOffset t0 = clock.Now;
        DateTimeOffset? metadataCreated = Value(database, "SELECT created_at_utc FROM dbo.catalog_metadata;");
        Assert.NotNull(metadataCreated);
        Assert.Equal(Value(database, "SELECT updated_at_utc FROM dbo.catalog_metadata;"), metadataCreated);
        Assert.Equal((t0, t0), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_type;"));

        // The import stages the families and the id marks, then publishes version 1.
        DateTimeOffset t1 = clock.Tick();
        await store.ImportBundleAsync(bundle, Actor, Operator, "seed");
        Assert.Equal(t1, Value(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 1;"));
        AssertEvery(database, "catalog_family", "1 = 1", t1, "created_at_utc");
        AssertEvery(database, "catalog_family_block", "1 = 1", t1, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_id_high_water", "1 = 1", t1, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row", "1 = 1", t1, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row_field", "1 = 1", t1, "created_at_utc");
        AssertEvery(database, "catalog_remap_rule", "1 = 1", t1, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "1 = 1", t1, "created_at_utc");

        // A draft opened with two edits.
        DateTimeOffset t2 = clock.Tick();
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("three"), CatalogFixtures.Fields(33)),
                ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(12)),
            ],
            Actor,
            Operator,
            "first");
        Assert.Equal((t2, t2), DraftTimes(database));
        AssertEvery(database, "catalog_draft_edit", "1 = 1", t2, "created_at_utc", "edited_at_utc");
        AssertEvery(database, "catalog_draft_edit_field", "1 = 1", t2, "created_at_utc");

        // The same row edited again under a new note. The edit keeps its creation time and its field rows are
        // new rows.
        DateTimeOffset t3 = clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(13))],
            Actor,
            Operator,
            "second");
        Assert.Equal((t2, t3), DraftTimes(database));
        Assert.Equal((t2, t3), EditTimes(database, "one"));
        Assert.Equal((t2, t2), EditTimes(database, "three"));
        AssertEvery(database, "catalog_draft_edit_field", EditFieldOf("one"), t3, "created_at_utc");
        AssertEvery(database, "catalog_draft_edit_field", EditFieldOf("three"), t2, "created_at_utc");

        // A family and its first block, then an id issued out of the block.
        DateTimeOffset t4 = clock.Tick();
        ContentFamily shields = await store.CreateFamilyAsync(Thing, "shields", 16, Actor, Operator);
        string ofShields = FormattableString.Invariant($"family_id = {shields.FamilyId}");
        AssertEvery(database, "catalog_family", ofShields, t4, "created_at_utc");
        Assert.Equal((t4, t4), Pair(database, $"SELECT created_at_utc, updated_at_utc FROM dbo.catalog_family_block WHERE {ofShields};"));
        Assert.Equal((t1, t4), HighWaterTimes(database));
        DateTimeOffset t5 = clock.Tick();
        await store.AllocateInFamilyAsync(shields.FamilyId);
        Assert.Equal((t4, t5), Pair(database, $"SELECT created_at_utc, updated_at_utc FROM dbo.catalog_family_block WHERE {ofShields};"));

        // The freeze and its release are changes to the draft row, and a pin moves only the metadata update.
        DateTimeOffset t6 = clock.Tick();
        await store.FreezeDraftAsync(1);
        Assert.Equal((t2, t6), DraftTimes(database));
        DateTimeOffset t7 = clock.Tick();
        await store.ClearDraftFreezeAsync();
        Assert.Equal((t2, t7), DraftTimes(database));

        // A marker naming a base the catalog no longer stands at is a dead publish's leftover, and the next
        // baseline read clears it.
        clock.Tick();
        await store.FreezeDraftAsync(0);
        DateTimeOffset cleared = clock.Tick();
        await store.ReadPublishBaselineAsync();
        Assert.Equal(0, Count(database, "SELECT COUNT(*) FROM dbo.catalog_draft WHERE frozen_for_base_version IS NOT NULL;"));
        Assert.Equal((t2, cleared), DraftTimes(database));
        DateTimeOffset t8 = clock.Tick();
        await store.SetPinnedVersionAsync(1, Actor, Operator);
        Assert.Equal((metadataCreated, t8), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_metadata;"));

        // Version 2 closes one revision, inserts two and issues the id the added row takes.
        DateTimeOffset t9 = clock.Tick();
        await store.PublishAsync(Request(1));
        Assert.Equal(t9, Value(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 2;"));
        Assert.Equal((t1, t9), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_row WHERE definition_id = 1 AND valid_from_version = 1;"));
        AssertEvery(database, "catalog_row", "valid_from_version = 2", t9, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row", "definition_id = 2", t1, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 2", t9, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 2", t9, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 1", t1, "created_at_utc");
        Assert.Equal((t1, t9), HighWaterTimes(database));
        Assert.Equal((metadataCreated, t9), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_metadata;"));
        Assert.Equal(0, Count(database, "SELECT COUNT(*) FROM dbo.catalog_draft;"));

        // A publish whose draft outlives it. An edit that arrived after the freeze was released is carried
        // forward, and the rebase is a change to the draft row at the publish time.
        DateTimeOffset t10 = clock.Tick();
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(15))],
            Actor,
            Operator,
            "third");
        DateTimeOffset t11 = clock.Tick();
        var publisher = new ContentPublisher(store, store, registry);
        ContentPublishPlan plan = await publisher.PrepareAsync(Request(2), await store.ReadPublishBaselineAsync());
        Assert.True(plan.IsValid);
        Assert.Equal((t10, t11), DraftTimes(database));
        DateTimeOffset t12 = clock.Tick();
        await store.ClearDraftFreezeAsync();
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("four"), CatalogFixtures.Fields(44))],
            Actor,
            Operator,
            string.Empty);
        DateTimeOffset t13 = clock.Tick();
        await store.CommitPublishAsync(plan, Request(2), null);
        Assert.Equal(3, Count(database, "SELECT base_version FROM dbo.catalog_draft;"));
        Assert.Equal((t10, t13), DraftTimes(database));
        Assert.Equal((t12, t12), EditTimes(database, "four"));

        await store.RecordUpgradeAsync(
            new ContentUpgradeStamp("row-timestamps", 1), ContentUpgradeDisposition.Baseline, Actor, Operator);

        AssertEveryRowCarriesItsTimes(database);

        // A changed type declaration is a change to its row.
        DateTimeOffset t14 = clock.Tick();
        var capped = new SqlServerContentAuthoringStore(
            database.ConnectionString,
            CatalogFixtures.Registry(CatalogFixtures.ThingSpec with { MaxDefinitionId = 1000 }),
            database.Pack(),
            clock.Read);
        await capped.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal((t0, t14), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_type;"));
    }

    /// <summary>
    /// Every row a publish writes carries its version's <c>published_at_utc</c> exactly, which is also what the
    /// migration fills a legacy row with. The clock moves on every read, so a second reading for any of those rows
    /// would show.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task A_publish_stamps_every_row_it_writes_with_the_version_publish_time()
    {
        using var database = new SqlServerCatalogDatabase();
        DateTimeOffset now = T0;
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), () => now = now.AddMilliseconds(1));
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await store.PublishAsync(Request(0));
        await store.ApplyEditsAsync(
            [
                ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(12)),
                ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0),
            ],
            Actor,
            Operator,
            "revise and retire");
        await store.PublishAsync(Request(1));

        DateTimeOffset first = Value(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 1;")!.Value;
        DateTimeOffset second = Value(database, "SELECT published_at_utc FROM dbo.catalog_version WHERE version_number = 2;")!.Value;
        Assert.Equal((first, second), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_row WHERE definition_id = 1 AND valid_from_version = 1;"));
        AssertEvery(database, "catalog_row", "valid_from_version = 2", second, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 1", first, "created_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 2", second, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 1", first, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 2", second, "created_at_utc");
        AssertEvery(database, "catalog_remap_rule", "introduced_in = 2", second, "created_at_utc");
    }

    [CatalogSqlServerFact]
    public async Task Writes_that_change_nothing_leave_the_update_time_alone()
    {
        using var database = new SqlServerCatalogDatabase();
        ContentBundle bundle = await SourceBundleAsync(database, withFamily: false);
        var clock = new ManualClock(T0);
        DateTimeOffset t0 = clock.Now;
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.AllocateAsync(Thing, 5);
        Assert.Equal((t0, t0), HighWaterTimes(database));

        clock.Tick();
        var reopened = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal((t0, t0), Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_type;"));

        // The bundle's ids sit below both marks, so seeding them raises nothing.
        clock.Tick();
        await reopened.ImportBundleAsync(bundle, Actor, Operator, "seed");
        Assert.Equal((t0, t0), HighWaterTimes(database));

        DateTimeOffset t3 = clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(12))], Actor, Operator, "note");
        Assert.Equal((t3, t3), DraftTimes(database));
        clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(13))], Actor, Operator, string.Empty);
        clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(14))], Actor, Operator, "note");
        Assert.Equal((t3, t3), DraftTimes(database));

        // A note that differs only in trailing blanks is a different value, which SQL Server's padded
        // comparison alone would call equal.
        DateTimeOffset t6 = clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), CatalogFixtures.Fields(15))], Actor, Operator, "note ");
        Assert.Equal((t3, t6), DraftTimes(database));
        Assert.Equal("note ", Text(database, "SELECT note FROM dbo.catalog_draft;"));

        DateTimeOffset t7 = clock.Tick();
        await reopened.FreezeDraftAsync(1);
        clock.Tick();
        await reopened.FreezeDraftAsync(1);
        Assert.Equal((t3, t7), DraftTimes(database));

        DateTimeOffset t9 = clock.Tick();
        await reopened.ClearDraftFreezeAsync();
        clock.Tick();
        await reopened.ClearDraftFreezeAsync();
        Assert.Equal((t3, t9), DraftTimes(database));
    }

    /// <summary>
    /// A bundle exported from a store on this same database, which is then dropped so the fact starts from an
    /// empty one: two rows, one of them retired so a remap rule rides along, and optionally a family with one
    /// block. The source publishes into a pack root of its own, so its version pointers never meet the fact's.
    /// </summary>
    static async Task<ContentBundle> SourceBundleAsync(SqlServerCatalogDatabase database, bool withFamily)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), new FileSystemPackStore(Path.Combine(database.Root, "source-pack")));
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await store.PublishAsync(Request(0));
        if (withFamily)
        {
            await store.CreateFamilyAsync(Thing, "swords", 16, Actor, Operator);
        }

        await store.ApplyEditsAsync(
            [ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0)],
            Actor,
            Operator,
            "retire two");
        await store.PublishAsync(Request(1));
        ContentBundle bundle = await store.ExportBundleAsync(2);
        database.DropSchema();
        return bundle;
    }

    /// <summary>
    /// Every catalog table but the text tables holds rows, every row carries its creation time, and every row of a
    /// table that changes after insert carries its update time.
    /// </summary>
    static void AssertEveryRowCarriesItsTimes(SqlServerCatalogDatabase database)
    {
        Assert.Equal(
            CreationTimes.Select(static value => value.Table),
            SqlServerCatalogResetHarness.Tables(database).OrderBy(static value => value, StringComparer.Ordinal));
        foreach ((string table, string column) in CreationTimes.Concat(UpdateTimes))
        {
            if (TextTables.Contains(table))
            {
                continue;
            }

            Assert.True(Count(database, $"SELECT COUNT(*) FROM dbo.{table};") > 0, $"{table} has no row to prove");
            Assert.True(
                Count(database, $"SELECT COUNT(*) FROM dbo.{table} WHERE {column} IS NULL;") == 0,
                $"{table}.{column} is NULL on a row this build wrote");
        }
    }

    /// <summary>Every row of a table matching a filter, holding one time in each named column.</summary>
    static void AssertEvery(
        SqlServerCatalogDatabase database,
        string table,
        string where,
        DateTimeOffset expected,
        params string[] columns)
    {
        Assert.True(Count(database, $"SELECT COUNT(*) FROM dbo.{table} WHERE {where};") > 0, $"{table} has no row where {where}");
        foreach (string column in columns)
        {
            int wrong = Count(
                database,
                $"SELECT COUNT(*) FROM dbo.{table} WHERE {where} AND ({column} IS NULL OR {column} <> @expected);",
                expected);
            Assert.True(wrong == 0, $"{wrong} row(s) of {table} where {where} do not carry {column} = {expected:O}");
        }
    }

    static string EditFieldOf(string key)
        => $"edit_ordinal IN (SELECT edit_ordinal FROM dbo.catalog_draft_edit WHERE content_key = N'{key}')";

    static (DateTimeOffset?, DateTimeOffset?) DraftTimes(SqlServerCatalogDatabase database)
        => Pair(database, "SELECT opened_at_utc, updated_at_utc FROM dbo.catalog_draft;");

    static (DateTimeOffset?, DateTimeOffset?) EditTimes(SqlServerCatalogDatabase database, string key)
        => Pair(database, $"SELECT created_at_utc, edited_at_utc FROM dbo.catalog_draft_edit WHERE content_key = N'{key}';");

    static (DateTimeOffset?, DateTimeOffset?) HighWaterTimes(SqlServerCatalogDatabase database)
        => Pair(database, "SELECT created_at_utc, updated_at_utc FROM dbo.catalog_id_high_water;");

    /// <summary>One nullable time on a raw connection.</summary>
    static DateTimeOffset? Value(SqlServerCatalogDatabase database, string sql) => Read(database, sql, 1).Item1;

    /// <summary>The two nullable times of the ONE row a statement returns.</summary>
    static (DateTimeOffset?, DateTimeOffset?) Pair(SqlServerCatalogDatabase database, string sql)
        => Read(database, sql, 2);

    static (DateTimeOffset?, DateTimeOffset?) Read(SqlServerCatalogDatabase database, string sql, int columns)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<(DateTimeOffset?, DateTimeOffset?)>();
        using (SqlDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                values.Add((
                    reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
                    columns < 2 || reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1)));
            }
        }

        return Assert.Single(values);
    }

    /// <summary>One integer on a raw connection, with an optional time bound as <c>@expected</c>.</summary>
    static int Count(SqlServerCatalogDatabase database, string sql, DateTimeOffset? expected = null)
    {
        using var connection = new SqlConnection(database.ConnectionString);
        connection.Open();
        using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (expected is DateTimeOffset value)
        {
            SqlParameter parameter = command.Parameters.Add("@expected", SqlDbType.DateTimeOffset);
            parameter.Scale = 7;
            parameter.Value = value;
        }

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One text value on a raw connection.</summary>
    static string Text(SqlServerCatalogDatabase database, string sql)
        => SqlServerCatalogResetHarness.Text(database, sql);

    /// <summary>The store's clock, moved one minute at a time by the test.</summary>
    sealed class ManualClock(DateTimeOffset start)
    {
        DateTimeOffset _now = start;

        public Func<DateTimeOffset> Read => () => _now;

        public DateTimeOffset Now => _now;

        public DateTimeOffset Tick()
        {
            _now = _now.AddMinutes(1);
            return _now;
        }
    }
}
